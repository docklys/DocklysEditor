using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace scrcpy.Native
{
    internal readonly struct MediaPacket
    {
        public readonly bool IsConfig;
        public readonly bool IsKeyFrame;
        public readonly long Pts;
        public readonly byte[] Data;

        public MediaPacket(bool isConfig, bool isKeyFrame, long pts, byte[] data)
        {
            IsConfig = isConfig;
            IsKeyFrame = isKeyFrame;
            Pts = pts;
            Data = data;
        }
    }

    /// <summary>
    /// Owns the video and control sockets and demuxes the video stream.
    ///
    /// Wire format (verified against scrcpy-server 4.0):
    ///   forward tunnel: one dummy byte on the first socket opened
    ///   then 64 bytes device name, then 4 bytes codec id ("h264")
    ///   then a sequence of 12-byte-headed packets:
    ///     - session packet: MSB of the first u32 set -> u32 flags, u32 width, u32 height
    ///     - media packet:   u64 (bit62 = config, bit61 = key, low 61 bits = pts), u32 size
    /// </summary>
    internal sealed class ScrcpyConnection : IDisposable
    {
        private readonly Socket _video;
        private readonly Socket _control;
        private readonly NetworkStream _videoStream;

        public string DeviceName { get; }
        public string CodecId { get; }
        public Socket ControlSocket => _control;

        private ScrcpyConnection(Socket video, Socket control, string deviceName, string codecId)
        {
            _video = video;
            _control = control;
            _videoStream = new NetworkStream(video, ownsSocket: false);
            DeviceName = deviceName;
            CodecId = codecId;
        }

        public static async Task<ScrcpyConnection> ConnectAsync(int port, CancellationToken ct)
        {
            var video = await ConnectVideoAsync(port, ct).ConfigureAwait(false);

            var control = await ConnectWithRetryAsync(port, ct).ConfigureAwait(false);
            control.NoDelay = true;

            var nameBuf = new byte[64];
            await ReadExactAsync(video, nameBuf, ct).ConfigureAwait(false);
            var deviceName = System.Text.Encoding.UTF8.GetString(nameBuf).TrimEnd('\0');

            var codecBuf = new byte[4];
            await ReadExactAsync(video, codecBuf, ct).ConfigureAwait(false);
            var codecId = System.Text.Encoding.ASCII.GetString(codecBuf);

            AdbClient.Log($"connected: device='{deviceName}' codec='{codecId}'");
            return new ScrcpyConnection(video, control, deviceName, codecId);
        }

        /// <summary>
        /// Opens the video socket and consumes the tunnel's dummy byte.
        ///
        /// With a forward tunnel, adb accepts the local TCP connection even when nothing is
        /// listening on the device yet and then drops it, so a successful connect() proves
        /// nothing. The dummy byte is the real readiness signal: retry the whole
        /// connect-then-read until it arrives.
        /// </summary>
        private static async Task<Socket> ConnectVideoAsync(int port, CancellationToken ct)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var sock = await ConnectWithRetryAsync(port, ct).ConfigureAwait(false);
                try
                {
                    var dummy = new byte[1];
                    await ReadExactAsync(sock, dummy, ct).ConfigureAwait(false);
                    if (dummy[0] != 0x00)
                        AdbClient.Log($"warning: unexpected dummy byte 0x{dummy[0]:x2}");
                    return sock;
                }
                catch (EndOfStreamException)
                {
                    // Device side not listening yet; adb closed the connection.
                    sock.Dispose();
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
            }
            throw new IOException($"The scrcpy server never became ready on port {port}.");
        }

        private static async Task<Socket> ConnectWithRetryAsync(int port, CancellationToken ct)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await sock.ConnectAsync("127.0.0.1", port, ct).ConfigureAwait(false);
                    return sock;
                }
                catch (SocketException)
                {
                    sock.Dispose();
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
            }
            throw new IOException($"Could not connect to the scrcpy server on port {port}.");
        }

        /// <summary>
        /// Reads the next packet. Session packets are reported through <paramref name="onSession"/>
        /// and reading continues until a media packet arrives.
        /// </summary>
        public async Task<MediaPacket> ReadPacketAsync(Action<int, int> onSession, CancellationToken ct)
        {
            var header = new byte[12];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await ReadExactAsync(_videoStream, header, ct).ConfigureAwait(false);

                if ((header[0] & 0x80) != 0)
                {
                    var width  = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
                    var height = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
                    onSession(width, height);
                    continue;
                }

                var flagsPts = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(0, 8));
                var size     = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));

                var isConfig = (flagsPts & (1UL << 62)) != 0;
                var isKey    = (flagsPts & (1UL << 61)) != 0;
                var pts      = (long)(flagsPts & ((1UL << 61) - 1));

                if (size < 0 || size > 64 * 1024 * 1024)
                    throw new IOException($"Implausible packet size {size}; stream is out of sync.");

                var data = new byte[size];
                await ReadExactAsync(_videoStream, data, ct).ConfigureAwait(false);
                return new MediaPacket(isConfig, isKey, pts, data);
            }
        }

        private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("The scrcpy video socket closed.");
                offset += read;
            }
        }

        private static async Task ReadExactAsync(Socket socket, byte[] buffer, CancellationToken ct)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await socket.ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None, ct)
                                       .ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("The scrcpy socket closed during handshake.");
                offset += read;
            }
        }

        public void Dispose()
        {
            try { _videoStream.Dispose(); } catch { }
            try { _video.Dispose(); } catch { }
            try { _control.Dispose(); } catch { }
        }
    }
}
