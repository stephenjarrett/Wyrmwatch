// Disposable test process: never installs or runs a game.
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Diagnostics;

if (File.Exists(Path.Combine(AppContext.BaseDirectory, "launcher-handoff")))
{
    var child = Path.Combine(AppContext.BaseDirectory, "RSDragonwilds", "Binaries", "Win64", "RSDragonwildsServer-Win64-Shipping.exe");
    var start = new ProcessStartInfo(child) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(child)! };
    foreach (var arg in args) start.ArgumentList.Add(arg);
    using var launched = Process.Start(start) ?? throw new IOException("Could not start disposable shipping fixture.");
    return;
}

using var stopped = new ManualResetEventSlim();
void Stop() { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "shutdown.requested"), "graceful"); stopped.Set(); }
var graphical = OperatingSystem.IsWindows() && File.Exists(Path.Combine(AppContext.BaseDirectory, "window-console"));
if (OperatingSystem.IsWindows())
{
    // Test runners can pass an inherited ignore-Ctrl+C flag to console children.
    NativeConsole.SetConsoleCtrlHandler(IntPtr.Zero, false);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; Stop(); };
}
// A noninteractive shell's background job inherits SIGINT ignored. Restore the
// fixture's cooperative signal contract before registering its graceful handler.
if (OperatingSystem.IsLinux() && NativeSignals.signal(2, IntPtr.Zero) == new IntPtr(-1)) throw new IOException("Could not reset the disposable fixture's SIGINT disposition.");
using var signal = OperatingSystem.IsLinux() ? PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; Stop(); }) : null;
var port = int.Parse(args.Single(arg => arg.StartsWith("-Port=", StringComparison.Ordinal))[6..]);
UdpClient BindFixtureSocket()
{
    try { return new UdpClient(new IPEndPoint(IPAddress.Loopback, port)); }
    catch (SocketException error)
    {
        // Synthetic test processes only; record a category, never arguments or
        // configuration. CI can distinguish port failure from path inspection.
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup.failure"), "Socket:" + error.SocketErrorCode);
        throw;
    }
}
using var socket = BindFixtureSocket();
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "server.ready"), "ready");
if (graphical) FixtureWindow.Run(Stop);
else stopped.Wait(TimeSpan.FromMinutes(2));

internal static class NativeConsole
{
    [DllImport("kernel32.dll")] internal static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
}

internal static class NativeSignals
{
    [DllImport("libc", SetLastError = true)] internal static extern IntPtr signal(int number, IntPtr handler);
}

// A disposable GUI console models the shipping game's WM_CLOSE shutdown path.
// It has no Windows text console, and never loads game code or user data.
internal static class FixtureWindow
{
    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    internal static void Run(Action stop)
    {
        FreeConsole();
        WindowProcedure procedure = (window, message, wparam, lparam) =>
        {
            if (message == 0x0010) { stop(); PostQuitMessage(0); return IntPtr.Zero; }
            return DefWindowProc(window, message, wparam, lparam);
        };
        var name = "WyrmwatchDisposableConsole";
        var instance = GetModuleHandle(null);
        var windowClass = new WindowClass { Procedure = Marshal.GetFunctionPointerForDelegate(procedure), Instance = instance, Name = name };
        if (RegisterClass(ref windowClass) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var title = File.Exists(Path.Combine(AppContext.BaseDirectory, "unrecognized-window")) ? "Unrelated fixture window" : "Server Console (RSDragonwilds) - " + Environment.ProcessPath;
        var handle = CreateWindowEx(0, name, title, 0x10cf0000, 0, 0, 400, 200, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "window.ready"), "ready");
        try { while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); } }
        finally { DestroyWindow(handle); GC.KeepAlive(procedure); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Style; public IntPtr Procedure; public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background; public string? Menu, Name;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message
    {
        public IntPtr Window; public uint Id; public UIntPtr Wparam; public IntPtr Lparam;
        public uint Time; public int X, Y; public uint Private;
    }
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "RegisterClassW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref WindowClass windowClass);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
}
