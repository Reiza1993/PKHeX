using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace PKHeX.Avalonia;

/// <summary>
/// "Check for updates" window: shows what's new, installs the update with a live log, and offers a restart.
/// </summary>
public static class UpdateWindow
{
    /// <param name="restart">Called with the program to start once the update is installed.</param>
    public static Window Create(Func<string, Task> restart)
    {
        var window = new Window
        {
            Title = "Check for updates",
            Width = 680,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var mode = Updater.CurrentMode == Updater.Mode.Source
            ? $"Update method: rebuild from your source folder ({Updater.SourceDirectory}) using git and the .NET SDK."
            : $"Update method: download the latest build from github.com/{Updater.Repository}/releases.";
        var header = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = $"Installed: {Updater.VersionText}", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = mode, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 },
            },
        };
        var summary = new TextBlock { Text = "Checking…", FontWeight = FontWeight.SemiBold, FontSize = 15, Margin = new(0, 10, 0, 4), TextWrapping = TextWrapping.Wrap };
        var details = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("monospace") };

        var check = new Button { Content = "Check again" };
        var update = new Button { Content = "Update now", IsEnabled = false, IsDefault = true };
        var restartNow = new Button { Content = "Restart now", IsVisible = false };
        var close = new Button { Content = "Close", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 8, 0, 0), Children = { check, update, restartNow, close } };

        Updater.UpdateInfo? info = null;
        string? program = null;
        bool busy = false;

        async Task Check()
        {
            busy = true;
            check.IsEnabled = update.IsEnabled = false;
            summary.Text = "Checking…";
            details.Text = string.Empty;
            try
            {
                info = await Task.Run(() => Updater.CheckAsync());
                summary.Text = info.Summary;
                details.Text = info.Details;
                update.IsEnabled = info.Available;
            }
            catch (Exception ex)
            {
                summary.Text = "Couldn't check for updates.";
                details.Text = ex.Message;
            }
            finally
            {
                busy = false;
                check.IsEnabled = true;
            }
        }

        void Log(string line) => Dispatcher.UIThread.Post(() =>
        {
            details.Text += line + Environment.NewLine;
            details.CaretIndex = details.Text.Length;
        });

        check.Click += async (_, _) => await Check();
        update.Click += async (_, _) =>
        {
            if (info is null)
                return;
            busy = true;
            check.IsEnabled = update.IsEnabled = close.IsEnabled = false;
            summary.Text = "Updating…";
            details.Text = string.Empty;
            try
            {
                var current = info;
                program = await Task.Run(() => Updater.ApplyAsync(current, Log));
                summary.Text = "Update installed. Restart PKHeX to use the new version.";
                restartNow.IsVisible = true;
            }
            catch (Exception ex)
            {
                summary.Text = "The update failed — your current version is unchanged.";
                Log(string.Empty);
                Log(ex.Message);
                check.IsEnabled = true;
            }
            finally
            {
                busy = false;
                close.IsEnabled = true;
            }
        };
        restartNow.Click += async (_, _) =>
        {
            if (program is not null)
                await restart(program);
        };
        close.Click += (_, _) => window.Close();
        window.Closing += (_, e) => e.Cancel = busy;
        window.Opened += async (_, _) => await Check();

        var panel = new DockPanel { Margin = new(14) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(summary, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(header);
        panel.Children.Add(summary);
        panel.Children.Add(buttons);
        panel.Children.Add(details);
        window.Content = panel;
        return window;
    }
}
