using Xunit;

namespace Scrcpy.Tests;

public sealed class ScrcpyTypeTests
{
    [Fact]
    public void Module_type_is_loadable_without_constructing_an_Avalonia_control()
    {
        var moduleType = typeof(scrcpy.Scrcpy);

        Assert.Equal("Scrcpy", moduleType.Name);
        Assert.Equal("scrcpy", moduleType.Namespace);
    }
}
