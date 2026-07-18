namespace Docklys.ModuleContracts;

public interface IModule
{
    string ModuleName { get; }
    string ModuleVersion { get; }
    string UniqueModuleId { get; }
    void SetModuleId(string uniqueModuleId);
    void PrintModuleId();
    int PreferredTileWidth => 1;
    int PreferredTileHeight => 1;
}

public interface IResizable
{
    event Action<int, int>? TileResizeRequested;
    void SetTileSize(int width, int height);
}
