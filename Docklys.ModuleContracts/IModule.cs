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

    // Modules that host native HWNDs (e.g. WebView2) implement this so the host can
    // hide the HWND while the settings panel is open, preventing the native window from
    // stealing pointer events during drag/reposition and in the module-list preview.
    public interface IInteractionFreezable
    {
        void FreezeInteraction();
        void UnfreezeInteraction();
    }
}