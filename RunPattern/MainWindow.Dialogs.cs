using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace RunPattern;

// Small programmatic modal dialogs, styled to the same dark monochrome palette
// as the main window — a direct port of RunModule's dialog helpers so the
// pattern editor's Rename / New / Delete flows look and feel identical.
public partial class MainWindow
{
    private sealed record NewPatternSpec(string Name, int TemplateIndex);

    // Apply the dark theme + neutral accent so Fluent's blue focus rings come
    // out matching the toolbar.
    internal static void StyleDialog(Window w)
    {
        w.RequestedThemeVariant = ThemeVariant.Dark;
        w.Background = new SolidColorBrush(Color.Parse("#1A1A1A"));
        w.Foreground = Brushes.White;

        w.Resources["SystemAccentColor"] = Color.Parse("#888888");
        w.Resources["SystemAccentColorLight1"] = Color.Parse("#9A9A9A");
        w.Resources["SystemAccentColorLight2"] = Color.Parse("#ABABAB");
        w.Resources["SystemAccentColorLight3"] = Color.Parse("#BCBCBC");
        w.Resources["SystemAccentColorDark1"] = Color.Parse("#777777");
        w.Resources["SystemAccentColorDark2"] = Color.Parse("#666666");
        w.Resources["SystemAccentColorDark3"] = Color.Parse("#555555");
        w.Resources["ColorAccent"] = Color.Parse("#E7EAED");
        w.Resources["ColorAccentBrush"] = new SolidColorBrush(Color.Parse("#E7EAED"));
        w.Resources["SystemControlHighlightAccentBrush"] = new SolidColorBrush(Color.Parse("#E7EAED"));
        w.Resources["SystemControlHighlightAccentBrush2"] = new SolidColorBrush(Color.Parse("#C7CDD3"));
        w.Resources["SystemControlForegroundBaseHighBrush"] = Brushes.White;
        w.Resources["SystemControlForegroundBaseMediumBrush"] = new SolidColorBrush(Color.Parse("#C7CDD3"));
    }

