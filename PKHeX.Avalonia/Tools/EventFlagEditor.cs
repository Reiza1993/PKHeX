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
/// Event flag and event work (constant) editor for games that store them as simple arrays.
/// </summary>
public static class EventFlagEditor
{
    private sealed record FlagSource(string Name, int Count, Func<int, bool> Get, Action<int, bool> Set);
    private sealed record WorkSource(string Name, int Count, long Max, Func<int, long> Get, Action<int, long> Set);

    private sealed class FlagRow(int index, string label, bool value) : INotifyPropertyChanged
    {
        public int Index { get; } = index;
        public string Label { get; } = label;
        public string Display => Label.Length == 0 ? $"{Index:0000}" : $"{Index:0000} — {Label}";
        public bool Original { get; } = value;
        public bool Value
        {
            get;
            set { field = value; PropertyChanged?.Invoke(this, new(nameof(Value))); }
        } = value;
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class WorkRow(int index, string label, long value)
    {
        public int Index { get; } = index;
        public string Label { get; } = label;
        public long Original { get; } = value;
        public long Value { get; set; } = value;
        public override string ToString() => Label.Length == 0 ? $"{Index:0000} = {Value}" : $"{Index:0000} — {Label} = {Value}";
    }

    public static bool IsSupported(SaveFile sav) => GetFlagSources(sav).Count != 0;

    private static List<FlagSource> GetFlagSources(SaveFile sav)
    {
        var result = new List<FlagSource>();
        switch (sav)
        {
            case IEventFlagArray a:
                result.Add(new("Event flags", a.EventFlagCount, a.GetEventFlag, a.SetEventFlag));
                break;
            case IEventFlagProvider37 p:
                result.Add(new("Event flags", p.EventWork.EventFlagCount, p.EventWork.GetEventFlag, p.EventWork.SetEventFlag));
                break;
            case SAV7b b:
                result.Add(new("Event flags", b.EventWork.CountFlag, b.EventWork.GetFlag, (i, v) => b.EventWork.SetFlag(i, v)));
                break;
            case SAV8BS bs:
                result.Add(new("Event flags", bs.FlagWork.CountFlag, bs.FlagWork.GetFlag, (i, v) => bs.FlagWork.SetFlag(i, v)));
                result.Add(new("System flags", bs.FlagWork.CountSystem, bs.FlagWork.GetSystemFlag, (i, v) => bs.FlagWork.SetSystemFlag(i, v)));
                break;
        }
        return result;
    }

    private static WorkSource? GetWorkSource(SaveFile sav)
    {
        static WorkSource U16(IEventWorkArray<ushort> w) => new("Event work", w.EventWorkCount, ushort.MaxValue, i => w.GetWork(i), (i, v) => w.SetWork(i, (ushort)v));
        static WorkSource U8(IEventWorkArray<byte> w) => new("Event work", w.EventWorkCount, byte.MaxValue, i => w.GetWork(i), (i, v) => w.SetWork(i, (byte)v));
        static WorkSource I32(IEventWork<int> w) => new("Event work", w.CountWork, int.MaxValue, i => w.GetWork(i), (i, v) => w.SetWork(i, (int)v));
        return sav switch
        {
            IEventWorkArray<ushort> w => U16(w),
            IEventWorkArray<byte> w => U8(w),
            IEventFlagProvider37 p => U16(p.EventWork),
            SAV7b b => I32(b.EventWork),
            SAV8BS bs => I32(bs.FlagWork),
            _ => null,
        };
    }

    private static EventLabelCollection? GetLabels(SaveFile sav, int flagCount, int workCount)
    {
        var game = sav.Version switch
        {
            GameVersion.X or GameVersion.Y => "xy",
            GameVersion.OR or GameVersion.AS => "oras",
            GameVersion.SN or GameVersion.MN => "sm",
            GameVersion.US or GameVersion.UM => "usum",
            GameVersion.D or GameVersion.P => "dp",
            GameVersion.Pt => "pt",
            GameVersion.HG or GameVersion.SS => "hgss",
            GameVersion.B or GameVersion.W => "bw",
            GameVersion.B2 or GameVersion.W2 => "b2w2",
            GameVersion.R or GameVersion.S => "rs",
            GameVersion.E => "e",
            GameVersion.FR or GameVersion.LG => "frlg",
            GameVersion.C => "c",
            GameVersion.GD or GameVersion.SI => "gs",
            GameVersion.GP or GameVersion.GE => "gg",
            GameVersion.BD or GameVersion.SP => "bdsp",
            _ => null,
        };
        if (game is null)
            return null;
        try { return new EventLabelCollection(game, flagCount, workCount); }
        catch { return null; }
    }

