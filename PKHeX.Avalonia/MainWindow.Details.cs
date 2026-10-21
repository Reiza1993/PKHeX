using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using PKHeX.Core;

namespace PKHeX.Avalonia;

/// <summary>
/// Extra Pokémon editor tabs: Met, Ribbons, Memories, Contest &amp; Misc.
/// </summary>
public partial class MainWindow
{
    // Met
    private ComboBox CB_Version = null!;
    private ComboBox CB_MetLocation = null!;
    private ComboBox CB_EggLocation = null!;
    private NumericUpDown NUD_MetLevel = null!;
    private CalendarDatePicker DP_MetDate = null!;
    private CheckBox CHK_EggDate = null!;
    private CalendarDatePicker DP_EggDate = null!;
    private CheckBox CHK_Fateful = null!;
    private Grid MetGrid = null!;

    // Ribbons
    private StackPanel RibbonPanel = null!;
    private readonly List<(RibbonInfo Info, Control Editor)> RibbonEditors = [];

    // Memories
    private Control MemoryContent = null!;
    private TextBlock MemoryUnavailable = null!;
    private TextBox TB_HTName = null!;
    private ComboBox CB_Handler = null!;
    private MemoryControls OTMemory = null!;
    private MemoryControls HTMemory = null!;

    // Contest & misc
    private NumericUpDown[] Contest = [];
    private Control ContestPanel = null!;
    private NumericUpDown NUD_OTFriendship = null!;
    private NumericUpDown NUD_HTFriendship = null!;
    private NumericUpDown NUD_PKRSStrain = null!;
    private NumericUpDown NUD_PKRSDays = null!;
    private TextBox TB_PID = null!;
    private TextBox TB_EC = null!;
    private string LoadedPID = string.Empty;
    private string LoadedEC = string.Empty;

    private static readonly string[] ContestNames = ["Cool", "Beauty", "Cute", "Smart", "Tough", "Sheen"];

    private MemoryStrings? _memoryStrings;
    private MemoryStrings MemoryText => _memoryStrings ??= new MemoryStrings(GameInfo.Strings);

    private sealed class MemoryControls
    {
        public readonly ComboBox Memory = Ui.Combo();
        public readonly ComboBox Argument = Ui.Combo();
        public readonly ComboBox Intensity = Ui.Combo();
        public readonly ComboBox Feeling = Ui.Combo();
    }

