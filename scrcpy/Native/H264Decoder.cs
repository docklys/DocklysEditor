using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace scrcpy.Native
{
    /// <summary>
    /// Decodes the Annex-B H.264 stream to BGRA frames.
    ///
    /// This drives ffmpeg over pipes rather than binding libavcodec directly: the system
    /// ships libavcodec 62 (ffmpeg 8.x), and the available managed bindings pin older
    /// soversions, so P/Invoke would break against the installed libraries. Pipes are
    /// version-independent and the copy cost is negligible at tile resolution.
    /// </summary>
    internal sealed class H264Decoder : IDisposable
    {
        private readonly Process _ffmpeg;
        private readonly Stream _stdin;
        private readonly Stream _stdout;
        private readonly int _frameBytes;

        public int Width { get; }
        public int Height { get; }

        private H264Decoder(Process ffmpeg, int width, int height)
        {
            _ffmpeg = ffmpeg;
            _stdin = ffmpeg.StandardInput.BaseStream;
            _stdout = ffmpeg.StandardOutput.BaseStream;
            Width = width;
            Height = height;
            _frameBytes = width * height * 4;
        }

        public static string? FindFfmpeg()
        {
            var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
            var env = Environment.GetEnvironmentVariable("FFMPEG_PATH");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var candidate = Path.Combine(dir, exe);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        public static H264Decoder Start(int width, int height, int fps)
        {
            var ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException(
                "ffmpeg was not found on PATH; it is required to decode the phone's H.264 stream.");

            var psi = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // These flags are load-bearing, not decoration. On ffmpeg's defaults the demuxer
            // probes 5MB/5s before emitting anything, so the tile sits on a ~5s-stale frame
            // and taps look like they did nothing.
            //
            // The reason the tiny probesize is safe here is -framerate: the h264 demuxer's
            // only reason to keep probing is to estimate the frame rate ("not enough frames
            // to estimate rate"), and stating it up front removes that need.
            //
            // Do NOT add -fflags nobuffer. It is the one flag that silently yields zero
            // output ("Output file is empty, nothing was encoded"), verified both on a
            // captured stream and live against a device.
            //
            // The explicit scale guarantees each output frame is exactly width*height*4
            // bytes, which is what ReadFrameAsync counts on.
            foreach (var a in new[]
                     {
                         "-hide_banner", "-loglevel", "error",
                         "-probesize", "32", "-analyzeduration", "0",
                         "-flags", "low_delay",
                         "-f", "h264", "-framerate", fps.ToString(), "-i", "pipe:0",
                         "-f", "rawvideo", "-pix_fmt", "bgra",
                         "-vf", $"scale={width}:{height}",
                         "pipe:1",
                     })
                psi.ArgumentList.Add(a);

            var proc = Process.Start(psi)
                       ?? throw new InvalidOperationException("Failed to start ffmpeg.");

            proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AdbClient.Log($"[ffmpeg] {e.Data}"); };
            proc.BeginErrorReadLine();

            return new H264Decoder(proc, width, height);
        }

        public async Task FeedAsync(byte[] data, CancellationToken ct)
        {
            await _stdin.WriteAsync(data, ct).ConfigureAwait(false);
            await _stdin.FlushAsync(ct).ConfigureAwait(false);
        }

        /// <summary>Reads exactly one decoded BGRA frame, or null if ffmpeg closed its output.</summary>
        public async Task<byte[]?> ReadFrameAsync(CancellationToken ct)
        {
            var frame = new byte[_frameBytes];
            var offset = 0;
            while (offset < _frameBytes)
            {
                var read = await _stdout.ReadAsync(frame.AsMemory(offset), ct).ConfigureAwait(false);
                if (read == 0) return null;
                offset += read;
            }
            return frame;
        }

        public void Dispose()
        {
            try { _stdin.Dispose(); } catch { }
            try { if (!_ffmpeg.WaitForExit(500)) _ffmpeg.Kill(entireProcessTree: true); } catch { }
            try { _ffmpeg.Dispose(); } catch { }
        }
    }
}