    // Step 1 (New): choose a template + name in one dialog.
    private async Task<NewPatternSpec?> PromptForNewPatternSpec(string patternsDir)
    {
        var tcs = new TaskCompletionSource<NewPatternSpec?>();

        var templateBox = new ComboBox
        {
            Width = 320,
            ItemsSource = TemplateNames,
            SelectedIndex = 0,
        };
        var nameBox = new TextBox { Width = 320, Watermark = "e.g. MyPattern" };
        var hint = new TextBlock
        {
            Text = "Letters, digits, and underscores. Must start with a letter or underscore.",
            FontSize = 11,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        var okBtn = new Button { Content = "Create", IsDefault = true, Padding = new Thickness(16, 4) };
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            Padding = new Thickness(16, 4),
            Margin = new Thickness(8, 0, 0, 0),
        };

        var window = new Window
        {
            Title = "Create New Pattern",
            Width = 480,
            MinWidth = 380,
            MinHeight = 220,
            SizeToContent = SizeToContent.Height,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "Template", Foreground = Brushes.White },
                    templateBox,
                    new TextBlock { Text = "Pattern name", Foreground = Brushes.White, Margin = new Thickness(0, 12, 0, 0) },
                    nameBox,
                    hint,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 16, 0, 0),
                        Children = { okBtn, cancel },
                    },
                },
            },
        };
        StyleDialog(window);

        okBtn.Click += async (_, _) =>
        {
            var raw = nameBox.Text?.Trim() ?? "";
            var (ok, reason) = ValidatePatternName(raw, patternsDir);
            if (!ok) { await ShowMessageDialog("Invalid name", reason!); nameBox.Focus(); return; }
            tcs.TrySetResult(new NewPatternSpec(raw, Math.Max(0, templateBox.SelectedIndex)));
            window.Close();
        };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); window.Close(); };
        window.Closed += (_, _) => tcs.TrySetResult(null);
        nameBox.AttachedToVisualTree += (_, _) => nameBox.Focus();

        await window.ShowDialog(this);
        return await tcs.Task;
    }

    // Single-line name prompt (used by Rename).
    private async Task<string?> PromptForPatternName(string? initial = null, string title = "Rename Pattern")
    {
        var tcs = new TaskCompletionSource<string?>();

        var textbox = new TextBox { Width = 320, Watermark = "e.g. MyPattern", Text = initial ?? "" };
        var hint = new TextBlock
        {
            Text = "Letters, digits, and underscores. Must start with a letter or underscore.",
            FontSize = 11,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        var okBtn = new Button { Content = "Rename", IsDefault = true, Padding = new Thickness(16, 4) };
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            Padding = new Thickness(16, 4),
            Margin = new Thickness(8, 0, 0, 0),
        };

        var window = new Window
        {
            Title = title,
            Width = 400,
            MinWidth = 320,
            MinHeight = 160,
            SizeToContent = SizeToContent.Height,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "Pattern name", Foreground = Brushes.White },
                    textbox,
                    hint,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 16, 0, 0),
                        Children = { okBtn, cancel },
                    },
                },
            },
        };
        StyleDialog(window);

        okBtn.Click += (_, _) => { tcs.TrySetResult(textbox.Text); window.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); window.Close(); };
        window.Closed += (_, _) => tcs.TrySetResult(null);
        textbox.AttachedToVisualTree += (_, _) =>
        {
            textbox.Focus();
            if (!string.IsNullOrEmpty(textbox.Text)) textbox.SelectAll();
        };

        await window.ShowDialog(this);
        return await tcs.Task;
    }

    // Multi-line instruction + input (used by Delete's 1234 confirmation).
    private async Task<string?> PromptForCode(string title, string message)
    {
        var tcs = new TaskCompletionSource<string?>();

        var label = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White };
        var textbox = new TextBox { Width = 220, Watermark = "1234" };
        var okBtn = new Button { Content = "Confirm", IsDefault = true, Padding = new Thickness(16, 4) };
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            Padding = new Thickness(16, 4),
            Margin = new Thickness(8, 0, 0, 0),
        };

        var window = new Window
        {
            Title = title,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 8,
                Children =
                {
                    label,
                    textbox,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 12, 0, 0),
                        Children = { okBtn, cancel },
                    },
                },
            },
        };
        StyleDialog(window);

        okBtn.Click += (_, _) => { tcs.TrySetResult(textbox.Text); window.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); window.Close(); };
        window.Closed += (_, _) => tcs.TrySetResult(null);
        textbox.AttachedToVisualTree += (_, _) => textbox.Focus();

        await window.ShowDialog(this);
        return await tcs.Task;
    }

    // Scrollable message box with a Copy button.
    private async Task ShowMessageDialog(string title, string message)
    {
        var tcs = new TaskCompletionSource<bool>();

        var copyBtn = new Button { Content = "Copy", Padding = new Thickness(14, 4) };
        var ok = new Button
        {
            Content = "OK",
            IsDefault = true,
            IsCancel = true,
            Padding = new Thickness(20, 4),
            Margin = new Thickness(8, 0, 0, 0),
        };

        var buttonRow = new Grid
        {
            Margin = new Thickness(0, 12, 0, 0),
            ColumnDefinitions = new ColumnDefinitions("Auto, *, Auto"),
        };
        Grid.SetColumn(copyBtn, 0);
        Grid.SetColumn(ok, 2);
        buttonRow.Children.Add(copyBtn);
        buttonRow.Children.Add(ok);

        var scrollViewer = new ScrollViewer
        {
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White },
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        var dockPanel = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
        DockPanel.SetDock(buttonRow, Dock.Bottom);
        dockPanel.Children.Add(buttonRow);
        dockPanel.Children.Add(scrollViewer);

        var window = new Window
        {
            Title = title,
            Width = 460,
            MinWidth = 340,
            MinHeight = 140,
            MaxHeight = 620,
            SizeToContent = SizeToContent.Height,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = dockPanel,
        };
        StyleDialog(window);

        copyBtn.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(window)?.Clipboard;
            if (clipboard != null) await clipboard.SetTextAsync(message);
            copyBtn.Content = "Copied";
            await Task.Delay(1400);
            if (window.IsVisible) copyBtn.Content = "Copy";
        };
        ok.Click += (_, _) => { tcs.TrySetResult(true); window.Close(); };
        window.Closed += (_, _) => tcs.TrySetResult(true);

        await window.ShowDialog(this);
        await tcs.Task;
    }
}
