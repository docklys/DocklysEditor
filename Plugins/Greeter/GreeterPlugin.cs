using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Docklys.ModuleContracts;

namespace DocklysPlugins;

// The bundled example plugin shipped with the editor (the sibling of the example
// patterns under DocklysModuleEditor/Patterns). It is what the RunPlugin carousel
// shows first, and what "Push to Docklys" copies into %AppData%/Docklys/Plugin so
// the app's Settings ▸ Plugins page lists it as an installed plugin.
//
// It exercises the whole IPlugin shape: name / version / description, plus a
// settings view with several controls that persist through ctx.SetSetting and a
// live preview that re-renders on every change.
public sealed class GreeterPlugin : IPlugin
{
    public string PluginName => "Greeter";
    public string PluginVersion => "1.0";
    public string UniquePluginId { get; private set; } = "plugin.greeter";
    public string PluginDescription => "Compose a greeting — every control persists and updates the preview live.";

    public void SetPluginId(string uniquePluginId) => UniquePluginId = uniquePluginId;

    public Control CreateSettingsView(PluginContext ctx) => new GreeterView(ctx);
}

internal sealed class GreeterView : UserControl
{
    private readonly PluginContext _ctx;
    private readonly TextBlock _preview = new();

    private const string KeyGreeting = "greeting";
    private const string KeyName = "name";
    private const string KeySize = "size";
    private const string KeyUpper = "upper";
    private const string KeyBold = "bold";

    public GreeterView(PluginContext ctx)
    {
        _ctx = ctx;

        var text = new SolidColorBrush(ctx.Color3);
        var accent = new SolidColorBrush(ctx.Accent);

        var greeting = new TextBox
        {
            Text = ctx.GetSetting(KeyGreeting) ?? "Hello",
            Watermark = "Greeting",
            Width = 240,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        greeting.TextChanged += (_, _) => { _ctx.SetSetting(KeyGreeting, greeting.Text); UpdatePreview(); };

        var name = new TextBox
        {
            Text = ctx.GetSetting(KeyName) ?? "world",
            Watermark = "Name",
            Width = 240,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        name.TextChanged += (_, _) => { _ctx.SetSetting(KeyName, name.Text); UpdatePreview(); };

        var size = new Slider
        {
            Minimum = 12,
            Maximum = 48,
            Value = ParseDouble(ctx.GetSetting(KeySize), 24),
            Width = 240,
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = accent,
        };
        size.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty) return;
            _ctx.SetSetting(KeySize, size.Value.ToString("F0", CultureInfo.InvariantCulture));
            UpdatePreview();
        };

        var upper = new CheckBox
        {
            Content = "UPPERCASE",
            IsChecked = ParseBool(ctx.GetSetting(KeyUpper)),
            Foreground = text,
        };
        upper.IsCheckedChanged += (_, _) => { _ctx.SetSetting(KeyUpper, Flag(upper.IsChecked)); UpdatePreview(); };

        var bold = new CheckBox
        {
            Content = "Bold",
            IsChecked = ParseBool(ctx.GetSetting(KeyBold)),
            Foreground = text,
        };
        bold.IsCheckedChanged += (_, _) => { _ctx.SetSetting(KeyBold, Flag(bold.IsChecked)); UpdatePreview(); };

        _preview.Foreground = accent;
        _preview.TextWrapping = TextWrapping.Wrap;

        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Label("Greeting", text), greeting,
                Label("Name", text), name,
                Label("Font size", text), size,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Children = { upper, bold } },
                Label("Preview", text),
                new Border
                {
                    Background = new SolidColorBrush(ctx.Color2),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12),
                    Child = _preview,
                },
            },
        };

        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var greeting = _ctx.GetSetting(KeyGreeting) ?? "Hello";
        var name = _ctx.GetSetting(KeyName) ?? "world";
        var s = $"{greeting}, {name}!";
        if (ParseBool(_ctx.GetSetting(KeyUpper))) s = s.ToUpperInvariant();

        _preview.Text = s;
        _preview.FontSize = ParseDouble(_ctx.GetSetting(KeySize), 24);
        _preview.FontWeight = ParseBool(_ctx.GetSetting(KeyBold)) ? FontWeight.Bold : FontWeight.Normal;
    }

    private static TextBlock Label(string s, IBrush brush)
        => new() { Text = s, Foreground = brush, FontSize = 12, Opacity = 0.85 };

    private static string Flag(bool? b) => b == true ? "true" : "false";

    private static double ParseDouble(string? s, double fallback)
        => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static bool ParseBool(string? s)
        => string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
}
