using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PKHeX.Core;

namespace PKHeX.Avalonia;

/// <summary>
/// Save-file tools: trainer, bag, Pokédex, event flags, batch editor, box utilities and Showdown sets.
/// </summary>
public partial class MainWindow
{
    private void RefreshSaveInfo()
    {
        if (SAV is not { } sav)
            return;
        var details = new List<string>
        {
            $"Game: {GameInfo.GetVersionName(sav.Version)} (Gen {sav.Generation})",
            $"Trainer: {sav.OT}  ·  TID: {sav.DisplayTID}",
        };
        try { details.Add($"Played: {sav.PlayTimeString.Replace('ː', ':')}"); }
        catch { /* play time block missing (e.g. incomplete save) */ }
        if (sav.State.Exportable && !IsChecksumValid(sav))
            details.Add("⚠ Checksums are invalid.");
        SaveDetails.Text = string.Join(Environment.NewLine, details);
    }

    private static bool IsChecksumValid(SaveFile sav)
    {
        try { return sav.ChecksumsValid; }
        catch { return false; }
    }

    private void UpdateToolMenus(SaveFile sav)
    {
        MenuTools.IsEnabled = true;
        MenuBag.IsEnabled = Safe(() => InventoryEditor.IsSupported(sav));
        MenuDex.IsEnabled = Safe(() => PokedexEditor.IsSupported(sav));
        MenuEvents.IsEnabled = Safe(() => EventFlagEditor.IsSupported(sav));
        MenuBoxes.IsEnabled = sav.HasBox;
        MenuGen3.IsVisible = sav is SAV3;

        static bool Safe(Func<bool> f)
        {
            try { return f(); }
            catch { return false; }
        }
    }

    private void RefreshAfterTool()
    {
        RefreshSaveInfo();
        RefreshBox();
        RefreshParty();
        // Reload the selected slot so the editor reflects any changes.
        if (Selected is { } s)
            SelectSlot(s.Party, s.Box, s.Slot);
    }

    private async Task OpenTool(Func<SaveFile, Window> factory, string name)
    {
        if (SAV is not { } sav)
            return;
        Window window;
        try
        {
            window = factory(sav);
        }
        catch (Exception ex)
        {
            await ShowMessage(name, $"This editor is not available for this save file.\n\n{ex.Message}");
            return;
        }
        var saved = await window.ShowDialog<bool?>(this);
        if (saved == true)
        {
            RefreshAfterTool();
            SetStatus($"{name} changes applied. Use File → Save to write the save file.");
        }
    }

    private async void Menu_Trainer(object? sender, RoutedEventArgs e) => await OpenTool(TrainerEditor.Create, "Trainer");
    private async void Menu_Bag(object? sender, RoutedEventArgs e) => await OpenTool(InventoryEditor.Create, "Bag");
    private async void Menu_Pokedex(object? sender, RoutedEventArgs e) => await OpenTool(PokedexEditor.Create, "Pokédex");
    private async void Menu_EventFlags(object? sender, RoutedEventArgs e) => await OpenTool(EventFlagEditor.Create, "Event flags");

    private async void Menu_Batch(object? sender, RoutedEventArgs e)
    {
        if (SAV is not { } sav)
            return;
        var window = BatchEditorWindow.Create(sav, Math.Max(0, BoxSelector.SelectedIndex), RefreshAfterTool);
        await window.ShowDialog(this);
    }

    private async void Menu_Gen3(object? sender, RoutedEventArgs e)
    {
        if (SAV is not SAV3 sav)
            return;
        var window = Gen3Tools.Create(sav, RefreshAfterTool);
        await window.ShowDialog(this);
        RefreshAfterTool();
    }

    #region Updates

    private async void Menu_Update(object? sender, RoutedEventArgs e)
    {
        var window = UpdateWindow.Create(ConfirmUpdate, RestartIntoUpdate);
        await window.ShowDialog(this);
    }

