using System.Diagnostics.CodeAnalysis;

namespace Docklys.ModuleContracts;

/// <summary>
/// Host-owned Android device mirroring boundary. Modules consume decoded frames and submit
/// validated user input; only the Docklys host may interact with native tools or transports.
/// </summary>
public interface IDeviceMirrorService
{
    Task<IDeviceMirrorSession> StartAsync(DeviceMirrorStartOptions options, CancellationToken cancellationToken = default);
}

public interface IDeviceMirrorSession : IAsyncDisposable
{
    string DeviceName { get; }
    int VideoWidth { get; }
    int VideoHeight { get; }

    event Action<DeviceMirrorFrame>? FrameReady;
    event Action<string>? StatusChanged;

    Task SendTouchAsync(DeviceMirrorTouchAction action, int x, int y, float pressure, CancellationToken cancellationToken = default);
    Task SendScrollAsync(int x, int y, float horizontal, float vertical, CancellationToken cancellationToken = default);
    Task SendKeyAsync(DeviceMirrorKey key, CancellationToken cancellationToken = default);
    Task SendTextAsync(string text, CancellationToken cancellationToken = default);
}

public sealed record DeviceMirrorStartOptions
{
    public int MaxSize { get; init; } = 1024;
    public int MaxFps { get; init; } = 60;
    public int VideoBitRate { get; init; } = 8_000_000;
}

public sealed record DeviceMirrorFrame(byte[] BgraPixels, int Width, int Height);

public enum DeviceMirrorTouchAction { Down, Move, Up }

public enum DeviceMirrorKey { Backspace, Enter, Back }

/// <summary>Read-only module access to services explicitly provided by the Docklys host.</summary>
public static class DocklysHostServices
{
    private static IDeviceMirrorService? _deviceMirror;

    public static bool TryGetDeviceMirror([NotNullWhen(true)] out IDeviceMirrorService? service)
    {
        service = Volatile.Read(ref _deviceMirror);
        return service is not null;
    }

    internal static void ConfigureDeviceMirror(IDeviceMirrorService? service) =>
        Interlocked.Exchange(ref _deviceMirror, service);
}
