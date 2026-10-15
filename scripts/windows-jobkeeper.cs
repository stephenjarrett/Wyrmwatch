// AGPL-3.0-only. Built with Windows' .NET Framework compiler, not the app runtime.
// This query-only helper can keep a game job alive without locking app files.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

internal static class JobKeeper
{
    private static int Main(string[] args)
    {
        Guid id;
        if (args.Length != 3 || args[0] != "--keep-job" ||
            !args[1].StartsWith("Local\\Wyrmwatch-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(args[1].Substring(16), "N", out id)) return 10;
        try
        {
            if (!String.Equals(Path.GetFullPath(args[2]), args[2], StringComparison.OrdinalIgnoreCase)) return 13;
            for (string path = args[2]; path != null; path = Path.GetDirectoryName(path))
                if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return 13;
            // JOB_OBJECT_QUERY only: never assign, signal, or terminate a process.
            using (SafeFileHandle job = OpenJobObject(4, false, args[1]))
            {
                if (job.IsInvalid) return 11;
                Accounting state;
                if (!QueryInformationJobObject(job, 1, out state, (uint)Marshal.SizeOf(typeof(Accounting)), IntPtr.Zero) || state.ActiveProcesses == 0) return 12;
                using (FileStream marker = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete)) marker.WriteByte(1);
                while (true)
                {
                    if (!QueryInformationJobObject(job, 1, out state, (uint)Marshal.SizeOf(typeof(Accounting)), IntPtr.Zero)) return 12;
                    if (state.ActiveProcesses == 0) return 0;
                    Thread.Sleep(100);
                }
            }
        }
        catch { return 13; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Accounting
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    [DllImport("kernel32.dll", EntryPoint = "OpenJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenJobObject(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, out Accounting state, uint size, IntPtr returned);
}
