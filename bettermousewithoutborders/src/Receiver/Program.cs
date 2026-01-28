using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using InputForwarder.Common;

namespace InputForwarder.Receiver;

internal static class Program
{
    private static ulong _seq = 0;

    private static async Task<int> Main(string[] args)
    {
        var config = ParseArgs(args);
        Console.WriteLine($"Receiver listening on {config.Host}:{config.Port} (secret: {config.Secret})");
        var listener = new TcpListener(config.Host, config.Port);
        listener.Start();
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = Task.Run(() => HandleClient(client, config));
        }
    }

    private static EndpointConfig ParseArgs(string[] args)
    {
        var host = IPAddress.Any;
        var port = 49152;
        var secret = "changeme";
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port":
                    port = int.Parse(args[++i]);
                    break;
                case "--psk":
                    secret = args[++i];
                    break;
                case "--host":
                    host = IPAddress.Parse(args[++i]);
                    break;
            }
        }

        return new EndpointConfig { Host = host, Port = port, Secret = secret };
    }

    private static async Task HandleClient(TcpClient client, EndpointConfig config)
    {
        Console.WriteLine("Client connected");
        using var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.UTF8);
        var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!ProtocolSerializer.TryDeserialize(line, config.Secret, out var env) || env == null)
            {
                Console.WriteLine("Invalid message, closing");
                break;
            }

            switch (env.Type)
            {
                case MessageType.Hello:
                    Console.WriteLine("HELLO received");
                    await writer.WriteLineAsync(
                        ProtocolSerializer.Serialize(MessageType.Status, NextSeq(), new StatusPayload(true, null),
                            config.Secret));
                    break;
                case MessageType.Key:
                    var keyPayload = env.Payload.Deserialize<KeyPayload>();
                    if (keyPayload != null) InjectKeyboard(keyPayload);
                    break;
                case MessageType.Mouse:
                    var mousePayload = env.Payload.Deserialize<MousePayload>();
                    if (mousePayload != null) InjectMouse(mousePayload);
                    break;
                case MessageType.Ping:
                    await writer.WriteLineAsync(
                        ProtocolSerializer.Serialize(MessageType.Pong, NextSeq(), new { }, config.Secret));
                    break;
                case MessageType.Goodbye:
                    return;
            }
        }

        Console.WriteLine("Client disconnected");
    }

    private static ulong NextSeq() => Interlocked.Increment(ref _seq);

    private static void InjectKeyboard(KeyPayload payload)
    {
        var inputs = new[]
        {
            new INPUT
            {
                type = InputType.INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = (ushort)payload.VirtualKey,
                        wScan = (ushort)payload.ScanCode,
                        dwFlags = payload.IsDown ? 0u : KEYEVENTF.KEYUP,
                        time = 0,
                        dwExtraInfo = UIntPtr.Zero
                    }
                }
            }
        };

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == 0)
        {
            Console.WriteLine($"SendInput failed for key {payload.VirtualKey}");
        }
    }

    private static void InjectMouse(MousePayload payload)
    {
        var flags = MOUSEEVENTF.MOVE;
        var inputs = new List<INPUT>();

        inputs.Add(new INPUT
        {
            type = InputType.INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = payload.Dx,
                    dy = payload.Dy,
                    mouseData = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        });

        if (payload.Wheel.Vertical != 0)
        {
            inputs.Add(new INPUT
            {
                type = InputType.INPUT_MOUSE,
                U = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        mouseData = payload.Wheel.Vertical,
                        dwFlags = MOUSEEVENTF.WHEEL
                    }
                }
            });
        }

        var sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        if (sent == 0)
        {
            Console.WriteLine("SendInput failed for mouse input");
        }
    }

    #region Win32 interop

    private enum InputType : uint
    {
        INPUT_MOUSE = 0,
        INPUT_KEYBOARD = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public InputType type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public MOUSEEVENTF dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public KEYEVENTF dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [Flags]
    private enum MOUSEEVENTF : uint
    {
        MOVE = 0x0001,
        LEFTDOWN = 0x0002,
        LEFTUP = 0x0004,
        RIGHTDOWN = 0x0008,
        RIGHTUP = 0x0010,
        MIDDLEDOWN = 0x0020,
        MIDDLEUP = 0x0040,
        XDOWN = 0x0080,
        XUP = 0x0100,
        WHEEL = 0x0800,
        HWHEEL = 0x01000,
        ABSOLUTE = 0x8000
    }

    [Flags]
    private enum KEYEVENTF : uint
    {
        EXTENDEDKEY = 0x0001,
        KEYUP = 0x0002,
        UNICODE = 0x0004,
        SCANCODE = 0x0008
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    #endregion
}

