using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PKHeX.Core;

namespace PKHeX.Avalonia;

public partial class MainWindow : Window
{
    private SaveFile? SAV;

    /// <summary> Entity currently shown in the editor. </summary>
    private PKM Current = FakeSaveFile.Default.BlankPKM;

    /// <summary> Selected storage slot: party (true) or box (false), box index, slot index. </summary>
    private (bool Party, int Box, int Slot)? Selected;

    private readonly List<Button> BoxSlots = [];
    private readonly List<Button> PartySlots = [];
    private readonly NumericUpDown[] IVs = new NumericUpDown[6];
    private readonly NumericUpDown[] EVs = new NumericUpDown[6];
    private readonly TextBlock[] StatValues = new TextBlock[6];
    private readonly TextBlock[] StatLabels = new TextBlock[6];
    private ComboBox[] MoveBoxes = [];

    private bool Loading;
    private int LoadedAbilityIndex = -1;

    private static readonly string[] StatNames = ["HP", "Attack", "Defense", "Speed", "Sp. Atk", "Sp. Def"];

    public MainWindow()
    {
        InitializeComponent();
        if (Sprites.AppIcon is { } icon)
            Icon = new WindowIcon(icon);
        MoveBoxes = [CB_Move1, CB_Move2, CB_Move3, CB_Move4];
        BuildStatGrid();
        BuildDetailTabs();
        InitEditorExtras();

        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        KeyDown += OnKeyDown;

        PopulateFilteredSources();
        LoadEditor(Current);
        Opened += async (_, _) => await CheckForUpdatesQuietly();
    }

    #region Messages

    private Task ShowMessage(string title, string message) => Ui.ShowMessage(this, title, message);

    private Task<bool> Confirm(string title, string message) => Ui.Confirm(this, title, message);

    private void SetStatus(string message) => StatusText.Text = message;

    #endregion

    #region File handling

    private static readonly FilePickerFileType AnyFile = new("All Files") { Patterns = ["*"] };

    /// <summary> Common save file names: .sav, RetroArch .srm, DeSmuME .dsv, Dolphin .gci, flash .fla, BizHawk .SaveRAM, Switch/3DS "main", … </summary>
    private static readonly FilePickerFileType SaveFiles = new("Save files (.sav, .srm, .dsv, .gci, …)")
    {
        Patterns = ["*.sav", "*.srm", "*.dsv", "*.gci", "*.fla", "*.dat", "*.bin", "*.SaveRAM", "*.raw", "*.bak", "main"],
    };

    private static readonly FilePickerFileType SavExtension = new("Save file (.sav)") { Patterns = ["*.sav"] };
    private static readonly FilePickerFileType SrmExtension = new("RetroArch save file (.srm)") { Patterns = ["*.srm"] };

