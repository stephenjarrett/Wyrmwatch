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
using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "server.ready"), "ready");
stopped.Wait(TimeSpan.FromMinutes(2));

internal static class NativeConsole
{
    [DllImport("kernel32.dll")] internal static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
}

internal static class NativeSignals
{
    [DllImport("libc", SetLastError = true)] internal static extern IntPtr signal(int number, IntPtr handler);
}
