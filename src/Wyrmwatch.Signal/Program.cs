using System.Diagnostics;
using System.Runtime.InteropServices;
using Wyrmwatch.Core;
using Wyrmwatch.Platform;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;

if (OperatingSystem.IsWindows() && args.Length == 3 && args[0] == "--keep-job") return await Native.KeepJobAsync(args[1], args[2]);

if (!OperatingSystem.IsWindows() || args.Length != 3 || !int.TryParse(args[0], out var pid) || !long.TryParse(args[1], out var ticks)) return 2;
try
{
    using var process = Process.GetProcessById(pid);
    var profile = new ServerProfile { InstallPath = args[2] };
    var executable = process.MainModule!.FileName!;
    if (process.StartTime.ToUniversalTime().Ticks != ticks || !WindowsRuntime.ExpectedPath(profile, executable)) return Failure(3, "Server identity changed. No shutdown request was sent.");
    Native.FreeConsole();
    if (!Native.AttachConsole((uint)pid))
    {
        var error = Marshal.GetLastWin32Error();
        // Shipping builds can expose Unreal's graphical log window instead of
        // an OS console. Only ERROR_INVALID_HANDLE means there is no console.
        // Access failures must never trigger a different shutdown mechanism.
        if (error != 6) return Failure(4, $"Could not attach to the server console: {new Win32Exception(error).Message} (Windows error {error}). The server was left running.");
        process.Refresh();
        var window = process.MainWindowHandle;
        if (window == IntPtr.Zero || Path.GetFileName(executable) != "RSDragonwildsServer-Win64-Shipping.exe" ||
            !process.MainWindowTitle.StartsWith("Server Console (RSDragonwilds) - " + executable, StringComparison.OrdinalIgnoreCase) ||
            Native.GetWindowThreadProcessId(window, out var windowPid) == 0 || windowPid != (uint)pid || !Native.IsWindowEnabled(window))
            return Failure(4, "The server has no Windows text console or recognized game console window. No shutdown request was sent; the server remains running.");
        if (!Native.PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero))
            return Failure(4, $"Could not request a normal game-window close: {new Win32Exception(Marshal.GetLastWin32Error()).Message}. The server was not force-killed.");
        return 0;
    }
    try
    {
        var members = new uint[256]; var count = Native.GetConsoleProcessList(members, (uint)members.Length);
        if (count == 0 || count > members.Length || !members.Take((int)count).Contains((uint)pid)) return Failure(5, "Server console membership could not be verified. No signal was sent.");
        foreach (var member in members.Take((int)count))
        {
            if (member == Environment.ProcessId) continue;
            using var other = Process.GetProcessById((int)member);
            if (!WindowsRuntime.ExpectedPath(profile, other.MainModule!.FileName!)) return Failure(6, "The console contains an unrelated process. No signal was sent.");
        }
        if (!Native.SetConsoleCtrlHandler(IntPtr.Zero, true)) return Failure(7, "Could not prepare the shutdown helper. No signal was sent.");
        if (!Native.GenerateConsoleCtrlEvent(0, 0)) return Failure(8, "Windows rejected the shutdown signal. The server was not force-killed.");
        await Task.Delay(300);
        return 0;
    }
    finally { Native.FreeConsole(); }
}
catch (Exception error) { return Failure(9, "Shutdown could not be verified: " + error.Message + ". The server was not force-killed."); }

static int Failure(int code, string message) { Console.Error.WriteLine(message); return code; }

internal static class Native
{
    internal static async Task<int> KeepJobAsync(string name, string ready)
    {
        if (!name.StartsWith("Local\\Wyrmwatch-", StringComparison.Ordinal) || !Guid.TryParseExact(name[16..], "N", out _)) return 10;
        try
        {
            // Query access only: this helper never signals, assigns, or terminates a process.
            using var job = OpenJobObject(4, false, name);
            if (job.IsInvalid) return 11;
            if (!QueryInformationJobObject(job, 1, out var initial, (uint)Marshal.SizeOf<JobAccounting>(), IntPtr.Zero) || initial.ActiveProcesses == 0) return 12;
            SafePaths.NoLinks(ready);
            using (var marker = new FileStream(ready, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete)) marker.WriteByte(1);
            while (true)
            {
                if (!QueryInformationJobObject(job, 1, out var accounting, (uint)Marshal.SizeOf<JobAccounting>(), IntPtr.Zero)) return 12;
                if (accounting.ActiveProcesses == 0) return 0;
                await Task.Delay(100);
            }
        }
        catch { return 13; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct JobAccounting
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    [DllImport("kernel32.dll", EntryPoint = "OpenJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle OpenJobObject(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, out JobAccounting info, uint size, IntPtr returned);
    [DllImport("kernel32.dll")] internal static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool AttachConsole(uint id);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint id);
    [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)] internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("kernel32.dll")] internal static extern uint GetConsoleProcessList(uint[] ids, uint count);
    [DllImport("kernel32.dll")] internal static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll")] internal static extern bool GenerateConsoleCtrlEvent(uint type, uint group);
}
