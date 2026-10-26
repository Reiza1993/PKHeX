using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using PKHeX.Core;

namespace PKHeX.Avalonia;

/// <summary>
/// Simple Pokédex editor: seen / caught flags per species.
/// </summary>
public static class PokedexEditor
{
    public static bool IsSupported(SaveFile sav) => GetOps(sav) is not null;

    /// <summary>
    /// Per-game operations to mark a species as seen, caught (fully registered), or cleared.
    /// </summary>
    private sealed record DexOps(Action<ushort> Seen, Action<ushort> Caught, Action<ushort> Clear, bool ExactSeen = true);

    /// <summary>
    /// Marks a species as seen and caught using the game's own Pokédex structures. Returns true if it is caught afterwards.
    /// </summary>
    public static bool TryMarkCaught(SaveFile sav, ushort species)
    {
        if (sav.GetCaught(species))
            return true;
        try
        {
            GetOps(sav)?.Caught(species);
        }
        catch (Exception)
        {
            // species not present in this game's Pokédex
        }
        return sav.GetCaught(species);
    }

    private static DexOps? GetOps(SaveFile sav)
    {
        var language = (LanguageID)Math.Max(1, sav.Language);
        switch (sav)
        {
            case SAV1 or SAV2 or SAV3 or SAV6XY or SAV6AO:
                return new(
                    s => { sav.SetSeen(s, true); sav.SetCaught(s, false); },
                    s => { sav.SetSeen(s, true); sav.SetCaught(s, true); },
                    s => { sav.SetCaught(s, false); sav.SetSeen(s, false); });
            case SAV4 s4:
            {
                var z = s4.Dex;
                return new(s => { z.SetDexEntryAll(s); z.SetCaught(s, false); }, s => z.SetDexEntryAll(s), z.ClearDexEntryAll);
            }
            case SAV5 s5:
            {
                var z = s5.Zukan;
                var table = sav.Personal;
                return new(s => { z.ClearSeen(s); z.SetCaught(s, false); z.CompleteSeen(s, false, table[s]); },
                    s => z.GiveAll(s, true, false, language, false),
                    s => { z.SetCaught(s, false); z.ClearSeen(s); });
            }
            case SAV7 s7:
                return FromZukan7(s7.Zukan);
            case SAV7b b:
                return FromZukan7(b.Zukan);
            case SAV8SWSH swsh:
            {
                var z = swsh.Zukan;
                return new(s => { z.SetDexEntryAll(s); z.SetCaught(s, false); }, s => z.SetDexEntryAll(s), z.ClearDexEntryAll);
            }
            case SAV8BS bs:
            {
                var z = bs.Zukan;
                return new(s => z.SetState(s, ZukanState8b.Seen), s => z.SetDexEntryAll(s), z.ClearDexEntryAll);
            }
            case SAV9SV sv:
                return new(s => sv.Zukan.SetDexEntryAll(s), s => sv.Zukan.SetDexEntryAll(s), sv.Zukan.ClearDexEntryAll, ExactSeen: false);
            case SAV9ZA za:
                return new(s => za.Zukan.SetDexEntryAll(s), s => za.Zukan.SetDexEntryAll(s), za.Zukan.ClearDexEntryAll, ExactSeen: false);
            default:
                return null;
        }

        static DexOps FromZukan7(Zukan7 z) => new(s => { z.SetDexEntryAll(s); z.SetCaught(s, false); }, s => z.SetDexEntryAll(s), z.ClearDexEntryAll);
    }

    private sealed class DexRow(ushort species, string name, bool seen, bool caught) : INotifyPropertyChanged
    {
        public ushort Species { get; } = species;
        public string Name { get; } = $"{species:000} {name}";
        public bool OriginalSeen { get; } = seen;
        public bool OriginalCaught { get; } = caught;

        public bool Seen
        {
            get;
            set { field = value; PropertyChanged?.Invoke(this, new(nameof(Seen))); }
        } = seen;

        public bool Caught
        {
            get;
            set { field = value; PropertyChanged?.Invoke(this, new(nameof(Caught))); }
        } = caught;

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public static Window Create(SaveFile sav)
    {
        var ops = GetOps(sav) ?? throw new NotSupportedException("The Pokédex of this game can't be edited here yet.");
        var names = GameInfo.Strings.specieslist;
        var rows = new List<DexRow>(sav.MaxSpeciesID);
        for (ushort s = 1; s <= sav.MaxSpeciesID; s++)
            rows.Add(new DexRow(s, s < names.Length ? names[s] : $"#{s}", sav.GetSeen(s), sav.GetCaught(s)));

        var list = new ListBox
        {
            ItemTemplate = new FuncDataTemplate<DexRow>((_, _) =>
            {
                var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Width = 220 };
                name.Bind(TextBlock.TextProperty, new Binding(nameof(DexRow.Name)));
                var seen = new CheckBox { Content = "Seen", Margin = new(8, 0) };
                seen.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(DexRow.Seen)) { Mode = BindingMode.TwoWay });
                var caught = new CheckBox { Content = "Caught", Margin = new(8, 0) };
                caught.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(DexRow.Caught)) { Mode = BindingMode.TwoWay });
                return new StackPanel { Orientation = Orientation.Horizontal, Children = { name, seen, caught } };
            }),
            ItemsSource = rows,
        };

        var summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
        void UpdateSummary() => summary.Text = $"Seen: {rows.Count(z => z.Seen)} · Caught: {rows.Count(z => z.Caught)} / {rows.Count}";
        foreach (var r in rows)
            r.PropertyChanged += (_, _) => UpdateSummary();
        UpdateSummary();

        var filter = new TextBox { Watermark = "Filter by name or number…", Width = 240 };
        filter.TextChanged += (_, _) =>
        {
            var text = filter.Text?.Trim() ?? string.Empty;
            list.ItemsSource = text.Length == 0 ? rows : rows.Where(z => z.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        };

        IEnumerable<DexRow> Visible() => list.ItemsSource as IEnumerable<DexRow> ?? rows;
        var top = Ui.Row(
            filter,
            Ui.Button("Seen all", (_, _) => { foreach (var r in Visible()) r.Seen = true; }),
            Ui.Button("Seen none", (_, _) => { foreach (var r in Visible()) { r.Seen = false; r.Caught = false; } }),
            Ui.Button("Caught all", (_, _) => { foreach (var r in Visible()) { r.Seen = true; r.Caught = true; } }),
            Ui.Button("Caught none", (_, _) => { foreach (var r in Visible()) r.Caught = false; }),
            summary);

        var panel = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(top);
        if (!ops.ExactSeen)
        {
            var note = new TextBlock { Text = "In this game, marking a Pokémon as seen registers its full entry (seen and caught).", Opacity = 0.7, Margin = new(0, 0, 0, 4) };
            DockPanel.SetDock(note, Dock.Top);
            panel.Children.Add(note);
        }
        panel.Children.Add(list);

        return Ui.ToolWindow($"Pokédex — {GameInfo.GetVersionName(sav.Version)}", panel, () =>
        {
            foreach (var r in rows)
            {
                if (r.Seen == r.OriginalSeen && r.Caught == r.OriginalCaught)
                    continue;
                try
                {
                    if (r.Caught)
                        ops.Caught(r.Species);
                    else if (r.Seen)
                        ops.Seen(r.Species);
                    else
                        ops.Clear(r.Species);
                }
                catch (Exception)
                {
                    // Species not present in this game's Pokédex; skip.
                }
            }
            if (sav is SAV3 s3)
                s3.MirrorSeenFlags();
            sav.State.Edited = true;
        }, 900, 640);
    }
}
