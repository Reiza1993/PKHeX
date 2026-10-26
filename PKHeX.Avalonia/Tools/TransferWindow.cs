using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PKHeX.Core;

namespace PKHeX.Avalonia;

/// <summary>
/// Copies (or moves) the Pokémon that the open save is missing from a second save file,
/// then makes sure each one is registered as seen and caught in the Pokédex.
/// </summary>
public static class TransferWindow
{
    /// <summary> Where a candidate Pokémon lives in the source save. </summary>
    private sealed record Source(bool Party, int Index);

    private sealed class Candidate(PKM pk, Source source, string text) : INotifyPropertyChanged
    {
        public PKM Pk { get; } = pk;
        public Source Source { get; } = source;
        public string Text { get; } = text;
        public bool Include
        {
            get;
            set { field = value; PropertyChanged?.Invoke(this, new(nameof(Include))); }
        } = true;
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <param name="initialSource">Optional source save to load straight away.</param>
    public static Window Create(SaveFile dest, Action onChanged, string? initialSource = null)
    {
        var window = new Window
        {
            Title = "Transfer missing Pokémon from another save",
            Width = 860,
            Height = 640,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        SaveFile? src = null;
        string? srcPath = null;
        var names = GameInfo.Strings.specieslist;

        var srcLabel = new TextBlock { Text = "No source save chosen yet.", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        var choose = new Button { Content = "Choose source save…" };
        var byDex = new RadioButton { Content = "not caught in this save's Pokédex", IsChecked = true, GroupName = "missing" };
        var byBoxes = new RadioButton { Content = "not in this save's boxes or party", GroupName = "missing" };
        var move = new CheckBox { Content = "Move instead of copy: also remove them from the source save (the source file is saved, with a .bak backup)" };
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        var list = new ListBox
        {
            ItemTemplate = new FuncDataTemplate<Candidate>((_, _) =>
            {
                var chk = new CheckBox();
                chk.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(Candidate.Include)) { Mode = BindingMode.TwoWay });
                chk.Bind(ContentControl.ContentProperty, new Binding(nameof(Candidate.Text)));
                return chk;
            }),
        };
        var transfer = new Button { Content = "Transfer selected", IsEnabled = false, IsDefault = true };
        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => window.Close();
        List<Candidate> candidates = [];

        bool IsMissing(ushort species, HashSet<ushort> owned) => byDex.IsChecked == true ? !dest.GetCaught(species) : !owned.Contains(species);

        void Refresh()
        {
            if (src is null)
            {
                list.ItemsSource = null;
                summary.Text = string.Empty;
                transfer.IsEnabled = false;
                return;
            }
            var owned = new HashSet<ushort>(AllPokemon(dest).Where(z => !z.Pk.IsEgg).Select(z => z.Pk.Species));
            candidates = [];
            int notCompatible = 0;
            foreach (var group in AllPokemon(src).Where(z => z.Pk.Species != 0 && !z.Pk.IsEgg).GroupBy(z => z.Pk.Species).OrderBy(g => g.Key))
            {
                var species = group.Key;
                if (species > dest.MaxSpeciesID || !IsMissing(species, owned))
                    continue;
                // Best copy: legal first, then highest level
                var best = group
                    .Select(z => (z.Pk, z.Source, Legal: new LegalityAnalysis(z.Pk).Valid))
                    .OrderByDescending(z => z.Legal).ThenByDescending(z => z.Pk.CurrentLevel)
                    .First();
                if (best.Pk.GetType() != dest.PKMType && !EntityConverter.IsConvertibleToFormat(best.Pk, dest.BlankPKM.Format))
                {
                    notCompatible++;
                    continue;
                }
                var where = best.Source.Party ? $"Party slot {best.Source.Index + 1}" : BoxText(src, best.Source.Index);
                var nick = best.Pk.IsNicknamed ? $" \"{best.Pk.Nickname}\"" : string.Empty;
                var text = $"#{species:000} {names[species]}{nick}  Lv. {best.Pk.CurrentLevel}  OT {best.Pk.OriginalTrainerName}  ({where}){(best.Legal ? string.Empty : "  ⚠ not legal")}";
                candidates.Add(new Candidate(best.Pk, best.Source, text));
            }
            list.ItemsSource = candidates;
            var free = FreeBoxSlots(dest);
            summary.Text = $"{candidates.Count} species missing here were found in the source save" +
                           (notCompatible > 0 ? $" ({notCompatible} more can't be converted to this game)" : string.Empty) +
                           $". This save has {free} free box slot(s).";
            transfer.IsEnabled = candidates.Count > 0;
        }

        byDex.IsCheckedChanged += (_, _) => Refresh();

        async Task LoadSource(string path)
        {
            if (dest.Metadata.FilePath is { } own && Path.GetFullPath(own) == Path.GetFullPath(path))
            {
                await Ui.ShowMessage(window, "Transfer", "That's the save that is already open. Choose a different save file.");
                return;
            }
            if (FileUtil.GetSupportedFile(path) is not SaveFile s)
            {
                await Ui.ShowMessage(window, "Transfer", "That file isn't a save file PKHeX recognises.");
                return;
            }
            s.Metadata.SetExtraInfo(path);
            src = s;
            srcPath = path;
            srcLabel.Text = $"Source: {Path.GetFileName(path)} — {GameInfo.GetVersionName(s.Version)}, trainer {s.OT}";
            Refresh();
        }

        choose.Click += async (_, _) =>
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose the save to transfer from", AllowMultiple = false });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
                await LoadSource(path);
        };
        if (initialSource is not null)
            _ = LoadSource(initialSource);

