using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace scrcpy.Native
{
    /// <summary>
    /// Thin wrapper over the adb executable: device discovery, pushing the bundled
    /// scrcpy-server, setting up the forward tunnel and launching the server process.
    /// </summary>
    internal sealed class AdbClient : IDisposable
    {
        // The server refuses to run unless this matches the jar in Assets exactly.
        public const string ServerVersion = "4.0";

        private const string DeviceJarPath = "/data/local/tmp/scrcpy-server.jar";

        private readonly string _adb;
        private readonly string _serial;
        private Process? _server;
        private int _forwardedPort = -1;

        private AdbClient(string adb, string serial)
        {
            _adb = adb;
            _serial = serial;
        }

        public string Serial => _serial;

        public static string? FindAdb()
        {
            var candidates = new List<string>();
            var env = Environment.GetEnvironmentVariable("ADB_PATH");
            if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env);

            var exe = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                candidates.Add(Path.Combine(dir, exe));
            }

            if (OperatingSystem.IsWindows())
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                candidates.Add(Path.Combine(local, "Android", "Sdk", "platform-tools", exe));
            }
            else
            {
                candidates.Add("/usr/bin/adb");
                candidates.Add("/usr/local/bin/adb");
            }

            return candidates.FirstOrDefault(File.Exists);
        }

        /// <summary>Returns the serials of all devices in the "device" state.</summary>
        public static IReadOnlyList<string> ListDevices(string adb)
        {
            var output = RunCapture(adb, null, "devices");
            var serials = new List<string>();
            foreach (var line in output.Split('\n').Skip(1))
            {
                var parts = line.Trim().Split('\t');
                if (parts.Length >= 2 && parts[1].Trim() == "device")
                    serials.Add(parts[0].Trim());
            }
            return serials;
        }

        public static AdbClient? Connect(string? preferredSerial = null)
        {
            var adb = FindAdb();
            if (adb == null) return null;

            var devices = ListDevices(adb);
            if (devices.Count == 0) return null;

            var serial = preferredSerial != null && devices.Contains(preferredSerial)
                ? preferredSerial
                : devices[0];
            return new AdbClient(adb, serial);
        }

        /// <summary>Writes the jar embedded in this DLL to a temp file and pushes it to the device.</summary>
        public void PushServer()
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"docklys-scrcpy-server-{ServerVersion}.jar");

            var asm = Assembly.GetExecutingAssembly();
            var resource = asm.GetManifestResourceNames()
                              .FirstOrDefault(n => n.EndsWith("scrcpy-server", StringComparison.Ordinal));
            if (resource == null)
                throw new InvalidOperationException("Embedded scrcpy-server resource is missing from the module DLL.");

            using (var src = asm.GetManifestResourceStream(resource)!)
            using (var dst = File.Create(tmp))
                src.CopyTo(dst);

            var result = RunCapture(_adb, _serial, "push", tmp, DeviceJarPath);
            if (!result.Contains("pushed") && !result.Contains("bytes"))
                throw new InvalidOperationException($"adb push failed: {result}");
        }

        /// <summary>
        /// Binds a free local TCP port to the device-side abstract socket. Returns the local port.
        /// </summary>
        public int Forward(string scid)
        {
            var port = FreeTcpPort();
            var result = RunCapture(_adb, _serial, "forward", $"tcp:{port}", $"localabstract:scrcpy_{scid}");
            if (result.Contains("error") || result.Contains("cannot"))
                throw new InvalidOperationException($"adb forward failed: {result}");
            _forwardedPort = port;
            return port;
        }

        public void StartServer(string scid, ScrcpyOptions options)
        {
            // Order and spelling follow the real client's invocation; the server parses key=value.
            var args = string.Join(' ', new[]
            {
                $"scid={scid}",
                "log_level=info",
                "video=true",
                "audio=false",
                "control=true",
                "tunnel_forward=true",
                "video_codec=h264",
                $"max_size={options.MaxSize}",
                $"max_fps={options.MaxFps}",
                $"video_bit_rate={options.VideoBitRate}",
                "clipboard_autosync=false",
                // cleanup=true makes the server restore device state when the socket drops,
                // which matters because the tile can be closed without a graceful shutdown.
                "cleanup=true",
            });

            var cmd = $"CLASSPATH={DeviceJarPath} app_process / com.genymobile.scrcpy.Server {ServerVersion} {args}";

            var psi = new ProcessStartInfo(_adb)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(_serial);
            psi.ArgumentList.Add("shell");
            psi.ArgumentList.Add(cmd);

            _server = Process.Start(psi);
            if (_server == null)
                throw new InvalidOperationException("Failed to start the scrcpy server process.");

            _server.OutputDataReceived += (_, e) => { if (e.Data != null) Log($"[server] {e.Data}"); };
            _server.ErrorDataReceived  += (_, e) => { if (e.Data != null) Log($"[server] {e.Data}"); };
            _server.BeginOutputReadLine();
            _server.BeginErrorReadLine();
        }

        public bool ServerAlive => _server is { HasExited: false };

        private static int FreeTcpPort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static string RunCapture(string adb, string? serial, params string[] args)
        {
            var psi = new ProcessStartInfo(adb)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (serial != null)
            {
                psi.ArgumentList.Add("-s");
                psi.ArgumentList.Add(serial);
            }
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            return stdout + stderr;
        }

        internal static void Log(string message) => Console.WriteLine($"[scrcpy] {message}");

        public void Dispose()
        {
            try
            {
                if (_server is { HasExited: false }) _server.Kill(entireProcessTree: true);
                _server?.Dispose();
            }
            catch { /* the process may already be gone */ }

            if (_forwardedPort > 0)
            {
                try { RunCapture(_adb, _serial, "forward", "--remove", $"tcp:{_forwardedPort}"); }
                catch { /* best effort */ }
                _forwardedPort = -1;
            }
        }
    }

    internal sealed class ScrcpyOptions
    {
        public int MaxSize { get; init; } = 1024;
        public int MaxFps { get; init; } = 60;
        public int VideoBitRate { get; init; } = 8_000_000;
    }
}
