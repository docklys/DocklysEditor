using System.Diagnostics;
using System.Globalization;
using NAudio.CoreAudioApi;
using Newtonsoft.Json.Linq;

namespace VolumeMixer;

/// <summary>
/// Represents one application playback stream independently of the host audio API.
/// Linux exposes these as PulseAudio "sink inputs"; PipeWire provides the same API
/// through pipewire-pulse.
/// </summary>
internal interface IAudioSession
{
    string Id { get; }
    string DisplayName { get; }
    string GroupKey { get; }
    int? ProcessId { get; }
    float Volume { get; }
    void SetVolume(float volume);
}

internal interface IAudioSessionBackend
{
    IReadOnlyList<IAudioSession> GetSessions();
}

internal static class AudioSessionBackend
{
    internal static readonly IAudioSessionBackend? Current = OperatingSystem.IsWindows()
        ? new WindowsAudioSessionBackend()
        : OperatingSystem.IsLinux()
            ? new PactlAudioSessionBackend()
            : null;
}

internal sealed class WindowsAudioSessionBackend : IAudioSessionBackend
{
    public IReadOnlyList<IAudioSession> GetSessions()
    {
        var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;
        var result = new List<IAudioSession>(sessions.Count);
        for (var index = 0; index < sessions.Count; index++)
            result.Add(new WindowsAudioSession(sessions[index]));
        return result;
    }
}

internal sealed class WindowsAudioSession : IAudioSession
{
    private readonly AudioSessionControl _session;

    public WindowsAudioSession(AudioSessionControl session) => _session = session;
    public string Id => _session.GetSessionIdentifier;
    public int? ProcessId => _session.GetProcessID == 0 ? null : (int)_session.GetProcessID;
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_session.DisplayName)) return _session.DisplayName;
            try { return ProcessId is int id ? Process.GetProcessById(id).ProcessName : "Unknown"; }
            catch { return "Unknown"; }
        }
    }
    public string GroupKey
    {
        get
        {
            try { return ProcessId is int id ? Process.GetProcessById(id).ProcessName.ToLowerInvariant() : DisplayName.ToLowerInvariant(); }
            catch { return DisplayName.ToLowerInvariant(); }
        }
    }
    public float Volume => _session.SimpleAudioVolume.Volume;
    public void SetVolume(float volume) => _session.SimpleAudioVolume.Volume = Math.Clamp(volume, 0f, 1f);
}

internal sealed class PactlAudioSessionBackend : IAudioSessionBackend
{
    public IReadOnlyList<IAudioSession> GetSessions()
    {
        var json = RunPactl("-f", "json", "list", "sink-inputs");
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<IAudioSession>();

        try
        {
            var inputs = JArray.Parse(json);
            var result = new List<IAudioSession>(inputs.Count);
            foreach (var input in inputs.OfType<JObject>())
            {
                var id = input.Value<long?>("index");
                if (id is null) continue;

                var properties = input["properties"] as JObject;
                var name = FirstNonEmpty(
                    properties?.Value<string>("application.name"),
                    properties?.Value<string>("media.name"),
                    properties?.Value<string>("node.name"),
                    properties?.Value<string>("application.process.binary"),
                    "Unknown");
                var binary = FirstNonEmpty(
                    properties?.Value<string>("application.process.binary"),
                    properties?.Value<string>("application.name"),
                    name);
                var pidText = properties?.Value<string>("application.process.id");
                var processId = int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
                    ? pid
                    : (int?)null;
                var volume = ReadVolume(input["volume"] as JObject);
                result.Add(new PactlAudioSession(id.Value.ToString(CultureInfo.InvariantCulture), name, binary, processId, volume));
            }
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VolumeMixer] Could not parse pactl sink inputs: {ex.Message}");
            return Array.Empty<IAudioSession>();
        }
    }

    private static float ReadVolume(JObject? volume)
    {
        var channel = volume?.Properties().FirstOrDefault()?.Value as JObject;
        var raw = channel?.Value<double?>("value");
        return raw is null ? 0f : Math.Clamp((float)(raw.Value / 65536d), 0f, 1f);
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && value != "(null)") ?? "Unknown";

    internal static void SetVolume(string id, float volume)
    {
        var percent = (Math.Clamp(volume, 0f, 1f) * 100f).ToString("0.#", CultureInfo.InvariantCulture) + "%";
        RunPactl("set-sink-input-volume", id, percent);
    }

    private static string? RunPactl(params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("pactl")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(2_000) || process.ExitCode != 0)
            {
                Debug.WriteLine($"[VolumeMixer] pactl failed: {error}");
                return null;
            }
            return output;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VolumeMixer] pactl is unavailable: {ex.Message}");
            return null;
        }
    }
}

internal sealed class PactlAudioSession : IAudioSession
{
    private readonly float _volume;
    public PactlAudioSession(string id, string displayName, string groupKey, int? processId, float volume)
    {
        Id = id;
        DisplayName = displayName;
        GroupKey = groupKey.ToLowerInvariant();
        ProcessId = processId;
        _volume = volume;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string GroupKey { get; }
    public int? ProcessId { get; }
    public float Volume => _volume;
    public void SetVolume(float volume) => PactlAudioSessionBackend.SetVolume(Id, volume);
}
