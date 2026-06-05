using Avalonia.Controls;
using Avalonia.Media;

namespace Docklys.ModuleContracts;

// A Plugin is a self-contained settings-panel extension — the configuration
// sibling of IModule (a dock tile) and IPattern (a window background). The host
// loads plugin DLLs from %AppData%/Docklys/Plugin and lists each one on the
// Settings ▸ Plugins page, where the plugin renders its own name + settings UI.
public interface IPlugin
{
    string PluginName { get; }
    string PluginVersion { get; }
    string UniquePluginId { get; }

    void SetPluginId(string uniquePluginId);

    // One-line description shown beneath the plugin's name on the Plugins page.
    string PluginDescription => string.Empty;

    // Build the plugin's settings UI. The host places it inside a card on the
    // Plugins page (below the plugin's name). ctx supplies the active skin's
    // colors plus a persistence bag so the plugin can save/restore its settings.
    Control CreateSettingsView(PluginContext ctx);
}

// Skin color tokens + a per-plugin persistence bag resolved by the host and
// handed to a plugin when its settings view is built. The color fields mirror
// PatternContext so plugins can theme themselves to the host skin even when
// previewed outside the app (e.g. in the RunPlugin editor).
public sealed class PluginContext
{
    // ColorColorBackground
    public Color Color1 { get; set; } = Colors.Gray;
    // ColorColor2Background
    public Color Color2 { get; set; } = Colors.DarkGray;
    // ColorColor3Background — a neutral "ink"/foreground color.
    public Color Color3 { get; set; } = Colors.White;
    // ColorAccent
    public Color Accent { get; set; } = Colors.DodgerBlue;

    // Per-plugin persistent settings. The host backs these with a small JSON file
    // at %AppData%/Docklys/Plugin/<UniquePluginId>.settings.json. GetSetting
    // returns null when a key has never been written; SetSetting persists
    // immediately (pass null to remove a key).
    public Func<string, string?> GetSetting { get; set; } = _ => null;
    public Action<string, string?> SetSetting { get; set; } = (_, _) => { };
}
