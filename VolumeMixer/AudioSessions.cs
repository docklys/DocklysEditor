using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace VolumeMixer;

internal interface IAudioSession
{
    string Id { get; }
    string DisplayName { get; }
    string GroupKey { get; }
    string? IconName { get; }
    int? ProcessId { get; }
    float Volume { get; }
    void SetVolume(float volume);
}

internal interface IAudioSessionBackend
{
    IReadOnlyList<IAudioSession> GetSessions();
}

/// <summary>
/// Public modules must not launch native tools. The Windows API backend remains available
/// through NAudio; other platforms return an empty, usable UI until Docklys provides a
/// host-owned audio-session bridge.
/// </summary>
internal static class AudioSessionBackend
{
    internal static readonly IAudioSessionBackend? Current = OperatingSystem.IsWindows()
        ? new WindowsAudioSessionBackend()
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
    public string? IconName => null;
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
