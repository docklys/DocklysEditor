using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using System.Threading.Tasks;

namespace RunTheme;

public partial class MainWindow
{
    internal static void StyleDialog(Window w)
    {
        w.RequestedThemeVariant = ThemeVariant.Dark;
        w.Classes.Add("dialog");
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
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
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
