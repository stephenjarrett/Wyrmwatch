using System.Diagnostics;
using System.Runtime.InteropServices;
using Dragonwilds.Core;
using Dragonwilds.Windows;

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
    [DllImport("kernel32.dll")] internal static extern bool FreeConsole();
    [DllImport("kernel32.dll")] internal static extern bool AttachConsole(uint id);
    [DllImport("kernel32.dll")] internal static extern uint GetConsoleProcessList(uint[] ids, uint count);
    [DllImport("kernel32.dll")] internal static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll")] internal static extern bool GenerateConsoleCtrlEvent(uint type, uint group);
}
