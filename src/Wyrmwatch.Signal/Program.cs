using System.Diagnostics;
using System.Runtime.InteropServices;
using Wyrmwatch.Core;
using Wyrmwatch.Platform;
using Microsoft.Win32.SafeHandles;

if (OperatingSystem.IsWindows() && args.Length == 3 && args[0] == "--keep-job") return await Native.KeepJobAsync(args[1], args[2]);

if (!OperatingSystem.IsWindows() || args.Length != 3 || !int.TryParse(args[0], out var pid) || !long.TryParse(args[1], out var ticks)) return 2;
try
{
    using var process = Process.GetProcessById(pid);
    var profile = new ServerProfile { InstallPath = args[2] };
    if (process.StartTime.ToUniversalTime().Ticks != ticks || !WindowsRuntime.ExpectedPath(profile, process.MainModule!.FileName!)) return 3;
    Native.FreeConsole();
    if (!Native.AttachConsole((uint)pid)) return 4;
    try
    {
        var members = new uint[256]; var count = Native.GetConsoleProcessList(members, (uint)members.Length);
        if (count == 0 || count > members.Length || !members.Take((int)count).Contains((uint)pid)) return 5;
        foreach (var member in members.Take((int)count))
        {
            if (member == Environment.ProcessId) continue;
            using var other = Process.GetProcessById((int)member);
            if (!WindowsRuntime.ExpectedPath(profile, other.MainModule!.FileName!)) return 6;
        }
        if (!Native.SetConsoleCtrlHandler(IntPtr.Zero, true)) return 7;
        if (!Native.GenerateConsoleCtrlEvent(0, 0)) return 8;
        await Task.Delay(300);
        return 0;
    }
    finally { Native.FreeConsole(); }
}
catch { return 9; }

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
    [DllImport("kernel32.dll")] internal static extern bool AttachConsole(uint id);
    [DllImport("kernel32.dll")] internal static extern uint GetConsoleProcessList(uint[] ids, uint count);
    [DllImport("kernel32.dll")] internal static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll")] internal static extern bool GenerateConsoleCtrlEvent(uint type, uint group);
}