    /// <summary> PKHeX restarts right after an update, so unsaved changes must be dealt with first. </summary>
    private async Task<bool> ConfirmUpdate()
    {
        if (SAV?.State.Edited != true)
            return true;
        return await Confirm("Update", "Your save file has unsaved changes, and PKHeX will restart after the update.\n\nChoose No, close this window and use File → Save first — or Yes to update anyway and lose the changes.");
    }

    /// <summary> Starts the updated program (reopening the current save) and closes this one. </summary>
    private async Task RestartIntoUpdate(string program)
    {
        var args = SAV?.Metadata.FilePath is { Length: > 0 } path ? new[] { path } : [];
        try
        {
            Updater.Restart(program, args);
        }
        catch (Exception ex)
        {
            await ShowMessage("Restart", $"Couldn't start the new version automatically: {ex.Message}\n\nPlease close PKHeX and open it again.");
            return;
        }
        if (SAV is not null)
            SAV.State.Edited = false; // already confirmed above
        if (global::Avalonia.Application.Current?.ApplicationLifetime is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Close();
    }

    /// <summary> Background check at startup; only shows a status-bar hint. </summary>
    private async Task CheckForUpdatesQuietly()
    {
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            var info = await Task.Run(() => Updater.CheckAsync(cts.Token), cts.Token);
            if (info.Available)
                SetStatus($"Update available: {info.Summary} Use Help → Check for updates.");
        }
        catch
        {
            // offline or no git: ignore silently
        }
    }

    #endregion

    #region Boxes

    private async void Menu_SortBoxes(object? sender, RoutedEventArgs e)
    {
        if (SAV is not { HasBox: true } sav)
            return;
        if (!await Confirm("Sort boxes", "Sort all Pokémon in every box by species number? Locked slots are left in place."))
            return;
        sav.SortBoxes();
        sav.State.Edited = true;
        RefreshAfterTool();
        SetStatus("Boxes sorted.");
    }

    private async void Menu_ClearBox(object? sender, RoutedEventArgs e)
    {
        if (SAV is not { HasBox: true } sav || BoxSelector.SelectedIndex < 0)
            return;
        var box = BoxSelector.SelectedIndex;
        if (!await Confirm("Clear box", $"Delete every Pokémon in Box {box + 1}? This cannot be undone (unless you don't save)."))
            return;
        var count = sav.ClearBoxes(box, box);
        sav.State.Edited = true;
        RefreshAfterTool();
        SetStatus($"Cleared {count} Pokémon from Box {box + 1}.");
    }