    private async void Menu_Open(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open save file or Pokémon file",
            AllowMultiple = false,
            FileTypeFilter = [SaveFiles, AnyFile],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null)
            await OpenFileAsync(path);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var path = e.DataTransfer.TryGetFile()?.TryGetLocalPath();
        if (path is not null)
            await OpenFileAsync(path);
    }

    public async void OpenFile(string path) => await OpenFileAsync(path);

    private async Task OpenFileAsync(string path)
    {
        if (!File.Exists(path))
        {
            await ShowMessage("Open", $"File not found:\n{path}");
            return;
        }

        var len = new FileInfo(path).Length;
        if (FileUtil.IsFileTooBig(len) || FileUtil.IsFileTooSmall(len))
        {
            await ShowMessage("Open", "The file size is not supported.");
            return;
        }

        object? obj;
        try
        {
            obj = FileUtil.GetSupportedFile(path, SAV);
        }
        catch (Exception ex)
        {
            await ShowMessage("Open", $"Unable to read the file:\n{ex.Message}");
            return;
        }

        switch (obj)
        {
            case SaveFile sav:
                if (SAV?.State.Edited == true && !await Confirm("Open", "The current save file has unsaved changes. Discard them?"))
                    return;
                sav.Metadata.SetExtraInfo(path);
                LoadSave(sav);
                break;
            case PKM pk:
                await ImportEntity(pk);
                break;
            case MysteryGift { IsEntity: true } mg when SAV is not null:
                await ImportEntity(mg.ConvertToPKM(SAV));
                break;
            case IPokeGroup group when SAV is not null:
                await ImportGroup(group);
                break;
            default:
                await ShowMessage("Open", "This file is not a recognized save file or Pokémon file.");
                break;
        }
    }

    private async void Menu_Save(object? sender, RoutedEventArgs e)
    {
        if (SAV is null)
            return;
        var path = SAV.Metadata.FilePath;
        if (path is null || Path.GetExtension(path) is ".zip")
        {
            await SaveAs();
            return;
        }
        await ExportSave(path);
    }

    private async void Menu_SaveAs(object? sender, RoutedEventArgs e) => await SaveAs();

    private async Task SaveAs()
    {
        if (SAV is null)
            return;
        var meta = SAV.Metadata;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save as",
            SuggestedFileName = meta.FileName ?? $"save{SAV.Extension}",
            SuggestedStartLocation = meta.FileFolder is { } dir ? await StorageProvider.TryGetFolderFromPathAsync(dir) : null,
            ShowOverwritePrompt = true,
            FileTypeChoices = IsSrm(meta.FileName) ? [SrmExtension, SavExtension, AnyFile] : [SavExtension, SrmExtension, AnyFile],
        });
        var path = file?.TryGetLocalPath();
        if (path is not null)
            await ExportSave(path);
    }

    private async Task ExportSave(string path)
    {
        if (SAV is null)
            return;
        try
        {
            // Keep a one-time backup of the original file next to it.
            var bak = path + ".bak";
            if (File.Exists(path) && !File.Exists(bak))
                File.Copy(path, bak);

            var flags = SAV.Metadata.GetSuggestedFlags(Path.GetExtension(path.AsSpan()));
            // A RetroArch .srm with extra trailing data, saved as something else: drop the trailer.
            if (SAV.Metadata.HasFooter && IsSrm(SAV.Metadata.FileName) && !IsSrm(path))
                flags |= BinaryExportSetting.ExcludeFooter;
            var data = SAV.Write(flags);
            await File.WriteAllBytesAsync(path, data);
            SAV.State.Edited = false;
            SAV.Metadata.SetExtraInfo(path);
            UpdateTitle();
            SetStatus($"Saved to {path}");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            await ShowMessage("Save", $"Unable to write the file:\n{ex.Message}");
        }
    }

    private static bool IsSrm(string? fileName) => fileName?.EndsWith(".srm", StringComparison.OrdinalIgnoreCase) == true;

    private async void Menu_Exit(object? sender, RoutedEventArgs e)
    {
        if (SAV?.State.Edited == true && !await Confirm("Exit", "The current save file has unsaved changes. Exit anyway?"))
            return;
        Close();
    }

    private async void Menu_About(object? sender, RoutedEventArgs e)
    {
        await ShowMessage("About", $"PKHeX (Avalonia) — a cross-platform front end for PKHeX.Core.\nVersion: {Updater.VersionText}\n\n" +
                                    "Runs natively on Linux (Ubuntu), macOS and Windows.\n\n" +
                                    "PKHeX is developed by Kaphotics and contributors, licensed under GPLv3.");
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;
        switch (e.Key)
        {
            case Key.O: Menu_Open(this, e); break;
            case Key.S when e.KeyModifiers.HasFlag(KeyModifiers.Shift): await SaveAs(); break;
            case Key.S: Menu_Save(this, e); break;
            case Key.L: Menu_Legality(this, e); break;
            default: return;
        }
        e.Handled = true;
    }

    #endregion

    #region Save file

    private void LoadSave(SaveFile sav)
    {
        SAV = sav;
        ParseSettings.InitFromSaveFileData(sav);
        RecentTrainerCache.SetRecentTrainer(sav);
        GameInfo.FilteredSources = new FilteredGameDataSource(sav, GameInfo.Sources);
        PopulateFilteredSources();

        MenuSave.IsEnabled = MenuSaveAs.IsEnabled = sav.State.Exportable;
        UpdateTitle();

        RefreshSaveInfo();
        UpdateToolMenus(sav);

        BuildBoxSlots();
        BuildPartySlots();

        Selected = null;
        LoadEditor(sav.LoadTemplate());
        StorageTabs.SelectedIndex = sav.HasBox ? 0 : 1;
        SetStatus($"Loaded {sav.Metadata.FileName ?? "save file"}.");
    }

    private void UpdateTitle()
    {
        if (SAV is null)
        {
            Title = "PKHeX (Avalonia)";
            SaveTitle.Text = "No save file loaded";
            return;
        }
        var name = SAV.Metadata.FileName ?? "Untitled";
        Title = $"PKHeX (Avalonia) — {name}";
        SaveTitle.Text = name;
    }

    private void BuildBoxSlots()
    {
        BoxGrid.Children.Clear();
        BoxSlots.Clear();
        if (SAV is not { HasBox: true } sav)
        {
            BoxSelector.ItemsSource = null;
            return;
        }

        for (int i = 0; i < sav.BoxSlotCount; i++)
        {
            int slot = i;
            var button = CreateSlotButton();
            button.Click += (_, _) => SelectSlot(false, BoxSelector.SelectedIndex, slot);
            BoxSlots.Add(button);
            BoxGrid.Children.Add(button);
        }

        Loading = true;
        BoxSelector.ItemsSource = BoxUtil.GetBoxNames(sav).Select((n, i) => $"{i + 1}: {n}").ToList();
        Loading = false;
        BoxSelector.SelectedIndex = Math.Clamp(sav.CurrentBox, 0, sav.BoxCount - 1);
        RefreshBox();
    }

    private void BuildPartySlots()
    {
        PartyGrid.Children.Clear();
        PartySlots.Clear();
        if (SAV is not { HasParty: true })
            return;
        for (int i = 0; i < 6; i++)
        {
            int slot = i;
            var button = CreateSlotButton();
            button.Click += (_, _) => SelectSlot(true, 0, slot);
            PartySlots.Add(button);
            PartyGrid.Children.Add(button);
        }
        RefreshParty();
    }

    private static Button CreateSlotButton() => new()
    {
        Classes = { "slot" },
        Content = new Image { Width = 68, Height = 56, Stretch = Stretch.Uniform },
    };

    private static void SetSlotContent(Button button, PKM? pk)
    {
        var image = (Image)button.Content!;
        if (pk is null || pk.Species == 0)
        {
            image.Source = null;
            ToolTip.SetTip(button, "(empty)");
            return;
        }
        image.Source = Sprites.GetSprite(pk);
        ToolTip.SetTip(button, GetEntitySummary(pk));
    }

    private static string GetEntitySummary(PKM pk)
    {
        var strings = GameInfo.Strings;
        if (pk.Species == 0)
            return "(empty)";
        var species = pk.Species < strings.specieslist.Length ? strings.specieslist[pk.Species] : $"#{pk.Species}";
        if (pk.IsEgg)
            return $"Egg ({species})";
        var name = pk.IsNicknamed ? $"{pk.Nickname} ({species})" : species;
        var shiny = pk.IsShiny ? " ★" : string.Empty;
        return $"{name}{shiny} — Lv. {pk.CurrentLevel}";
    }

    private void RefreshBox()
    {
        if (SAV is not { HasBox: true } sav || BoxSelector.SelectedIndex < 0)
            return;
        var box = BoxSelector.SelectedIndex;
        for (int i = 0; i < BoxSlots.Count; i++)
            SetSlotContent(BoxSlots[i], sav.GetBoxSlotAtIndex(box, i));
        UpdateSelectionHighlight();
    }

    private void RefreshParty()
    {
        if (SAV is not { HasParty: true } sav)
            return;
        for (int i = 0; i < PartySlots.Count; i++)
            SetSlotContent(PartySlots[i], i < sav.PartyCount ? sav.GetPartySlotAtIndex(i) : null);
        UpdateSelectionHighlight();
    }

    private void UpdateSelectionHighlight()
    {
        for (int i = 0; i < BoxSlots.Count; i++)
            BoxSlots[i].Classes.Set("selected", Selected is { Party: false } s && s.Box == BoxSelector.SelectedIndex && s.Slot == i);
        for (int i = 0; i < PartySlots.Count; i++)
            PartySlots[i].Classes.Set("selected", Selected is { Party: true } s && s.Slot == i);
        BtnApply.IsEnabled = BtnDelete.IsEnabled = Selected is not null;
    }

    private void Box_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (Loading)
            return;
        RefreshBox();
    }

    private void Box_Prev(object? sender, RoutedEventArgs e)
    {
        if (SAV is not { HasBox: true } sav)
            return;
        BoxSelector.SelectedIndex = (BoxSelector.SelectedIndex + sav.BoxCount - 1) % sav.BoxCount;
    }

    private void Box_Next(object? sender, RoutedEventArgs e)
    {
        if (SAV is not { HasBox: true } sav)
            return;
        BoxSelector.SelectedIndex = (BoxSelector.SelectedIndex + 1) % sav.BoxCount;
    }

    private void SelectSlot(bool party, int box, int slot)
    {
        if (SAV is not { } sav || box < 0)
            return;
        Selected = (party, box, slot);
        PKM pk;
        if (party)
            pk = slot < sav.PartyCount ? sav.GetPartySlotAtIndex(slot) : sav.LoadTemplate();
        else
            pk = sav.GetBoxSlotAtIndex(box, slot);
        if (pk.Species == 0)
            pk = sav.LoadTemplate();
        LoadEditor(pk);
        UpdateSelectionHighlight();
        var where = party ? $"Party slot {slot + 1}" : $"Box {box + 1}, slot {slot + 1}";
        SetStatus($"Selected {where}.");
    }

    private async void Slot_Apply(object? sender, RoutedEventArgs e)
    {
        var pk = GetEditedEntity();
        if (pk.Species == 0)
        {
            await ShowMessage("Save to slot", "Choose a species first.");
            return;
        }
        if (await WriteToSelectedSlot(pk))
            SetStatus("Pokémon written to the selected slot. Use File → Save to write the save file.");
    }

    /// <summary> Writes <paramref name="pk"/> into the selected party/box slot. Returns false if nothing was written. </summary>
    private async Task<bool> WriteToSelectedSlot(PKM pk)
    {
        if (SAV is not { } sav || Selected is not { } s)
            return false;
        if (s.Party)
        {
            var index = Math.Min(s.Slot, sav.PartyCount);
            sav.SetPartySlotAtIndex(pk, index);
            Selected = (true, 0, index);
            RefreshParty();
        }
        else
        {
            if (sav.IsBoxSlotOverwriteProtected(s.Box, s.Slot))
            {
                await ShowMessage("Save to slot", "This slot is locked or overwrite-protected.");
                return false;
            }
            sav.SetBoxSlotAtIndex(pk, s.Box, s.Slot);
            if (BoxSelector.SelectedIndex != s.Box)
                BoxSelector.SelectedIndex = s.Box;
            RefreshBox();
        }
        sav.State.Edited = true;
        LoadEditor(pk);
        return true;
    }

    private async void Slot_Delete(object? sender, RoutedEventArgs e)
    {
        if (SAV is not { } sav || Selected is not { } s)
            return;
        if (s.Party)
        {
            if (s.Slot >= sav.PartyCount)
                return;
            if (sav.PartyCount <= 1)
            {
                await ShowMessage("Delete slot", "The party must contain at least one Pokémon.");
                return;
            }
            sav.DeletePartySlot(s.Slot);
            RefreshParty();
        }
        else
        {
            if (sav.IsBoxSlotOverwriteProtected(s.Box, s.Slot))
            {
                await ShowMessage("Delete slot", "This slot is locked or overwrite-protected.");
                return;
            }
            sav.SetBoxSlotAtIndex(sav.BlankPKM, s.Box, s.Slot);
            RefreshBox();
        }
        sav.State.Edited = true;
        LoadEditor(sav.LoadTemplate());
        SetStatus("Slot cleared.");
    }

    #endregion

    #region Import / Export entity

    private async void Menu_ImportPKM(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Pokémon file",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Pokémon files") { Patterns = ["*.pk*", "*.pb*", "*.pa*", "*.ck3", "*.xk3", "*.bk4", "*.rk4", "*.sk2", "*.wc*", "*.pgf", "*.pgt", "*.pcd"] }, AnyFile],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null)
            await OpenFileAsync(path);
    }

    private async Task ImportEntity(PKM pk)
    {
        if (SAV is { } sav && pk.GetType() != sav.PKMType)
        {
            var converted = EntityConverter.ConvertToType(pk, sav.PKMType, out var result);
            if (converted is null)
            {
                await ShowMessage("Import", result.GetDisplayString(pk, sav.PKMType));
                return;
            }
            pk = converted;
        }
        LoadEditor(pk);
        SetStatus(Selected is null
            ? "Pokémon loaded into the editor. Select a slot and click \"Save to selected slot\" to store it."
            : "Pokémon loaded into the editor. Click \"Save to selected slot\" to store it.");
    }

    private async Task ImportGroup(IPokeGroup group)
    {
        if (SAV is not { HasBox: true } sav)
            return;
        var box = Math.Max(0, BoxSelector.SelectedIndex);
        if (!await Confirm("Import", $"Load {group.Contents.Count()} Pokémon into Box {box + 1}, overwriting its contents?"))
            return;
        var list = group.Contents.Select(z => EntityConverter.ConvertToType(z, sav.PKMType, out _)).OfType<PKM>().ToList();
        sav.SetBoxData(list, box);
        sav.State.Edited = true;
        RefreshBox();
        SetStatus($"Imported {list.Count} Pokémon into Box {box + 1}.");
    }

    private async void Menu_ExportPKM(object? sender, RoutedEventArgs e)
    {
        var pk = GetEditedEntity();
        if (pk.Species == 0)
        {
            await ShowMessage("Export", "There is no Pokémon in the editor to export.");
            return;
        }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Pokémon",
            SuggestedFileName = PathUtil.CleanFileName(pk.FileName),
            DefaultExtension = pk.Extension,
            ShowOverwritePrompt = true,
        });
        var path = file?.TryGetLocalPath();
        if (path is null)
            return;
        var data = new byte[pk.SIZE_PARTY];
        pk.WriteDecryptedDataParty(data);
        await File.WriteAllBytesAsync(path, data);
        SetStatus($"Exported to {path}");
    }

    #endregion

    #region Editor

    private static void SetSource(ComboBox cb, IReadOnlyList<ComboItem> list) => Ui.SetSource(cb, list);
    private static int GetValue(ComboBox cb) => Ui.GetValue(cb);
    private static void SetValue(ComboBox cb, int value) => Ui.SetValue(cb, value);

    private void PopulateFilteredSources()
    {
        Loading = true;
        var src = GameInfo.FilteredSources;
        SetSource(CB_Species, src.Species);
        SetSource(CB_Nature, src.Natures);
        SetSource(CB_HeldItem, src.Items);
        SetSource(CB_Ball, src.Balls);
        SetSource(CB_Language, src.Languages);
        foreach (var cb in MoveBoxes)
            SetSource(cb, src.Moves);
        PopulateExtraSources();
        Loading = false;
    }

    private void BuildStatGrid()
    {
        StatGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        AddCell(new TextBlock { Text = "Stat", FontWeight = FontWeight.Bold }, 0, 0);
        AddCell(new TextBlock { Text = "IV", FontWeight = FontWeight.Bold, Margin = new(4, 0) }, 0, 1);
        AddCell(new TextBlock { Text = "EV", FontWeight = FontWeight.Bold, Margin = new(4, 0) }, 0, 2);
        AddCell(new TextBlock { Text = "Value", FontWeight = FontWeight.Bold, Margin = new(12, 0) }, 0, 3);
        for (int i = 0; i < 6; i++)
        {
            StatGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            StatLabels[i] = new TextBlock { Text = StatNames[i], VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0) };
            IVs[i] = new NumericUpDown { Minimum = 0, Maximum = 31, FormatString = "0", Margin = new(4, 2) };
            EVs[i] = new NumericUpDown { Minimum = 0, Maximum = 252, FormatString = "0", Margin = new(4, 2) };
            StatValues[i] = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new(12, 0) };
            IVs[i].ValueChanged += (_, _) => UpdateStatPreview();
            EVs[i].ValueChanged += (_, _) => UpdateStatPreview();
            AddCell(StatLabels[i], i + 1, 0);
            AddCell(IVs[i], i + 1, 1);
            AddCell(EVs[i], i + 1, 2);
            AddCell(StatValues[i], i + 1, 3);
        }
        NUD_Level.ValueChanged += (_, _) => UpdateStatPreview();
        CB_Nature.SelectionChanged += (_, _) => UpdateStatPreview();
        CB_Form.SelectionChanged += (_, _) => { if (!Loading) { RefreshAbilities(); UpdateStatPreview(); } };

        void AddCell(Control c, int row, int col)
        {
            Grid.SetRow(c, row);
            Grid.SetColumn(c, col);
            StatGrid.Children.Add(c);
        }
    }

    private static int GetIV(PKM pk, int i) => i switch { 0 => pk.IV_HP, 1 => pk.IV_ATK, 2 => pk.IV_DEF, 3 => pk.IV_SPE, 4 => pk.IV_SPA, _ => pk.IV_SPD };
    private static int GetEV(PKM pk, int i) => i switch { 0 => pk.EV_HP, 1 => pk.EV_ATK, 2 => pk.EV_DEF, 3 => pk.EV_SPE, 4 => pk.EV_SPA, _ => pk.EV_SPD };

    private static void SetIV(PKM pk, int i, int value)
    {
        switch (i)
        {
            case 0: pk.IV_HP = value; break;
            case 1: pk.IV_ATK = value; break;
            case 2: pk.IV_DEF = value; break;
            case 3: pk.IV_SPE = value; break;
            case 4: pk.IV_SPA = value; break;
            default: pk.IV_SPD = value; break;
        }
    }

    private static void SetEV(PKM pk, int i, int value)
    {
        switch (i)
        {
            case 0: pk.EV_HP = value; break;
            case 1: pk.EV_ATK = value; break;
            case 2: pk.EV_DEF = value; break;
            case 3: pk.EV_SPE = value; break;
            case 4: pk.EV_SPA = value; break;
            default: pk.EV_SPD = value; break;
        }
    }

    private void LoadEditor(PKM pk)
    {
        Loading = true;
        Current = pk;
        var format = pk.Format;

        SetValue(CB_Species, pk.Species);
        RefreshForms(pk.Species, pk.Context);
        CB_Form.SelectedIndex = Math.Clamp(pk.Form, 0, Math.Max(0, CB_Form.ItemCount - 1));
        TB_Nickname.Text = pk.Nickname;
        TB_Nickname.MaxLength = pk.MaxStringLengthNickname;
        CHK_Nicknamed.IsChecked = pk.IsNicknamed;
        NUD_Level.Value = pk.Species == 0 ? 1 : pk.CurrentLevel;

        CB_Nature.IsEnabled = format >= 3;
        SetValue(CB_Nature, (int)pk.Nature);

        CB_Ability.IsEnabled = format >= 3;
        RefreshAbilities();
        SelectAbility(pk);
        LoadedAbilityIndex = CB_Ability.SelectedIndex;

        CB_HeldItem.IsEnabled = format >= 2;
        SetValue(CB_HeldItem, pk.HeldItem);
        CB_Ball.IsEnabled = format >= 3;
        SetValue(CB_Ball, pk.Ball);
        SetValue(CB_Language, pk.Language);

        LoadEditorExtras(pk);
        CHK_Shiny.IsChecked = pk.IsShiny;
        CHK_Egg.IsChecked = pk.IsEgg;

        Span<ushort> moves = stackalloc ushort[4];
        pk.GetMoves(moves);
        for (int i = 0; i < 4; i++)
            SetValue(MoveBoxes[i], moves[i]);

        bool gb = format <= 2;
        for (int i = 0; i < 6; i++)
        {
            IVs[i].Maximum = pk.MaxIV;
            EVs[i].Maximum = pk.MaxEV;
            IVs[i].Value = GetIV(pk, i);
            EVs[i].Value = GetEV(pk, i);
        }
        StatLabels[4].Text = gb ? "Special" : StatNames[4];
        StatLabels[5].IsVisible = IVs[5].IsVisible = EVs[5].IsVisible = StatValues[5].IsVisible = !gb;
        IVs[0].IsEnabled = !gb; // HP IV is derived from the other IVs in Gen 1/2

        TB_OT.Text = pk.OriginalTrainerName;
        TB_OT.MaxLength = pk.MaxStringLengthTrainer;
        NUD_TID.Value = pk.DisplayTID;
        NUD_SID.Value = pk.DisplaySID;
        NUD_SID.IsEnabled = format >= 3;

        LoadDetails(pk);

        Loading = false;
        UpdatePreview(pk);
        UpdateStatPreview();
    }

    private void RefreshForms(ushort species, EntityContext context)
    {
        var s = GameInfo.Strings;
        var forms = FormConverter.GetFormList(species, s.types, s.forms, GameInfo.GenderSymbolUnicode, context);
        if (forms.Length == 0)
            forms = ["—"];
        SetSource(CB_Form, forms.Select((f, i) => new ComboItem(f, i)).ToList());
        CB_Form.IsEnabled = forms.Length > 1;
    }

    private PersonalInfo GetPersonal()
    {
        var table = SAV?.Personal ?? FakeSaveFile.Default.Personal;
        var species = (ushort)GetValue(CB_Species);
        var form = (byte)Math.Max(0, CB_Form.SelectedIndex);
        if (!table.IsPresentInGame(species, form))
            form = 0;
        return table.GetFormEntry(species, form);
    }

    private void RefreshAbilities()
    {
        var previous = CB_Ability.SelectedIndex;
        var list = GameInfo.FilteredSources.GetAbilityList(GetPersonal());
        SetSource(CB_Ability, list);
        CB_Ability.SelectedIndex = list.Count == 0 ? -1 : Math.Clamp(previous, 0, list.Count - 1);
    }

    private void SelectAbility(PKM pk)
    {
        if (CB_Ability.ItemsSource is not IReadOnlyList<ComboItem> list || list.Count == 0)
            return;
        int index = pk.AbilityNumber switch { 1 => 0, 2 => 1, 4 => 2, _ => -1 };
        if ((uint)index >= list.Count || list[index].Value != pk.Ability)
        {
            index = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Value != pk.Ability)
                    continue;
                index = i;
                break;
            }
        }
        CB_Ability.SelectedIndex = index;
    }

    private void Species_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (Loading)
            return;
        Loading = true;
        RefreshForms((ushort)GetValue(CB_Species), Current.Context);
        CB_Form.SelectedIndex = 0;
        Loading = false;
        RefreshAbilities();
        if (CHK_Nicknamed.IsChecked != true)
        {
            var species = (ushort)GetValue(CB_Species);
            TB_Nickname.Text = SpeciesName.GetSpeciesNameGeneration(species, Math.Max(1, GetValue(CB_Language)), Current.Format);
        }
        UpdateStatPreview();
    }

    private void Stats_MaxIVs(object? sender, RoutedEventArgs e)
    {
        foreach (var nud in IVs)
            nud.Value = nud.Maximum;
    }

    private void Stats_ClearEVs(object? sender, RoutedEventArgs e)
    {
        foreach (var nud in EVs)
            nud.Value = 0;
    }

    private void UpdateStatPreview()
    {
        if (Loading)
            return;
        var pk = Current.Clone();
        try
        {
            ApplyStatFields(pk);
            pk.Species = (ushort)GetValue(CB_Species);
            pk.Form = (byte)Math.Max(0, CB_Form.SelectedIndex);
            if (pk.Format >= 3)
                pk.StatAlignment = (Nature)GetValue(pk.Format >= 8 ? CB_StatNature : CB_Nature);
            pk.CurrentLevel = (byte)(NUD_Level.Value ?? 1);
            var stats = pk.GetStats(pk.PersonalInfo);
            for (int i = 0; i < 6; i++)
                StatValues[i].Text = stats[i].ToString();
            L_StatTotals.Text = $"IV total: {IVs.Take(pk.Format <= 2 ? 5 : 6).Sum(z => (int)(z.Value ?? 0))}  ·  EV total: {EVs.Take(pk.Format <= 2 ? 5 : 6).Sum(z => (int)(z.Value ?? 0))}";
        }
        catch (Exception)
        {
            // Partial input (e.g. species not yet chosen); ignore until fields are complete.
        }
    }

    private void ApplyStatFields(PKM pk)
    {
        bool gb = pk.Format <= 2;
        for (int i = gb ? 1 : 0; i < 6; i++)
        {
            var iv = (int)(IVs[gb && i == 5 ? 4 : i].Value ?? 0);
            SetIV(pk, i, iv);
        }
        for (int i = 0; i < 6; i++)
        {
            var ev = (int)(EVs[gb && i == 5 ? 4 : i].Value ?? 0);
            SetEV(pk, i, ev);
        }
    }

    /// <summary>
    /// Builds a copy of <see cref="Current"/> with all editor fields applied.
    /// </summary>
    private PKM GetEditedEntity()
    {
        // Only fields the user actually changed are written, so that saving an untouched Pokémon
        // doesn't alter data the editor doesn't show (EXP within a level, PP, string trash bytes, …).
        var original = Current;
        var pk = original.Clone();
        var format = pk.Format;

        pk.Species = (ushort)GetValue(CB_Species);
        if (pk.Species == 0)
            return pk;
        bool speciesChanged = pk.Species != original.Species;
        pk.Form = (byte)Math.Max(0, CB_Form.SelectedIndex);
        ApplyIdentifiers(pk);
        ApplyGender(pk, original);
        var language = GetValue(CB_Language);
        if (language != original.Language)
            pk.Language = language;
        pk.IsEgg = CHK_Egg.IsChecked == true;

        var nickname = TB_Nickname.Text ?? string.Empty;
        var nicknamed = CHK_Nicknamed.IsChecked == true;
        if (nicknamed != original.IsNicknamed || nickname != original.Nickname || speciesChanged || language != original.Language)
        {
            if (nicknamed && !string.IsNullOrWhiteSpace(nickname))
                pk.SetNickname(nickname);
            else
                pk.ClearNickname();
        }

        var level = (byte)Math.Clamp((int)(NUD_Level.Value ?? 1), 1, 100);
        if (speciesChanged || level != original.CurrentLevel)
            pk.CurrentLevel = level;
        var item = GetValue(CB_HeldItem);
        if (format >= 2 && item != original.HeldItem)
            pk.HeldItem = item;
        if (format >= 3)
        {
            var ball = (byte)GetValue(CB_Ball);
            if (ball != original.Ball)
                pk.Ball = ball;
            var nature = (Nature)GetValue(CB_Nature);
            if (nature != pk.Nature)
            {
                if (format <= 4)
                    pk.SetPIDNature(nature);
                else
                    pk.Nature = nature;
                pk.StatAlignment = nature;
            }
            var ability = CB_Ability.SelectedIndex;
            if (ability >= 0 && (speciesChanged || pk.Form != original.Form || ability != LoadedAbilityIndex))
                pk.RefreshAbility(ability);
        }

        ApplyMoves(pk, original);

        ApplyStatFields(pk);

        var ot = TB_OT.Text ?? string.Empty;
        if (ot != original.OriginalTrainerName)
            pk.OriginalTrainerName = ot;
        var tid = (uint)(NUD_TID.Value ?? 0);
        if (tid != original.DisplayTID)
            pk.DisplayTID = tid;
        var sid = (uint)(NUD_SID.Value ?? 0);
        if (format >= 3 && sid != original.DisplaySID)
            pk.DisplaySID = sid;

        ApplyEditorExtras(pk, original);
        ApplyDetails(pk);

        var shiny = CHK_Shiny.IsChecked == true;
        if (shiny != pk.IsShiny)
            pk.SetIsShiny(shiny);

        // Nothing edited: hand back an exact copy.
        if (IsStoredDataEqual(pk, original))
            return original.Clone();

        // Keep calculated stats in sync for Pokémon that store them (party data), preserving current HP.
        if (original.PartyStatsPresent)
        {
            var hp = original.Stat_HPCurrent;
            pk.ResetPartyStats();
            pk.Stat_HPCurrent = Math.Min(hp, pk.Stat_HPMax);
        }
        pk.RefreshChecksum();
        return pk;
    }

    private static bool IsStoredDataEqual(PKM a, PKM b)
    {
        if (a.SIZE_STORED != b.SIZE_STORED)
            return false;
        Span<byte> x = stackalloc byte[a.SIZE_STORED];
        Span<byte> y = stackalloc byte[b.SIZE_STORED];
        a.WriteDecryptedDataStored(x);
        b.WriteDecryptedDataStored(y);
        return x.SequenceEqual(y);
    }

    private void UpdatePreview(PKM pk)
    {
        PreviewSprite.Source = Sprites.GetSprite(pk);
        BallSprite.Source = pk.Format >= 3 && pk.Species != 0 ? Sprites.GetBall(pk.Ball) : null;
        PreviewTitle.Text = pk.Species == 0 ? "(empty)" : GetEntitySummary(pk);
        UpdateEvolveButton(pk);
        if (pk.Species == 0)
        {
            LegalityText.Text = string.Empty;
            return;
        }
        var la = new LegalityAnalysis(pk);
        LegalityText.Text = la.Valid ? "✔ Legal" : "✖ Not legal — see Check legality for details";
        LegalityText.Foreground = la.Valid ? Brushes.SeaGreen : Brushes.IndianRed;
    }

    private async void Menu_Legality(object? sender, RoutedEventArgs e)
    {
        var pk = GetEditedEntity();
        if (pk.Species == 0)
        {
            await ShowMessage("Legality", "There is no Pokémon in the editor.");
            return;
        }
        var la = new LegalityAnalysis(pk);
        UpdatePreview(pk);
        await ShowMessage("Legality", la.Report(verbose: true));
    }

    #endregion
}
