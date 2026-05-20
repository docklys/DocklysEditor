using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace RunModule;

public partial class App : Application
{
    public static SkinHost? Skins { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        try
        {
            Skins = SkinHost.Create(AppContext.BaseDirectory);
            if (Skins != null)
            {
                var requested = SkinHost.LoadPersistedSkinName();
                Skins.ApplySkin(requested);
            }
            else
            {
                Console.WriteLine("[RunModule] No Skins folder reachable from " + AppContext.BaseDirectory
                                  + " — falling back to RunModule/App.axaml fallback resources.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RunModule] Skin init failed: {ex.Message}");
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
