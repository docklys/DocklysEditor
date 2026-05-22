using Avalonia;
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace RunModule;

class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        TryUseDesktopWebView(builder);
        return builder;
    }

    // Calls AppBuilderExtensions.UseDesktopWebView(builder) via reflection so
    // we don't need a compile-time using for Avalonia.WebView.Desktop, which
    // isn't exposed as a net9.0 compile target by WebView.Avalonia.Desktop 11.0.0.1.
    // The DLL is present in the output (via the package reference in the csproj)
    // so the runtime call always succeeds.
    private static void TryUseDesktopWebView(AppBuilder builder)
    {
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                          .FirstOrDefault(a => a.GetName().Name == "Avalonia.WebView.Desktop");

            if (asm == null)
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Avalonia.WebView.Desktop.dll");
                if (!File.Exists(path))
                {
                    Console.WriteLine("[RunModule] Avalonia.WebView.Desktop.dll not found — WebView unavailable.");
                    return;
                }
                asm = Assembly.LoadFrom(path);
            }

            // Target the known extension class directly rather than scanning all types,
            // which can silently drop types via ReflectionTypeLoadException.
            var extType = asm.GetType("Avalonia.WebView.Desktop.AppBuilderExtensions")
                       ?? asm.GetType("AvaloniaWebView.Desktop.AppBuilderExtensions");

            if (extType == null)
            {
                // Fallback: scan all loadable types
                Type[] allTypes;
                try { allTypes = asm.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { allTypes = rtle.Types.Where(t => t != null).ToArray()!; }

                extType = allTypes.FirstOrDefault(t =>
                    t?.GetMethod("UseDesktopWebView", BindingFlags.Public | BindingFlags.Static) != null);
            }

            if (extType == null)
            {
                Console.WriteLine("[RunModule] UseDesktopWebView extension type not found.");
                return;
            }

            // Don't filter by parameter types — the method takes extra params (e.g. bool isWslDevelop)
            // that would cause a name+types lookup to return null.
            var m = extType.GetMethod("UseDesktopWebView", BindingFlags.Public | BindingFlags.Static);

            if (m == null)
            {
                Console.WriteLine($"[RunModule] UseDesktopWebView method not found on {extType.FullName}.");
                return;
            }

            // Build arg list: AppBuilder first, then default values for any extra params.
            var parameters = m.GetParameters();
            var args = new object[parameters.Length];
            args[0] = builder;
            for (int i = 1; i < parameters.Length; i++)
            {
                args[i] = parameters[i].HasDefaultValue
                    ? parameters[i].DefaultValue!
                    : Activator.CreateInstance(parameters[i].ParameterType)!;
            }

            m.Invoke(null, args);
            Console.WriteLine($"[RunModule] UseDesktopWebView registered via {extType.FullName}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RunModule] UseDesktopWebView failed: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
                Console.WriteLine($"[RunModule]   inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }
    }
}
