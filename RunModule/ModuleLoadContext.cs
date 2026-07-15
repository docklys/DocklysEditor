using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace RunModule;

// Isolated, collectible ALC for one module DLL.
//
// Avalonia and Docklys.ModuleContracts deliberately come from the default
// context so a loaded module has the same Control and IModule type identities
// as the editor. Other dependencies are resolved relative to the module's
// build output. Returning null for every dependency used to work only for
// modules already referenced by RunModule; independently-built modules then
// failed during construction or rendered blank when one of their private
// dependencies could not be found.
//
// isCollectible=true means Unload() can reclaim the module's types from
// memory, which is what makes hot-reload work: unload the old context, create
// a new one pointing at the freshly-built DLL, done.
internal sealed class ModuleLoadContext : AssemblyLoadContext
{
    private readonly string _dllPath;
    private readonly AssemblyDependencyResolver _resolver;

    internal ModuleLoadContext(string dllPath) : base(isCollectible: true)
    {
        _dllPath = dllPath;
        _resolver = new AssemblyDependencyResolver(dllPath);
    }

    internal Assembly LoadModule()
    {
        using var ms = new MemoryStream(File.ReadAllBytes(_dllPath));
        return LoadFromStream(ms);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsSharedWithEditor(assemblyName.Name))
            return AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly =>
                string.Equals(assembly.GetName().Name, assemblyName.Name,
                    StringComparison.OrdinalIgnoreCase));

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path == null ? null : LoadFromAssemblyPath(path);
    }

    private static bool IsSharedWithEditor(string? assemblyName) =>
        assemblyName != null &&
        (assemblyName.Equals("Docklys.ModuleContracts", StringComparison.OrdinalIgnoreCase)
         || assemblyName.Equals("Avalonia", StringComparison.OrdinalIgnoreCase)
         || assemblyName.StartsWith("Avalonia.", StringComparison.OrdinalIgnoreCase)
         || assemblyName.Equals("WebView.Avalonia", StringComparison.OrdinalIgnoreCase)
         || assemblyName.StartsWith("WebViewCore", StringComparison.OrdinalIgnoreCase));
}
