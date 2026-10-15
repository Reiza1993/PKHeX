using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PKHeX.Core;

namespace PKHeX.Avalonia;

/// <summary>
/// Batch editor: applies PKHeX batch instructions (e.g. <c>=Species=25</c>, <c>.Nickname=Pika</c>) to many Pokémon at once.
/// </summary>
public static class BatchEditorWindow
{
    private const string Help =
        "One instruction per line. Prefixes:\n" +
        "  =Property=Value   filter: only edit Pokémon where Property equals Value\n" +
        "  !Property=Value   filter: only edit Pokémon where Property does NOT equal Value\n" +
        "  >Property=Value / <Property=Value   filter: greater / less than\n" +
        "  .Property=Value   set Property to Value (also +, -, * … to add, subtract, multiply)\n" +
        "Separate independent jobs with a line containing only ; (no blank lines).\n" +
        "Special values: $suggest, $rand, $shiny, $target … (same as the Windows batch editor).\n\n" +
        "Example (rename every Pikachu, then level everything to 100):\n" +
        "  =Species=25\n  .Nickname=Sparky\n  ;\n  .CurrentLevel=100";

    public static Window Create(SaveFile sav, int currentBox, Action onChanged)
    {
        var window = new Window
        {
            Title = "Batch Editor",
            Width = 760,
            Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var instructions = new TextBox
        {
            AcceptsReturn = true,
            FontFamily = new FontFamily("monospace"),
            Watermark = "=Species=25\n.CurrentLevel=100",
        };

        var editor = EntityBatchEditor.Instance;
        var properties = editor.Properties.SelectMany(z => z).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
        var propCombo = new ComboBox { ItemsSource = properties, Width = 260, IsTextSearchEnabled = true };
        var type = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
        propCombo.SelectionChanged += (_, _) =>
        {
            if (propCombo.SelectedItem is not string p)
                return;
            type.Text = editor.TryGetPropertyType(p, out var t) ? t : string.Empty;
        };
        void Insert(char prefix)
        {
            if (propCombo.SelectedItem is not string p)
                return;
            var text = instructions.Text ?? string.Empty;
            if (text.Length != 0 && !text.EndsWith('\n'))
                text += Environment.NewLine;
            instructions.Text = text + $"{prefix}{p}=";
            instructions.CaretIndex = instructions.Text.Length;
            instructions.Focus();
        }

        var scope = Ui.Combo([new("All boxes", 0), new($"Current box ({currentBox + 1})", 1), new("Party", 2), new("All boxes and party", 3)]);
        scope.SelectedIndex = 0;
        scope.Width = 220;
        if (!sav.HasParty)
            scope.ItemsSource = new List<ComboItem> { new("All boxes", 0), new($"Current box ({currentBox + 1})", 1) };
        if (!sav.HasBox)
            scope.ItemsSource = new List<ComboItem> { new("Party", 2) };
        scope.SelectedIndex = 0;

        var run = new Button { Content = "Run", IsDefault = true };
        run.Click += async (_, _) =>
        {
            var text = instructions.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                return;
            if (StringInstructionSet.HasEmptyLine(text))
            {
                await Ui.ShowMessage(window, "Batch Editor", "Remove empty lines from the instructions (use ; to separate jobs).");
                return;
            }
            StringInstructionSet[] sets;
            try { sets = StringInstructionSet.GetBatchSets(text); }
            catch (Exception ex)
            {
                await Ui.ShowMessage(window, "Batch Editor", $"Invalid instructions:\n{ex.Message}");
                return;
            }
            if (sets.Any(z => z.Instructions.Count == 0))
            {
                await Ui.ShowMessage(window, "Batch Editor", "Each job needs at least one .Property=Value instruction.");
                return;
            }
            foreach (var set in sets)
            {
                EntityBatchEditor.ScreenStrings(set.Filters);
                EntityBatchEditor.ScreenStrings(set.Instructions);
            }

            var processor = new EntityBatchProcessor();
            var mode = Ui.GetValue(scope);
            var modified = Run(sav, processor, sets, mode, currentBox);
            onChanged();
            await Ui.ShowMessage(window, "Batch Editor", processor.GetEditorResults(sets) + $"\n\n{modified} slot(s) written to the save.");
        };

        var help = new TextBlock { Text = Help, FontFamily = new FontFamily("monospace"), FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) };
        var top = Ui.Row(Ui.Label("Property"), propCombo,
            Ui.Button("= filter", (_, _) => Insert('=')),
            Ui.Button("! filter", (_, _) => Insert('!')),
            Ui.Button(". set", (_, _) => Insert('.')),
            type);
        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => window.Close();
        var bottom = Ui.Row(Ui.Label("Apply to"), scope, run, close);

        var panel = new DockPanel { Margin = new(12) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(help, Dock.Bottom);
        panel.Children.Add(top);
        panel.Children.Add(bottom);
        panel.Children.Add(help);
        panel.Children.Add(instructions);
        window.Content = panel;
        return window;
    }

    private static int Run(SaveFile sav, EntityBatchProcessor processor, StringInstructionSet[] sets, int mode, int currentBox)
    {
        var settings = default(EntityImportSettings) with { UpdateRecord = EntityImportOption.Disable };
        int written = 0;
        var max = sav.MaxSpeciesID;

        // Meta filters (box/slot position) are not supported here; drop them.
        static IReadOnlyList<StringInstruction> Strip(IReadOnlyList<StringInstruction> filters)
            => [.. filters.Where(f => !BatchFilters.FilterMeta.Any(m => m.IsMatch(f.PropertyName)))];

        bool Process(PKM pk)
        {
            if (pk.Species == 0 || pk.Species > max)
                return false;
            bool changed = false;
            foreach (var set in sets)
                changed |= processor.Process(pk, Strip(set.Filters), set.Instructions);
            return changed;
        }

        if (mode is 0 or 1 or 3 && sav.HasBox)
        {
            int start = mode == 1 ? currentBox : 0;
            int end = mode == 1 ? currentBox : sav.BoxCount - 1;
            for (int box = start; box <= end; box++)
            {
                for (int slot = 0; slot < sav.BoxSlotCount; slot++)
                {
                    if (sav.IsBoxSlotOverwriteProtected(box, slot))
                        continue;
                    var pk = sav.GetBoxSlotAtIndex(box, slot);
                    if (!Process(pk))
                        continue;
                    sav.SetBoxSlotAtIndex(pk, box, slot, settings);
                    written++;
                }
            }
        }
        if (mode is 2 or 3 && sav.HasParty)
        {
            for (int i = 0; i < sav.PartyCount; i++)
            {
                var pk = sav.GetPartySlotAtIndex(i);
                if (!Process(pk))
                    continue;
                sav.SetPartySlotAtIndex(pk, i, settings);
                written++;
            }
        }
        if (written != 0)
            sav.State.Edited = true;
        return written;
    }
}
