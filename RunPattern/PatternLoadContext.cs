using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace RunPattern;

// Isolated, collectible ALC for one pattern DLL — copy of RunModule's
// ModuleLoadContext. All dependency resolution returns null so the runtime
// falls through to the default context (Avalonia + Docklys.ModuleContracts use
// the host's type identities). Only the pattern DLL is loaded here, from a
// MemoryStream so the file on disk is never locked — that is what lets a freshly
// rebuilt pattern hot-reload.
internal sealed class PatternLoadContext : AssemblyLoadContext
{
    private readonly string _dllPath;

    internal PatternLoadContext(string dllPath) : base(isCollectible: true) => _dllPath = dllPath;

    internal Assembly LoadPattern()
    {
        using var ms = new MemoryStream(File.ReadAllBytes(_dllPath));
        return LoadFromStream(ms);
    }

    protected override Assembly? Load(AssemblyName assemblyName) => null;
}
