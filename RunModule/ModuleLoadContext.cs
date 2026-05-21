using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace RunModule;

// Isolated, collectible ALC for one module DLL.
//
// All dependency resolution returns null so the runtime falls through to
// AssemblyLoadContext.Default — Avalonia, Docklys.ModuleContracts, NAudio,
// etc. all use the same type identities as the host. Only the module DLL
// itself is loaded into this context, via LoadFromStream so the file on disk
// is never locked.
//
// isCollectible=true means Unload() can reclaim the module's types from
// memory, which is what makes hot-reload work: unload the old context, create
// a new one pointing at the freshly-built DLL, done.
internal sealed class ModuleLoadContext : AssemblyLoadContext
{
    private readonly string _dllPath;

    internal ModuleLoadContext(string dllPath) : base(isCollectible: true)
    {
        _dllPath = dllPath;
    }

    internal Assembly LoadModule()
    {
        using var ms = new MemoryStream(File.ReadAllBytes(_dllPath));
        return LoadFromStream(ms);
    }

    protected override Assembly? Load(AssemblyName assemblyName) => null;
}