    private async void Menu_DumpBoxes(object? sender, RoutedEventArgs e)
    {
        if (SAV is not { HasBox: true } sav)
            return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a folder for the exported Pokémon", AllowMultiple = false });
        var root = folders.FirstOrDefault()?.TryGetLocalPath();
        if (root is null)
            return;

        var names = BoxUtil.GetBoxNames(sav);
        int count = 0;
        try
        {
            for (int box = 0; box < sav.BoxCount; box++)
            {
                var dir = Path.Combine(root, PathUtil.CleanFileName($"{box + 1:00} {names[box]}"));
                for (int slot = 0; slot < sav.BoxSlotCount; slot++)
                {
                    var pk = sav.GetBoxSlotAtIndex(box, slot);
                    if (pk.Species == 0 || !pk.Valid)
                        continue;
                    Directory.CreateDirectory(dir);
                    var data = new byte[pk.SIZE_STORED];
                    pk.WriteDecryptedDataStored(data);
                    await File.WriteAllBytesAsync(Path.Combine(dir, PathUtil.CleanFileName(pk.FileName)), data);
                    count++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessage("Export boxes", $"Unable to write files:\n{ex.Message}");
            return;
        }
        SetStatus($"Exported {count} Pokémon to {root}");
    }

    private async void Menu_ShowdownBox(object? sender, RoutedEventArgs e)
    {
        if (SAV is not { HasBox: true } sav || BoxSelector.SelectedIndex < 0)
            return;
        var data = sav.GetBoxData(BoxSelector.SelectedIndex).Where(z => z.Species != 0);
        var text = ShowdownParsing.GetShowdownSets(data, Environment.NewLine + Environment.NewLine);
        await Ui.PromptText(this, "Showdown export", $"Box {BoxSelector.SelectedIndex + 1} as Showdown sets (select and copy the text).", text, readOnly: true);
    }

    private async void Menu_LegalityReport(object? sender, RoutedEventArgs e)
    {
        if (SAV is not { HasBox: true } sav)
            return;
        SetStatus("Checking legality of all boxes…");
        var report = await Task.Run(() => BuildLegalityReport(sav));
        SetStatus("Legality report finished.");
        await Ui.PromptText(this, "Legality report", "Pokémon in your boxes and party that did not pass the legality check:", report, readOnly: true);
    }

    private static string BuildLegalityReport(SaveFile sav)
    {
        var sb = new StringBuilder();
        int total = 0, bad = 0;
        var species = GameInfo.Strings.specieslist;

        void Check(PKM pk, string where)
        {
            if (pk.Species == 0)
                return;
            total++;
            var la = new LegalityAnalysis(pk);
            if (la.Valid)
                return;
            bad++;
            var name = pk.Species < species.Length ? species[pk.Species] : $"#{pk.Species}";
            sb.AppendLine($"{where}: {name}");
            foreach (var line in la.Report().Split(Environment.NewLine).Where(z => z.Length != 0))
                sb.AppendLine($"    {line}");
            sb.AppendLine();
        }

        for (int box = 0; box < sav.BoxCount; box++)
        {
            for (int slot = 0; slot < sav.BoxSlotCount; slot++)
                Check(sav.GetBoxSlotAtIndex(box, slot), $"Box {box + 1}, slot {slot + 1}");
        }
        if (sav.HasParty)
        {
            for (int i = 0; i < sav.PartyCount; i++)
                Check(sav.GetPartySlotAtIndex(i), $"Party slot {i + 1}");
        }

        var header = $"Checked {total} Pokémon: {total - bad} legal, {bad} not legal.{Environment.NewLine}{Environment.NewLine}";
        return header + sb;
    }

    #endregion

    #region Showdown

    private async void Menu_ShowdownImport(object? sender, RoutedEventArgs e)
    {
        var text = await Ui.PromptText(this, "Import Showdown set", "Paste a Showdown / battle-team set. It will be applied to the Pokémon in the editor.");
        if (string.IsNullOrWhiteSpace(text))
            return;
        var set = ShowdownParsing.GetShowdownSets(text).FirstOrDefault();
        if (set is null || set.Species == 0)
        {
            await ShowMessage("Import Showdown set", "No valid set was found in the text.");
            return;
        }

        var pk = GetEditedEntity();
        if (pk.Species == 0 && SAV is not null)
            pk = SAV.LoadTemplate();
        try
        {
            pk.ApplySetDetails(set);
        }
        catch (Exception ex)
        {
            await ShowMessage("Import Showdown set", $"The set could not be applied:\n{ex.Message}");
            return;
        }
        LoadEditor(pk);
        var msg = set.InvalidLines.Count == 0
            ? "Set imported into the editor."
            : $"Set imported, but some lines were not understood: {string.Join(", ", set.InvalidLines.Select(z => z.Value))}";
        SetStatus(msg);
    }

    private async void Menu_ShowdownExport(object? sender, RoutedEventArgs e)
    {
        var pk = GetEditedEntity();
        if (pk.Species == 0)
        {
            await ShowMessage("Export Showdown set", "There is no Pokémon in the editor.");
            return;
        }
        var text = ShowdownParsing.GetShowdownText(pk);
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
        await Ui.PromptText(this, "Export Showdown set", "Showdown set (also copied to the clipboard):", text, readOnly: true);
    }

    #endregion
}