        var all = Ui.Button("Select all", (_, _) => candidates.ForEach(c => c.Include = true));
        var none = Ui.Button("Select none", (_, _) => candidates.ForEach(c => c.Include = false));

        transfer.Click += async (_, _) =>
        {
            if (src is null)
                return;
            var chosen = candidates.Where(c => c.Include).ToList();
            if (chosen.Count == 0)
                return;
            var report = DoTransfer(dest, src, chosen.Select(c => (c.Pk, c.Source)).ToList(), move.IsChecked == true);
            if (move.IsChecked == true && report.Moved > 0 && srcPath is not null)
            {
                var error = await SaveSource(src, srcPath);
                if (error is not null)
                    report = report with { Lines = [.. report.Lines, "", $"⚠ The source save couldn't be written: {error}"] };
            }
            onChanged();
            Refresh();
            await Ui.ShowMessage(window, "Transfer", string.Join(Environment.NewLine, report.Lines));
        };

        var top = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = $"Destination (the open save): {GameInfo.GetVersionName(dest.Version)}, trainer {dest.OT}. " +
                           "Pick a second save; every species it has that this one is missing is listed below (one per species — the best legal, highest-level copy).",
                    TextWrapping = TextWrapping.Wrap,
                },
                Ui.Row(choose, srcLabel),
                Ui.Row(Ui.Label("Missing means:"), byDex, byBoxes),
                move,
                summary,
            },
        };
        var bottom = Ui.Row(all, none, transfer, close);
        bottom.HorizontalAlignment = HorizontalAlignment.Right;
        var panel = new DockPanel { Margin = new(12) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        panel.Children.Add(top);
        panel.Children.Add(bottom);
        panel.Children.Add(list);
        window.Content = panel;
        return window;
    }

    public sealed record Report(int Copied, int Moved, List<string> Lines);

    /// <summary>
    /// Copies the chosen Pokémon into the first free box slots of <paramref name="dest"/>, registers them in its Pokédex,
    /// and optionally removes them from <paramref name="src"/>.
    /// </summary>
    public static Report DoTransfer(SaveFile dest, SaveFile src, IReadOnlyList<(PKM Pk, (bool Party, int Index) Where)> chosen, bool move)
        => DoTransfer(dest, src, chosen.Select(c => (c.Pk, new Source(c.Where.Party, c.Where.Index))).ToList(), move);

    private static Report DoTransfer(SaveFile dest, SaveFile src, List<(PKM Pk, Source Source)> chosen, bool move)
    {
        var names = GameInfo.Strings.specieslist;
        var lines = new List<string>();
        var done = new List<Source>();
        int copied = 0, registered = 0;
        foreach (var (original, source) in chosen)
        {
            var name = names[original.Species];
            var pk = original.GetType() == dest.PKMType ? original.Clone() : EntityConverter.ConvertToType(original.Clone(), dest.PKMType, out _);
            if (pk is null)
            {
                lines.Add($"✖ {name}: can't be converted to this game.");
                continue;
            }
            var index = dest.NextOpenBoxSlot();
            if (index < 0)
            {
                lines.Add($"✖ {name} and the rest: no free box slots left.");
                break;
            }
            dest.SetBoxSlotAtIndex(pk, index); // also registers it in the Pokédex
            if (PokedexEditor.TryMarkCaught(dest, pk.Species))
                registered++;
            dest.GetBoxSlotFromIndex(index, out var box, out var slot);
            lines.Add($"✔ {name} → Box {box + 1}, slot {slot + 1}");
            copied++;
            done.Add(source);
        }

        int moved = 0;
        if (move && done.Count > 0)
        {
            foreach (var s in done.Where(z => !z.Party))
            {
                src.SetBoxSlotAtIndex(src.BlankPKM, s.Index);
                moved++;
            }
            // Remove party members from the back so the other positions don't shift; keep at least one Pokémon.
            foreach (var s in done.Where(z => z.Party).OrderByDescending(z => z.Index))
            {
                if (src.PartyCount <= 1)
                {
                    lines.Add("⚠ The last Pokémon in the source party was copied but left there (a party can't be empty).");
                    continue;
                }
                src.DeletePartySlot(s.Index);
                moved++;
            }
            src.State.Edited = true;
        }
        if (copied > 0)
            dest.State.Edited = true;

        var header = $"{copied} Pokémon {(move ? "moved" : "copied")}; {registered} registered as caught in the Pokédex.";
        if (dest is SAV3FRLG { NationalDex: false } && chosen.Any(c => c.Pk.Species > 151))
            header += "\nFireRed/LeafGreen only shows non-Kanto Pokémon in the Pokédex once you have the National Pokédex (Gen 3 tools → Islands & National Dex).";
        header += "\nUse File → Save to write this save.";
        return new Report(copied, moved, [header, "", .. lines]);
    }

    private static async Task<string?> SaveSource(SaveFile src, string path)
    {
        try
        {
            var bak = path + ".bak";
            if (File.Exists(path) && !File.Exists(bak))
                File.Copy(path, bak);
            var data = src.Write(src.Metadata.GetSuggestedFlags(Path.GetExtension(path.AsSpan())));
            await File.WriteAllBytesAsync(path, data);
            src.State.Edited = false;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private static IEnumerable<(PKM Pk, Source Source)> AllPokemon(SaveFile sav)
    {
        if (sav.HasParty)
        {
            for (int i = 0; i < sav.PartyCount; i++)
                yield return (sav.GetPartySlotAtIndex(i), new Source(true, i));
        }
        if (sav.HasBox)
        {
            for (int i = 0; i < sav.BoxCount * sav.BoxSlotCount; i++)
            {
                var pk = sav.GetBoxSlotAtIndex(i);
                if (pk.Species != 0)
                    yield return (pk, new Source(false, i));
            }
        }
    }

    private static int FreeBoxSlots(SaveFile sav)
    {
        if (!sav.HasBox)
            return 0;
        int free = 0;
        for (int i = 0; i < sav.BoxCount * sav.BoxSlotCount; i++)
        {
            if (sav.GetBoxSlotAtIndex(i).Species == 0 && !sav.IsBoxSlotOverwriteProtected(i))
                free++;
        }
        return free;
    }

    private static string BoxText(SaveFile sav, int index)
    {
        sav.GetBoxSlotFromIndex(index, out var box, out var slot);
        return $"Box {box + 1}, slot {slot + 1}";
    }
}
