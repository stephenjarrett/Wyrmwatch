using System.Diagnostics;
using System.Globalization;

namespace Wyrmwatch.Core;

public static class ProcessLifetime
{
    // Linux's managed wall-clock start time can differ between readers. Use the
    // kernel start counter and boot ID so a manager restart keeps the same identity.
    public static string Token(Process process)
    {
        if (!OperatingSystem.IsLinux()) return process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
        var stat = File.ReadAllText($"/proc/{process.Id}/stat");
        var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20 || !ulong.TryParse(fields[19], out _)) throw new IOException("Cannot read the process start counter.");
        var boot = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
        if (!Guid.TryParse(boot, out _)) throw new IOException("Cannot read the kernel boot identity.");
        return boot + ":" + fields[19];
    }
}
