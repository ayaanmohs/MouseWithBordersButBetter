using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using InputForwarder.Common;

namespace InputForwarder.Sender;

internal static class Program
{
    private static ulong _seq;
    private static LockState _state = LockState.Local;
    private static TcpClient? _client;
    private static StreamWriter? _writer;
    private static LowLevelKeyboardProc? _keyboardProc;
    private static LowLevelMouseProc? _mouseProc;
    private static IntPtr _keyboardHookId = IntPtr.Zero;
    private static IntPtr _mouseHookId = IntPtr.Zero;
    private static string _secret = "changeme";

    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_MOUSEWHEEL = 0x020A;

    private static async Task<int> Main(string[] args)
    {
        var config = ParseArgs(args);
        _secret = config.Secret;
        await Connect(config);
        InstallHooks();
        Console.WriteLine("Sender running. Press Ctrl+C to exit.");
        Console.CancelKeyPress += (_, _) => UninstallHooks();
        Application.Run();
        return 0;
    }

    private static EndpointConfig ParseArgs(string[] args)
    {
        var host = "127.0.0.1";
        var port = 49152;
        var secret = "changeme";
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--receiver":
                    host = args[++i];
                    break;
                case "--port":
                    port = int.Parse(args[++i]);
                    break;
                case "--psk":
                    secret = args[++i];
                    break;
            }
        }

        return new EndpointConfig { Host = System.Net.IPAddress.Parse(host), Port = port, Secret = secret };
    }

    private static async Task Connect(EndpointConfig config)
    {
        _client?.Dispose();
        _client = new TcpClient();
        await _client.ConnectAsync(config.Host, config.Port);
        _writer = new StreamWriter(_client.GetStream(), Encoding.UTF8) { AutoFlush = true };
        await Send(MessageType.Hello, new HelloPayload(Environment.MachineName, config.Secret));
        Console.WriteLine("Connected to receiver");
    }

    private static void InstallHooks()
    {
        _keyboardProc = KeyboardCallback;
        _mouseProc = MouseCallback;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        var moduleHandle = GetModuleHandle(curModule?.ModuleName);
        _keyboardHookId = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, moduleHandle, 0);
        _mouseHookId = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, moduleHandle, 0);
    }

    private static void UninstallHooks()
    {
        if (_keyboardHookId != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHookId);
        if (_mouseHookId != IntPtr.Zero) UnhookWindowsHookEx(_mouseHookId);
    }

    private static IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _state == LockState.Locked)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (IsToggleHotkey(info))
            {
                ToggleState();
                return (IntPtr)1;
            }
            var isDown = wParam == (IntPtr)WM_KEYDOWN;
            var payload = new KeyPayload(info.vkCode, info.scanCode, isDown,
                new ModifierState(IsPressed(Keys.Menu), IsPressed(Keys.ControlKey), IsPressed(Keys.ShiftKey),
                    IsPressed(Keys.LWin) || IsPressed(Keys.RWin)));
            _ = Send(MessageType.Key, payload);
            return (IntPtr)1; // swallow locally
        }

        return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
    }

    private static IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _state == LockState.Locked)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var wheel = wParam == (IntPtr)WM_MOUSEWHEEL ? (short)((info.mouseData >> 16) & 0xffff) : 0;
            var payload = new MousePayload(
                Dx: info.pt.x,
                Dy: info.pt.y,
                Buttons: new MouseButtons("none", "none", "none", "none", "none"),
                Wheel: new WheelPayload(wheel, 0));
            _ = Send(MessageType.Mouse, payload);
            return (IntPtr)1;
        }

        return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
    }

    private static async Task Send<T>(MessageType type, T payload)
    {
        if (_writer == null) return;
        try
        {
            var line = ProtocolSerializer.Serialize(type, NextSeq(), payload, _secret);
            await _writer.WriteLineAsync(line);
        }
        catch
        {
            Console.WriteLine("Send failed; falling back to LOCAL");
            _state = LockState.Local;
        }
    }

    private static void ToggleState()
    {
        _state = _state == LockState.Locked ? LockState.Local : LockState.Locked;
        Console.WriteLine($"Mode -> {_state}");
        _ = Send(MessageType.Mode, new ModePayload(_state));
    }

    private static bool IsToggleHotkey(KBDLLHOOKSTRUCT info)
    {
        // Ctrl+Alt+F12 default toggle
        var f12 = info.vkCode == (int)Keys.F12;
        return f12 && IsPressed(Keys.Menu) && IsPressed(Keys.ControlKey);
    }

    private static ulong NextSeq() => Interlocked.Increment(ref _seq);

    #region Win32 hooks

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public int mouseData;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private static bool IsPressed(Keys key) => (Control.ModifierKeys & key) == key;

    #endregion
}