    private void BuildDetailTabs()
    {
        // Met tab
        var met = new Ui.Form();
        CB_Version = met.Add("Origin game", Ui.Combo());
        CB_MetLocation = met.Add("Met location", Ui.Combo());
        NUD_MetLevel = met.Add("Met level", Ui.Number(0, 100));
        DP_MetDate = met.Add("Met date", new CalendarDatePicker { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new(0, 2) });
        met.AddWide(Ui.Header("Egg"));
        CB_EggLocation = met.Add("Egg location", Ui.Combo());
        CHK_EggDate = met.Add("Has egg date", new CheckBox());
        DP_EggDate = met.Add("Egg date", new CalendarDatePicker { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new(0, 2) });
        CHK_EggDate.IsCheckedChanged += (_, _) => DP_EggDate.IsEnabled = CHK_EggDate.IsChecked == true && CHK_EggDate.IsEnabled;
        met.AddWide(Ui.Row(Ui.Button("Suggest met data", Met_Suggest), Ui.Button("Hatched from an egg (fill egg data)", Met_AsEgg)));
        met.AddWide(Ui.Header("Other"));
        CHK_Fateful = met.Add("Fateful encounter", new CheckBox());
        CB_Version.SelectionChanged += (_, _) => { if (!Loading) RefreshLocations(); };
        MetGrid = met.Grid;
        AddEditorTab("Met", new ScrollViewer { Content = met.Grid });

        // Ribbons tab
        RibbonPanel = new StackPanel { Margin = new(4) };
        var ribbonButtons = Ui.Row(
            Ui.Button("Give all valid ribbons", (_, _) => ModifyEdited(RibbonApplicator.SetAllValidRibbons)),
            Ui.Button("Remove all ribbons", (_, _) => ModifyEdited(RibbonApplicator.RemoveAllValidRibbons)));
        var ribbonDock = new DockPanel();
        DockPanel.SetDock(ribbonButtons, Dock.Top);
        ribbonDock.Children.Add(ribbonButtons);
        ribbonDock.Children.Add(new ScrollViewer { Content = RibbonPanel });
        AddEditorTab("Ribbons", ribbonDock);

        // Memories tab
        var mem = new Ui.Form();
        OTMemory = new MemoryControls();
        HTMemory = new MemoryControls();
        mem.AddWide(Ui.Header("Original Trainer memory"));
        AddMemoryControls(mem, OTMemory);
        mem.AddWide(Ui.Header("Handling Trainer"));
        TB_HTName = mem.Add("Handler name", new TextBox());
        CB_Handler = mem.Add("Current handler", Ui.Combo([new("Original Trainer", 0), new("Handling Trainer", 1)]));
        AddMemoryControls(mem, HTMemory);
        MemoryUnavailable = new TextBlock { Text = "Memories are only stored from Generation 6 onward.", Margin = new(8), Opacity = 0.7 };
        MemoryContent = new ScrollViewer { Content = mem.Grid };
        AddEditorTab("Memories", new Panel { Children = { MemoryContent, MemoryUnavailable } });

        // Contest & Misc tab
        var misc = new Ui.Form();
        misc.AddWide(Ui.Header("Contest stats"));
        Contest = new NumericUpDown[ContestNames.Length];
        var contestForm = new Ui.Form();
        for (int i = 0; i < Contest.Length; i++)
            Contest[i] = contestForm.Add(ContestNames[i], Ui.Number(0, 255));
        ContestPanel = misc.AddWide(contestForm.Grid);
        misc.AddWide(Ui.Header("Friendship"));
        NUD_OTFriendship = misc.Add("OT friendship", Ui.Number(0, 255));
        NUD_HTFriendship = misc.Add("HT friendship", Ui.Number(0, 255));
        misc.AddWide(Ui.Header("Pokérus"));
        NUD_PKRSStrain = misc.Add("Strain", Ui.Number(0, 15));
        NUD_PKRSDays = misc.Add("Days", Ui.Number(0, 15));
        misc.AddWide(Ui.Header("Identifiers (hex)"));
        TB_PID = misc.Add("PID", new TextBox { MaxLength = 8 });
        TB_EC = misc.Add("Encryption constant", new TextBox { MaxLength = 8 });
        AddEditorTab("Contest & Misc", new ScrollViewer { Content = misc.Grid });
    }

    private void AddEditorTab(string header, Control content) => EditorTabs.Items.Add(new TabItem { Header = header, Content = content });

    private void AddMemoryControls(Ui.Form form, MemoryControls c)
    {
        form.Add("Memory", c.Memory);
        form.Add("Detail", c.Argument);
        form.Add("Intensity", c.Intensity);
        form.Add("Feeling", c.Feeling);
        c.Memory.SelectionChanged += (_, _) =>
        {
            if (Loading)
                return;
            RefreshMemoryArgument(c, MemoryGeneration(Current), 0);
        };
    }

    private async void ModifyEdited(Action<PKM> action)
    {
        var pk = GetEditedEntity();
        if (pk.Species == 0)
            return;
        try
        {
            action(pk);
        }
        catch (Exception ex)
        {
            await Ui.ShowMessage(this, "Ribbons", ex.Message);
            return;
        }
        LoadEditor(pk);
    }

    private static int MemoryGeneration(PKM pk) => pk.Format >= 8 ? 8 : 6;

