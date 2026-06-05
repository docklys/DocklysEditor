using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace RunPlugin;

// Isolated, collectible ALC for one plugin DLL — copy of RunPattern's
// PatternLoadContext. All dependency resolution returns null so the runtime
// falls through to the default context (Avalonia + Docklys.ModuleContracts use
// the host's type identities). Only the plugin DLL is loaded here, from a
// MemoryStream so the file on disk is never locked — that is what lets a freshly
// rebuilt plugin hot-reload.
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly string _dllPath;

    internal PluginLoadContext(string dllPath) : base(isCollectible: true) => _dllPath = dllPath;

    internal Assembly LoadPlugin()
    {
        using var ms = new MemoryStream(File.ReadAllBytes(_dllPath));
        return LoadFromStream(ms);
    }

    protected override Assembly? Load(AssemblyName assemblyName) => null;
}
