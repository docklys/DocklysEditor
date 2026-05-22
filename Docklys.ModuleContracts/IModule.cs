namespace Docklys.ModuleContracts
{
    public interface IModule
    {
        string ModuleName { get; }
        string ModuleVersion { get; }
        string UniqueModuleId { get; }
        void SetModuleId(string uniqueModuleId);
        void PrintModuleId();

        int PreferredTileWidth => 1;
        int PreferredTileHeight => 1;
        public class FontDummy
        {
        }
    }

    // Modules implementing this interface can request a runtime tile-size change.
    // The host subscribes to TileResizeRequested when placing the module, and calls
    // SetTileSize to inform the module of its current (possibly restored) tile size.
    public interface IResizable
    {
        event Action<int, int>? TileResizeRequested;
        void SetTileSize(int width, int height);
    }
}