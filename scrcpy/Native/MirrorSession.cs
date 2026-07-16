using System;
using System.Threading;
using System.Threading.Tasks;

namespace scrcpy.Native
{
    /// <summary>
    /// Owns the full pipeline: adb -> server -> sockets -> decoder -> frames,
    /// and exposes the control channel for input injection.
    /// </summary>
    internal sealed class MirrorSession : IAsyncDisposable
    {
        private readonly ScrcpyOptions _options;
        private readonly CancellationTokenSource _cts = new();

        private AdbClient? _adb;
        private ScrcpyConnection? _connection;
        private H264Decoder? _decoder;
        private Task? _videoLoop;
        private Task? _decodeLoop;

        public event Action<byte[], int, int>? FrameReady;
        public event Action<string>? StatusChanged;

        public ControlChannel? Control { get; private set; }
        public int VideoWidth { get; private set; }
        public int VideoHeight { get; private set; }
        public string DeviceName { get; private set; } = "";

        public MirrorSession(ScrcpyOptions options) => _options = options;

        public async Task StartAsync()
        {
            var ct = _cts.Token;

            StatusChanged?.Invoke("Looking for device…");
            _adb = AdbClient.Connect();
            if (_adb == null)
            {
                var reason = AdbClient.FindAdb() == null
                    ? "adb not found on PATH"
                    : "no device (enable USB debugging)";
                StatusChanged?.Invoke(reason);
                throw new InvalidOperationException(reason);
            }

            if (H264Decoder.FindFfmpeg() == null)
            {
                StatusChanged?.Invoke("ffmpeg not found on PATH");
                throw new InvalidOperationException("ffmpeg not found on PATH");
            }

            StatusChanged?.Invoke("Starting server…");
            _adb.PushServer();

            var scid = Random.Shared.Next(1, int.MaxValue).ToString("x8");
            var port = _adb.Forward(scid);
            _adb.StartServer(scid, _options);

            _connection = await ScrcpyConnection.ConnectAsync(port, ct).ConfigureAwait(false);
            DeviceName = _connection.DeviceName;
            Control = new ControlChannel(_connection.ControlSocket);

            if (_connection.CodecId != "h264")
                throw new InvalidOperationException($"Unexpected codec '{_connection.CodecId}'.");

            StatusChanged?.Invoke($"Connected to {DeviceName}");
            _videoLoop = Task.Run(() => VideoLoopAsync(ct), ct);
        }

        private async Task VideoLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var packet = await _connection!.ReadPacketAsync(OnSession, ct).ConfigureAwait(false);

                    if (_decoder == null)
                    {
                        // The session packet always precedes the first media packet, so by
                        // here the dimensions are known.
                        if (VideoWidth == 0 || VideoHeight == 0) continue;
                        StartDecoder(ct);
                    }

                    await _decoder!.FeedAsync(packet.Data, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AdbClient.Log($"video loop ended: {ex.Message}");
                StatusChanged?.Invoke("Disconnected");
            }
        }

        private void OnSession(int width, int height)
        {
            AdbClient.Log($"session: {width}x{height}");
            if (width == VideoWidth && height == VideoHeight) return;

            VideoWidth = width;
            VideoHeight = height;

            // A rotation or display resize restarts the encoder on the device, so the
            // decoder has to be rebuilt around the new geometry.
            if (_decoder != null)
            {
                _decoder.Dispose();
                _decoder = null;
            }
        }

        private void StartDecoder(CancellationToken ct)
        {
            _decoder = H264Decoder.Start(VideoWidth, VideoHeight, _options.MaxFps);
            var decoder = _decoder;
            var w = VideoWidth;
            var h = VideoHeight;
            _decodeLoop = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var frame = await decoder.ReadFrameAsync(ct).ConfigureAwait(false);
                        if (frame == null) break;
                        FrameReady?.Invoke(frame, w, h);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { AdbClient.Log($"decode loop ended: {ex.Message}"); }
            }, ct);
        }

        public async ValueTask DisposeAsync()
        {
            try { _cts.Cancel(); } catch { }

            foreach (var t in new[] { _videoLoop, _decodeLoop })
            {
                if (t == null) continue;
                try { await t.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            }

            _decoder?.Dispose();
            _connection?.Dispose();
            _adb?.Dispose();
            _cts.Dispose();
        }
    }
}