    public static Window Create(SaveFile sav)
    {
        var flagSources = GetFlagSources(sav);
        var work = GetWorkSource(sav);
        var labels = GetLabels(sav, flagSources.FirstOrDefault()?.Count ?? 0, work?.Count ?? 0);
        var flagLabels = labels?.Flag.ToDictionary(z => z.Index, z => z.Name) ?? [];
        var workLabels = labels?.Work.ToDictionary(z => z.Index, z => z.Name) ?? [];

        var tabs = new TabControl();
        var allFlagRows = new List<(FlagSource Source, List<FlagRow> Rows)>();
        foreach (var src in flagSources)
        {
            var useLabels = ReferenceEquals(src, flagSources[0]);
            var rows = Enumerable.Range(0, src.Count)
                .Select(i => new FlagRow(i, useLabels && flagLabels.TryGetValue(i, out var l) ? l : string.Empty, src.Get(i)))
                .ToList();
            allFlagRows.Add((src, rows));
            tabs.Items.Add(new TabItem { Header = src.Name, Content = CreateFlagView(rows) });
        }

        List<WorkRow>? workRows = null;
        if (work is { Count: > 0 })
        {
            workRows = Enumerable.Range(0, work.Count)
                .Select(i => new WorkRow(i, workLabels.TryGetValue(i, out var l) ? l : string.Empty, work.Get(i)))
                .ToList();
            tabs.Items.Add(new TabItem { Header = work.Name, Content = CreateWorkView(workRows, work.Max) });
        }

        return Ui.ToolWindow($"Event Flags — {GameInfo.GetVersionName(sav.Version)}", tabs, () =>
        {
            foreach (var (src, rows) in allFlagRows)
            {
                foreach (var r in rows.Where(z => z.Value != z.Original))
                    src.Set(r.Index, r.Value);
            }
            if (work is not null && workRows is not null)
            {
                foreach (var r in workRows.Where(z => z.Value != z.Original))
                    work.Set(r.Index, r.Value);
            }
            sav.State.Edited = true;
        }, 820, 640);
    }

    private static Control CreateFlagView(List<FlagRow> rows)
    {
        var list = new ListBox
        {
            ItemTemplate = new FuncDataTemplate<FlagRow>((_, _) =>
            {
                var chk = new CheckBox();
                chk.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(FlagRow.Value)) { Mode = BindingMode.TwoWay });
                chk.Bind(ContentControl.ContentProperty, new Binding(nameof(FlagRow.Display)));
                return chk;
            }),
            ItemsSource = rows,
        };
        var filter = new TextBox { Watermark = "Search by number or name…", Width = 260 };
        var onlyNamed = new CheckBox { Content = "Only named flags" };
        var onlySet = new CheckBox { Content = "Only set flags" };
        void Apply()
        {
            var text = filter.Text?.Trim() ?? string.Empty;
            IEnumerable<FlagRow> q = rows;
            if (onlyNamed.IsChecked == true)
                q = q.Where(z => z.Label.Length != 0);
            if (onlySet.IsChecked == true)
                q = q.Where(z => z.Value);
            if (text.Length != 0)
                q = q.Where(z => z.Display.Contains(text, StringComparison.OrdinalIgnoreCase));
            list.ItemsSource = q.ToList();
        }
        filter.TextChanged += (_, _) => Apply();
        onlyNamed.IsCheckedChanged += (_, _) => Apply();
        onlySet.IsCheckedChanged += (_, _) => Apply();

        var top = Ui.Row(filter, onlyNamed, onlySet);
        var panel = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(top);
        panel.Children.Add(list);
        return panel;
    }

    private static Control CreateWorkView(List<WorkRow> rows, long max)
    {
        var list = new ListBox { ItemsSource = rows };
        var value = Ui.Number(0, max);
        value.IsEnabled = false;
        bool loading = false;
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not WorkRow r)
            {
                value.IsEnabled = false;
                return;
            }
            loading = true;
            value.IsEnabled = true;
            value.Value = Math.Clamp(r.Value, 0, max);
            loading = false;
        };
        value.ValueChanged += (_, _) =>
        {
            if (loading || list.SelectedItem is not WorkRow r)
                return;
            r.Value = (long)(value.Value ?? 0);
            var sel = list.SelectedIndex;
            var src = list.ItemsSource;
            list.ItemsSource = null;
            list.ItemsSource = src;
            loading = true;
            list.SelectedIndex = sel;
            loading = false;
        };

        var filter = new TextBox { Watermark = "Search by number or name…", Width = 260 };
        filter.TextChanged += (_, _) =>
        {
            var text = filter.Text?.Trim() ?? string.Empty;
            list.ItemsSource = text.Length == 0 ? rows : rows.Where(z => z.ToString().Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        };

        var top = Ui.Row(filter, Ui.Label("Value of selected:"), value);
        value.Width = 180;
        var panel = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(top);
        panel.Children.Add(list);
        return panel;
    }
}
