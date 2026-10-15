using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using PKHeX.Core;

namespace PKHeX.Avalonia;

/// <summary>
/// Small helpers for building code-only UI and simple dialogs.
/// </summary>
public static class Ui
{
    #region ComboBox

    public static void SetSource(ComboBox cb, IReadOnlyList<ComboItem> list)
    {
        cb.DisplayMemberBinding ??= new Binding(nameof(ComboItem.Text));
        cb.ItemsSource = list;
    }

    public static int GetValue(ComboBox cb) => cb.SelectedItem is ComboItem item ? item.Value : 0;

    /// <summary>
    /// Selects the entry with the given value. If the list doesn't contain it, a placeholder entry is added
    /// so that saving never silently replaces a value the editor doesn't know about.
    /// </summary>
    public static void SetValue(ComboBox cb, int value)
    {
        if (cb.ItemsSource is not IReadOnlyList<ComboItem> list)
            return;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Value != value)
                continue;
            cb.SelectedIndex = i;
            return;
        }
        var extended = new List<ComboItem>(list.Count + 1) { new(value == 0 ? "(None)" : $"(Unknown: {value})", value) };
        extended.AddRange(list);
        cb.ItemsSource = extended;
        cb.SelectedIndex = 0;
    }

    public static ComboBox Combo(IReadOnlyList<ComboItem>? source = null)
    {
        var cb = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new(0, 2) };
        if (source is not null)
            SetSource(cb, source);
        return cb;
    }

    #endregion

    #region Layout

    public static NumericUpDown Number(decimal min, decimal max, decimal value = 0) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = value,
        FormatString = "0",
        Increment = 1,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Margin = new(0, 2),
    };

    public static TextBlock Label(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new(0, 0, 8, 0),
    };

    public static TextBlock Header(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.Bold,
        Margin = new(0, 8, 0, 4),
    };

    public static Button Button(string text, System.EventHandler<global::Avalonia.Interactivity.RoutedEventArgs> click)
    {
        var b = new Button { Content = text };
        b.Click += click;
        return b;
    }

    public static StackPanel Row(params Control[] children)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new(0, 4) };
        sp.Children.AddRange(children);
        return sp;
    }

    /// <summary>
    /// A two-column form (label, editor).
    /// </summary>
    public sealed class Form
    {
        public Grid Grid { get; } = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new(4) };

        public T Add<T>(string label, T control) where T : Control
        {
            var row = Grid.RowDefinitions.Count;
            Grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var l = Label(label);
            Grid.SetRow(l, row);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            Grid.Children.Add(l);
            Grid.Children.Add(control);
            return control;
        }

        public T AddWide<T>(T control) where T : Control
        {
            var row = Grid.RowDefinitions.Count;
            Grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(control, row);
            Grid.SetColumnSpan(control, 2);
            Grid.Children.Add(control);
            return control;
        }
    }

    #endregion

    #region Dialogs

    public static Task ShowMessage(Window owner, string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 680,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        var ok = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true, Margin = new(0, 12, 0, 0) };
        ok.Click += (_, _) => dialog.Close();
        var text = new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        var panel = new DockPanel { Margin = new(16) };
        DockPanel.SetDock(ok, Dock.Bottom);
        panel.Children.Add(ok);
        panel.Children.Add(new ScrollViewer { Content = text, MaxHeight = 560 });
        dialog.Content = panel;
        return dialog.ShowDialog(owner);
    }

    public static async Task<bool> Confirm(Window owner, string title, string message)
    {
        bool result = false;
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        var yes = new Button { Content = "Yes", IsDefault = true };
        var no = new Button { Content = "No", IsCancel = true };
        yes.Click += (_, _) => { result = true; dialog.Close(); };
        no.Click += (_, _) => dialog.Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0), Children = { yes, no } };
        var panel = new DockPanel { Margin = new(16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        dialog.Content = panel;
        await dialog.ShowDialog(owner);
        return result;
    }

    /// <summary>
    /// Shows a multi-line text box. Returns the entered text, or null if cancelled.
    /// </summary>
    public static async Task<string?> PromptText(Window owner, string title, string description, string initial = "", bool readOnly = false)
    {
        string? result = null;
        var dialog = new Window
        {
            Title = title,
            Width = 620,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var box = new TextBox
        {
            Text = initial,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            IsReadOnly = readOnly,
            FontFamily = new FontFamily("monospace"),
        };
        var ok = new Button { Content = readOnly ? "Close" : "OK", IsDefault = !readOnly };
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsVisible = !readOnly };
        ok.Click += (_, _) => { result = box.Text ?? string.Empty; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0), Children = { ok, cancel } };
        var top = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) };
        var panel = new DockPanel { Margin = new(16) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(top);
        panel.Children.Add(buttons);
        panel.Children.Add(box);
        dialog.Content = panel;
        await dialog.ShowDialog(owner);
        return result;
    }

    /// <summary>
    /// Creates a standard tool window with Save/Cancel buttons at the bottom.
    /// </summary>
    public static Window ToolWindow(string title, Control content, System.Action onSave, double width = 760, double height = 600)
    {
        var w = new Window
        {
            Title = title,
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var save = new Button { Content = "Save", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        save.Click += (_, _) => { onSave(); w.Close(true); };
        cancel.Click += (_, _) => w.Close(false);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 8, 0, 0), Children = { save, cancel } };
        var panel = new DockPanel { Margin = new(12) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(content);
        w.Content = panel;
        return w;
    }

    #endregion
}