    private void RefreshLocations()
    {
        var version = (GameVersion)Ui.GetValue(CB_Version);
        var context = Current.Context;
        var met = Ui.GetValue(CB_MetLocation);
        var egg = Ui.GetValue(CB_EggLocation);
        try
        {
            Ui.SetSource(CB_MetLocation, GameInfo.GetLocationList(version, context));
            Ui.SetSource(CB_EggLocation, GameInfo.GetLocationList(version, context, egg: true));
        }
        catch (Exception)
        {
            Ui.SetSource(CB_MetLocation, []);
            Ui.SetSource(CB_EggLocation, []);
        }
        Ui.SetValue(CB_MetLocation, met);
        Ui.SetValue(CB_EggLocation, egg);
    }

    private void RefreshMemoryArgument(MemoryControls c, int memoryGen, int value)
    {
        var memory = (byte)Ui.GetValue(c.Memory);
        var type = Memories.GetMemoryArgType(memory, memoryGen);
        var list = MemoryText.GetArgumentStrings(type, memoryGen);
        Ui.SetSource(c.Argument, list);
        Ui.SetValue(c.Argument, value);
        c.Argument.IsEnabled = type != MemoryArgType.None;
    }

    private static List<ComboItem> ToCombo(ReadOnlySpan<string> values)
    {
        var list = new List<ComboItem>(values.Length);
        for (int i = 0; i < values.Length; i++)
            list.Add(new ComboItem(string.IsNullOrEmpty(values[i]) ? $"({i})" : values[i], i));
        return list;
    }

    private void LoadMemory(MemoryControls c, int memoryGen, byte memory, ushort variable, byte intensity, byte feeling)
    {
        Ui.SetSource(c.Memory, MemoryText.Memory);
        Ui.SetValue(c.Memory, memory);
        RefreshMemoryArgument(c, memoryGen, variable);
        Ui.SetSource(c.Intensity, ToCombo(MemoryText.GetMemoryQualities()));
        Ui.SetValue(c.Intensity, intensity);
        Ui.SetSource(c.Feeling, ToCombo(MemoryText.GetMemoryFeelings(memoryGen)));
        Ui.SetValue(c.Feeling, feeling);
    }

    /// <summary>
    /// Loads the extra tabs from <paramref name="pk"/>. Called by LoadEditor while <see cref="Loading"/> is set.
    /// </summary>
    private void LoadDetails(PKM pk)
    {
        var format = pk.Format;

        // Met
        Ui.SetSource(CB_Version, GameInfo.FilteredSources.Games);
        Ui.SetValue(CB_Version, (int)pk.Version);
        CB_Version.IsEnabled = format >= 3;
        RefreshLocations();
        Ui.SetValue(CB_MetLocation, pk.MetLocation);
        Ui.SetValue(CB_EggLocation, pk.EggLocation);
        CB_EggLocation.IsEnabled = format >= 4;
        NUD_MetLevel.Value = pk.MetLevel;
        MetGrid.IsEnabled = format >= 2;
        var metDate = pk.MetDate;
        DP_MetDate.IsEnabled = metDate is not null;
        DP_MetDate.SelectedDate = metDate?.ToDateTime(TimeOnly.MinValue);
        var eggDate = pk.EggMetDate;
        CHK_EggDate.IsEnabled = format >= 4;
        CHK_EggDate.IsChecked = eggDate is not null;
        DP_EggDate.IsEnabled = eggDate is not null;
        DP_EggDate.SelectedDate = eggDate?.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today;
        CHK_Fateful.IsChecked = pk.FatefulEncounter;
        CHK_Fateful.IsEnabled = format >= 3;

        // Ribbons
        RibbonEditors.Clear();
        RibbonPanel.Children.Clear();
        var ribbons = RibbonInfo.GetRibbonInfo(pk);
        if (ribbons.Count == 0)
            RibbonPanel.Children.Add(new TextBlock { Text = "This format has no ribbons.", Opacity = 0.7 });
        var names = GameInfo.Strings.Ribbons;
        foreach (var r in ribbons.OrderBy(z => z.Name))
        {
            var display = names.GetNameSafe(r.Name, out var n) ? n : r.Name.Replace(RibbonInfo.PropertyPrefix, string.Empty);
            Control editor;
            if (r.Type == RibbonValueType.Boolean)
            {
                editor = new CheckBox { Content = display, IsChecked = r.HasRibbon };
            }
            else
            {
                var nud = Ui.Number(0, r.MaxCount, Math.Min(r.RibbonCount, (byte)r.MaxCount));
                nud.Width = 140;
                editor = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { nud, Ui.Label(display) } };
            }
            RibbonEditors.Add((r, editor));
            RibbonPanel.Children.Add(editor);
        }

