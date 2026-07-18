using System;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Dockly.Services.DeviceMirroring;

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
        // RunModule is a development host. Register the exact same reviewed
        // integration boundary as Dockly so privileged features preview normally.
        EnsureSharedContractsLoaded();
        DeviceMirroringHostRegistration.Configure();

#if LINUX
        // Before the first module webview is constructed, so none can slip through unhooked.
        WebViewNavigationHost.Install();
#endif

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void EnsureSharedContractsLoaded()
    {
        const string contractsAssemblyName = "Docklys.ModuleContracts";
        if (AssemblyLoadContext.Default.Assemblies.Any(assembly =>
                string.Equals(assembly.GetName().Name, contractsAssemblyName, StringComparison.Ordinal)))
            return;

        var contractsPath = Path.Combine(AppContext.BaseDirectory, contractsAssemblyName + ".dll");
        if (!File.Exists(contractsPath))
            throw new FileNotFoundException("RunModule requires its shared module contracts assembly.", contractsPath);

        AssemblyLoadContext.Default.LoadFromAssemblyPath(contractsPath);
    }
}
