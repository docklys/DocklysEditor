using System;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace scrcpy.Native
{
    internal enum ControlMsgType : byte
    {
        InjectKeycode = 0,
        InjectText = 1,
        InjectTouchEvent = 2,
        InjectScrollEvent = 3,
        BackOrScreenOn = 4,
        ExpandNotificationPanel = 5,
        ExpandSettingsPanel = 6,
        CollapsePanels = 7,
        GetClipboard = 8,
        SetClipboard = 9,
        SetDisplayPower = 10,
        RotateDevice = 11,
    }

    /// <summary>Android MotionEvent actions.</summary>
    internal enum TouchAction : byte
    {
        Down = 0,
        Up = 1,
        Move = 2,
    }

    /// <summary>Android KeyEvent actions.</summary>
    internal enum KeyAction : byte
    {
        Down = 0,
        Up = 1,
    }

    internal static class AndroidKeycode
    {
        public const int Back = 4;
        public const int Home = 3;
        public const int AppSwitch = 187;
        public const int VolumeUp = 24;
        public const int VolumeDown = 25;
        public const int Power = 26;
        public const int Del = 67;
        public const int Enter = 66;
    }

    /// <summary>
    /// Serialises control messages onto the control socket.
    /// Layouts verified against scrcpy 4.0's control_msg.c; all fields are big-endian.
    /// </summary>
    internal sealed class ControlChannel
    {
        // Signed -1: the server treats this pointer id as the mouse.
        private const long PointerIdMouse = -1L;

        private readonly Socket _socket;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public ControlChannel(Socket socket) => _socket = socket;

        private async Task SendAsync(byte[] payload, CancellationToken ct)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var sent = 0;
                while (sent < payload.Length)
                    sent += await _socket.SendAsync(payload.AsMemory(sent), SocketFlags.None, ct)
                                         .ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// INJECT_TOUCH_EVENT, 32 bytes:
        /// type(1) action(1) pointerId(8) x(4) y(4) w(2) h(2) pressure(2) actionButton(4) buttons(4)
        /// </summary>
        public Task SendTouchAsync(TouchAction action, int x, int y, int videoWidth, int videoHeight,
                                   float pressure, CancellationToken ct)
        {
            var buf = new byte[32];
            buf[0] = (byte)ControlMsgType.InjectTouchEvent;
            buf[1] = (byte)action;
            BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(2, 8), PointerIdMouse);
            BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(10, 4), x);
            BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(14, 4), y);
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(18, 2), (ushort)videoWidth);
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(20, 2), (ushort)videoHeight);
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(22, 2), FloatToU16Fp(pressure));

            // An UP must clear the button state, otherwise the server keeps the
            // primary button latched and every later move is treated as a drag.
            var buttons = action == TouchAction.Up ? 0u : 1u; // AMOTION_EVENT_BUTTON_PRIMARY
            var actionButton = action switch
            {
                TouchAction.Down => 1u,
                TouchAction.Up => 1u,
                _ => 0u,
            };
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(24, 4), actionButton);
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(28, 4), buttons);
            return SendAsync(buf, ct);
        }

        /// <summary>
        /// INJECT_SCROLL_EVENT, 21 bytes:
        /// type(1) x(4) y(4) w(2) h(2) hscroll(2) vscroll(2) buttons(4)
        /// </summary>
        public Task SendScrollAsync(int x, int y, int videoWidth, int videoHeight,
                                    float hScroll, float vScroll, CancellationToken ct)
        {
            var buf = new byte[21];
            buf[0] = (byte)ControlMsgType.InjectScrollEvent;
            BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(1, 4), x);
            BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(5, 4), y);
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(9, 2), (ushort)videoWidth);
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(11, 2), (ushort)videoHeight);
            BinaryPrimitives.WriteInt16BigEndian(buf.AsSpan(13, 2), FloatToI16Fp(hScroll));
            BinaryPrimitives.WriteInt16BigEndian(buf.AsSpan(15, 2), FloatToI16Fp(vScroll));
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(17, 4), 0);
            return SendAsync(buf, ct);
        }

        /// <summary>
        /// INJECT_KEYCODE, 14 bytes: type(1) action(1) keycode(4) repeat(4) metastate(4)
        /// </summary>
        public Task SendKeycodeAsync(KeyAction action, int keycode, int metaState, CancellationToken ct)
        {
            var buf = new byte[14];
            buf[0] = (byte)ControlMsgType.InjectKeycode;
            buf[1] = (byte)action;
            BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(2, 4), keycode);
            BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(6, 4), 0);
            BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(10, 4), metaState);
            return SendAsync(buf, ct);
        }

        public async Task TapKeyAsync(int keycode, CancellationToken ct)
        {
            await SendKeycodeAsync(KeyAction.Down, keycode, 0, ct).ConfigureAwait(false);
            await SendKeycodeAsync(KeyAction.Up, keycode, 0, ct).ConfigureAwait(false);
        }

        /// <summary>INJECT_TEXT: type(1) length(4) utf8 bytes</summary>
        public Task SendTextAsync(string text, CancellationToken ct)
        {
            var utf8 = System.Text.Encoding.UTF8.GetBytes(text);
            var buf = new byte[5 + utf8.Length];
            buf[0] = (byte)ControlMsgType.InjectText;
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(1, 4), (uint)utf8.Length);
            utf8.CopyTo(buf, 5);
            return SendAsync(buf, ct);
        }

        public Task SendSimpleAsync(ControlMsgType type, CancellationToken ct)
            => SendAsync(new[] { (byte)type }, ct);

        // scrcpy encodes [0,1] into the full u16 range.
        private static ushort FloatToU16Fp(float value)
        {
            if (value >= 1.0f) return ushort.MaxValue;
            if (value <= 0.0f) return 0;
            return (ushort)(value * 65536.0f);
        }

        // scrcpy encodes [-1,1] into the full i16 range.
        private static short FloatToI16Fp(float value)
        {
            if (value >= 1.0f) return short.MaxValue;
            if (value <= -1.0f) return short.MinValue;
            return (short)(value * 32768.0f);
        }
    }
}
