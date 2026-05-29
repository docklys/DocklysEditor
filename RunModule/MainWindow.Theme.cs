using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace RunModule;

public partial class MainWindow
{
    private static readonly string[] EditableColorResources = {
        "ColorModuleColor",
        "ColorModuleFont",
        "ColorModuleAccentColor"
    };

    private void InitializeTheme()
    {
        var savedColors = SkinHost.LoadThemeColors();
        if (savedColors != null)
        {
            foreach (var kvp in savedColors)
            {
                ApplyThemeColor(kvp.Key, kvp.Value);
            }
        }
    }

    private void ThemeButton_Click(object? sender, RoutedEventArgs e)
    {
        PopulateThemeSettings();
        ThemeSettingsOverlay.IsVisible = true;
    }

    private void CloseTheme_Click(object? sender, RoutedEventArgs e)
    {
        ThemeSettingsOverlay.IsVisible = false;
    }

    private async void ResetTheme_Click(object? sender, RoutedEventArgs e)
    {
        SkinHost.SaveThemeColors(new Dictionary<string, string>());
        
        // We can't easily revert to "App.axaml defaults" once overwritten in the
        // global resources without restarting or reloading App.axaml. 
        // For now, inform the user a restart is needed to fully revert.
        await ShowMessageDialog("Reset Theme", 
            "Theme colors have been reset to defaults in AppData.\n\nPlease restart the application to apply the original App.axaml colors.");
        
        ThemeSettingsOverlay.IsVisible = false;
    }

    private void PopulateThemeSettings()
    {
        ThemeColorsContainer.Children.Clear();
        foreach (var resName in EditableColorResources)
        {
            var entry = CreateColorEntry(resName);
            ThemeColorsContainer.Children.Add(entry);
        }
    }

    private Control CreateColorEntry(string resName)
    {
        var currentBrush = Application.Current?.Resources[resName] as SolidColorBrush;
        var currentColor = currentBrush?.Color ?? Colors.White;

        var nameLabel = new TextBlock
        {
            Text = resName.Replace("Color", ""),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            FontSize = 11,
            FontWeight = FontWeight.Medium
        };
        nameLabel.Bind(TextBlock.ForegroundProperty, Application.Current!.GetResourceObservable("ColorModuleFont"));

        var colorPicker = new ColorPicker
        {
            Color = currentColor,
            Width = 40,
            Height = 24,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            // Simple mode or just a button that opens a flyout
            Palette = null
        };

        colorPicker.ColorChanged += (s, e) =>
        {
            ApplyThemeColor(resName, e.NewColor.ToString());
            SaveCurrentTheme();
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*, Auto"),
            Children = { nameLabel, colorPicker }
        };
        Grid.SetColumn(nameLabel, 0);
        Grid.SetColumn(colorPicker, 1);

        var border = new Border
        {
            Padding = new Thickness(12, 8),
            Margin = new Thickness(0, 0, 0, 4),
            CornerRadius = new CornerRadius(4),
            Child = grid
        };
        border.Bind(Border.BackgroundProperty, Application.Current.GetResourceObservable("ColorModuleAccentColor"));

        return border;
    }

    private void ApplyThemeColor(string resName, string hex)
    {
        if (Application.Current == null) return;
        try
        {
            var color = Color.Parse(hex);
            Application.Current.Resources[resName] = new SolidColorBrush(color);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Theme] Failed to apply color {hex} to {resName}: {ex.Message}");
        }
    }

    private void SaveCurrentTheme()
    {
        var colors = new Dictionary<string, string>();
        foreach (var resName in EditableColorResources)
        {
            if (Application.Current?.Resources[resName] is SolidColorBrush brush)
            {
                colors[resName] = brush.Color.ToString();
            }
        }
        SkinHost.SaveThemeColors(colors);
    }
    // (PromptForColorHex removed as ColorPicker is used)
}
