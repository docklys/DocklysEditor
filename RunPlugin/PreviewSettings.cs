using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RunPlugin;

// JSON-backed key/value bag scoped to one plugin id, used to give previewed
// plugins a real persistence target in the editor. Deliberately writes to the
// same %AppData%/Docklys/Plugin/<id>.settings.json the running app reads, so
// values tuned in RunPlugin survive a "Push to Docklys". Mirror of the host's
// PluginSettingsStore.
internal sealed class PreviewSettingsBag
{
    private readonly string _path;
    private readonly Dictionary<string, string> _values;

    private PreviewSettingsBag(string path, Dictionary<string, string> values)
    {
        _path = path;
        _values = values;
    }

    public static PreviewSettingsBag For(string id)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Docklys", "Plugin");
        try { Directory.CreateDirectory(dir); } catch { }

        var file = Path.Combine(dir, Sanitize(id) + ".settings.json");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(file))
                values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? values;
        }
        catch { /* a corrupt bag just starts empty */ }

        return new PreviewSettingsBag(file, values);
    }

    public string? Get(string key)
        => !string.IsNullOrEmpty(key) && _values.TryGetValue(key, out var v) ? v : null;

    public void Set(string key, string? value)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (value == null) _values.Remove(key);
        else _values[key] = value;
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_values)); }
        catch { /* best-effort in the editor */ }
    }

    private static string Sanitize(string id)
    {
        if (string.IsNullOrEmpty(id)) return "plugin";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(id.Length);
        foreach (var ch in id)
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        return sb.ToString();
    }
}