        // Memories
        bool hasMemories = pk is IMemoryOT or IMemoryHT;
        MemoryContent.IsVisible = hasMemories;
        MemoryUnavailable.IsVisible = !hasMemories;
        var memoryGen = MemoryGeneration(pk);
        if (pk is IMemoryOT ot)
            LoadMemory(OTMemory, memoryGen, ot.OriginalTrainerMemory, ot.OriginalTrainerMemoryVariable, ot.OriginalTrainerMemoryIntensity, ot.OriginalTrainerMemoryFeeling);
        SetMemoryEnabled(OTMemory, pk is IMemoryOT);
        if (pk is IMemoryHT ht)
            LoadMemory(HTMemory, memoryGen, ht.HandlingTrainerMemory, ht.HandlingTrainerMemoryVariable, ht.HandlingTrainerMemoryIntensity, ht.HandlingTrainerMemoryFeeling);
        SetMemoryEnabled(HTMemory, pk is IMemoryHT);
        TB_HTName.Text = pk.HandlingTrainerName;
        TB_HTName.MaxLength = pk.MaxStringLengthTrainer;
        TB_HTName.IsEnabled = CB_Handler.IsEnabled = format >= 6;
        Ui.SetValue(CB_Handler, pk.CurrentHandler);

        // Contest & misc
        ContestPanel.IsEnabled = pk is IContestStats;
        if (pk is IContestStatsReadOnly c)
        {
            Contest[0].Value = c.ContestCool;
            Contest[1].Value = c.ContestBeauty;
            Contest[2].Value = c.ContestCute;
            Contest[3].Value = c.ContestSmart;
            Contest[4].Value = c.ContestTough;
            Contest[5].Value = c.ContestSheen;
        }
        else
        {
            foreach (var nud in Contest)
                nud.Value = 0;
        }
        NUD_OTFriendship.Value = pk.OriginalTrainerFriendship;
        NUD_OTFriendship.IsEnabled = format >= 2;
        NUD_HTFriendship.Value = pk.HandlingTrainerFriendship;
        NUD_HTFriendship.IsEnabled = format >= 6;
        NUD_PKRSStrain.Value = pk.PokerusStrain;
        NUD_PKRSDays.Value = pk.PokerusDays;
        NUD_PKRSStrain.IsEnabled = NUD_PKRSDays.IsEnabled = format >= 2;
        TB_PID.Text = LoadedPID = pk.PID.ToString("X8");
        TB_EC.Text = LoadedEC = pk.EncryptionConstant.ToString("X8");
        TB_PID.IsEnabled = format >= 3;
        TB_EC.IsEnabled = format >= 6;
    }

    private static void SetMemoryEnabled(MemoryControls c, bool enabled)
    {
        c.Memory.IsEnabled = c.Intensity.IsEnabled = c.Feeling.IsEnabled = enabled;
        c.Argument.IsEnabled = enabled && c.Argument.IsEnabled;
    }

    /// <summary>
    /// Applies PID / EC edits. Called early so later edits (nature, shiny) can adjust the PID further.
    /// </summary>
    private void ApplyIdentifiers(PKM pk)
    {
        if (pk.Format >= 3 && TB_PID.Text != LoadedPID && uint.TryParse(TB_PID.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pid))
            pk.PID = pid;
        if (pk.Format >= 6 && TB_EC.Text != LoadedEC && uint.TryParse(TB_EC.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ec))
            pk.EncryptionConstant = ec;
    }

    /// <summary>
    /// Applies the extra tabs to <paramref name="pk"/>.
    /// </summary>
    private void ApplyDetails(PKM pk)
    {
        var format = pk.Format;

        // Met
        if (format >= 3)
            pk.Version = (GameVersion)Ui.GetValue(CB_Version);
        if (format >= 2 && CB_MetLocation.SelectedItem is ComboItem)
            pk.MetLocation = (ushort)Ui.GetValue(CB_MetLocation);
        if (format >= 4 && CB_EggLocation.SelectedItem is ComboItem)
            pk.EggLocation = (ushort)Ui.GetValue(CB_EggLocation);
        if (format >= 2)
            pk.MetLevel = (byte)(NUD_MetLevel.Value ?? 0);
        if (DP_MetDate.IsEnabled && DP_MetDate.SelectedDate is { } md)
            pk.MetDate = DateOnly.FromDateTime(md);
        if (format >= 4)
            pk.EggMetDate = CHK_EggDate.IsChecked == true && DP_EggDate.SelectedDate is { } ed ? DateOnly.FromDateTime(ed) : null;
        if (format >= 3)
            pk.FatefulEncounter = CHK_Fateful.IsChecked == true;

        // Ribbons
        foreach (var (info, editor) in RibbonEditors)
        {
            object value = editor switch
            {
                CheckBox chk => chk.IsChecked == true,
                StackPanel { Children: [NumericUpDown nud, ..] } => (byte)(nud.Value ?? 0),
                _ => null!,
            };
            if (value is not null)
                ReflectUtil.SetValue(pk, info.Name, value);
        }

        // Memories
        if (pk is IMemoryOT ot)
        {
            ot.OriginalTrainerMemory = (byte)Ui.GetValue(OTMemory.Memory);
            ot.OriginalTrainerMemoryVariable = (ushort)Ui.GetValue(OTMemory.Argument);
            ot.OriginalTrainerMemoryIntensity = (byte)Ui.GetValue(OTMemory.Intensity);
            ot.OriginalTrainerMemoryFeeling = (byte)Ui.GetValue(OTMemory.Feeling);
        }
        if (pk is IMemoryHT ht)
        {
            ht.HandlingTrainerMemory = (byte)Ui.GetValue(HTMemory.Memory);
            ht.HandlingTrainerMemoryVariable = (ushort)Ui.GetValue(HTMemory.Argument);
            ht.HandlingTrainerMemoryIntensity = (byte)Ui.GetValue(HTMemory.Intensity);
            ht.HandlingTrainerMemoryFeeling = (byte)Ui.GetValue(HTMemory.Feeling);
        }
        if (format >= 6)
        {
            var htName = TB_HTName.Text ?? string.Empty;
            if (htName != pk.HandlingTrainerName)
                pk.HandlingTrainerName = htName;
            pk.CurrentHandler = (byte)Ui.GetValue(CB_Handler);
            pk.HandlingTrainerFriendship = (byte)(NUD_HTFriendship.Value ?? 0);
        }

        // Contest & misc
        if (pk is IContestStats c)
        {
            c.ContestCool = (byte)(Contest[0].Value ?? 0);
            c.ContestBeauty = (byte)(Contest[1].Value ?? 0);
            c.ContestCute = (byte)(Contest[2].Value ?? 0);
            c.ContestSmart = (byte)(Contest[3].Value ?? 0);
            c.ContestTough = (byte)(Contest[4].Value ?? 0);
            c.ContestSheen = (byte)(Contest[5].Value ?? 0);
        }
        if (format >= 2)
        {
            pk.OriginalTrainerFriendship = (byte)(NUD_OTFriendship.Value ?? 0);
            pk.PokerusStrain = (int)(NUD_PKRSStrain.Value ?? 0);
            pk.PokerusDays = (int)(NUD_PKRSDays.Value ?? 0);
        }
    }
}
