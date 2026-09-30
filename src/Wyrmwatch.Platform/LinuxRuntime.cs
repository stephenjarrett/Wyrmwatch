using System.Diagnostics;
using System.Runtime.InteropServices;
using Wyrmwatch.Core;

namespace Wyrmwatch.Platform;

// Kept behind IServerRuntime; the desktop has no platform-specific process logic.
public sealed class LinuxRuntime(JsonStore store) : IServerRuntime
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, List<ProcessIdentity>> owned = store.Read("owned-processes.json", () => new Dictionary<string, List<ProcessIdentity>>());
    private readonly Dictionary<string, (DateTime Time, double Cpu)> previous = [];
    public async Task<ServerSnapshot> InspectAsync(ServerProfile p, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try { return await Task.Run(() => Inspect(p), token); }
        finally { gate.Release(); }
    }
    private ServerSnapshot Inspect(ServerProfile p)
    {
        var found = new List<ProcessIdentity>(); long memory = 0; double cpu = 0; var accessible = true;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (!process.ProcessName.StartsWith("RSDragonwilds", StringComparison.Ordinal)) continue;
                    var path = process.MainModule?.FileName;
                    if (path is null) { accessible = false; continue; }
                    if (!SafePaths.Within(path, p.InstallPath)) continue;
                    found.Add(new(process.Id, process.StartTime.ToUniversalTime(), path, ProcessLifetime.Token(process))); memory += process.WorkingSet64; cpu += process.TotalProcessorTime.TotalMilliseconds;
                }
                catch (System.ComponentModel.Win32Exception) { accessible = false; }
                catch (InvalidOperationException) { }
                catch (IOException) { accessible = false; }
            }
        }
        if (found.Count == 0) return ServerSnapshot.Offline with { Accessible = accessible, Players = accessible ? 0 : null };
        if (owned.TryGetValue(p.Id, out var registered))
        {
            var known = registered.Where(IsSame).ToList();
            foreach (var candidate in found)
            {
                if (known.Any(i => SameIdentity(i, candidate))) continue;
                var current = candidate.Id; var visited = new HashSet<int>();
                while (current > 1 && visited.Add(current))
                {
                    if (known.Any(i => i.Id == current && i.StartUtc <= candidate.StartUtc)) { known.Add(candidate); break; }
                    try { var stat = File.ReadAllText($"/proc/{current}/stat"); var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' '); current = int.Parse(fields[1]); }
                    catch { break; }
                }
            }
            if (!known.SequenceEqual(registered)) { owned[p.Id] = known; store.Write("owned-processes.json", owned); }
        }
        var now = DateTime.UtcNow; var key = string.Join(':', found.OrderBy(i => i.Id).Select(i => $"{i.Id}-{i.StartUtc.Ticks}")); double percent = 0;
        if (previous.TryGetValue(key, out var old) && now > old.Time) percent = Math.Clamp((cpu - old.Cpu) / (now - old.Time).TotalMilliseconds / Environment.ProcessorCount * 100, 0, 100);
        previous[key] = (now, cpu);
        if (previous.Count > 100) previous.Clear();
        var started = found.Min(i => i.StartUtc); var players = PlayerActivity.Read(p.LogPath, started);
        return new(true, accessible, players.Count, players.Reason, percent, memory, now - started, found);
    }
    private static bool IsSame(ProcessIdentity identity)
    {
        try { using var p = Process.GetProcessById(identity.Id); return ProcessLifetime.Token(p) == identity.StartToken && p.MainModule?.FileName == identity.Path; }
        catch { return false; }
    }
    private static bool SameIdentity(ProcessIdentity a, ProcessIdentity b) => a.Id == b.Id && a.StartToken is not null && a.StartToken == b.StartToken && a.Path == b.Path;
    public async Task StartAsync(ServerProfile p, CancellationToken token = default)
    {
        p.Validate(); var state = await InspectAsync(p, token);
        if (!state.Accessible || state.Running) throw new IOException("The server is already running or its state cannot be verified.");
        if (!File.Exists(p.Launcher)) throw new IOException("Select the Linux server launcher under Settings.");
        GameConfiguration.Merge("", GameConfiguration.Read(p.ConfigPath));
        SafePaths.NoLinks(p.Launcher);
        var shell = p.Launcher.EndsWith(".sh", StringComparison.Ordinal);
        var start = new ProcessStartInfo(shell ? "/bin/bash" : p.Launcher) { WorkingDirectory = p.InstallPath, UseShellExecute = false };
        if (shell) start.ArgumentList.Add(p.Launcher); start.ArgumentList.Add("-log"); start.ArgumentList.Add($"-Port={p.Port}");
        using var process = Process.Start(start) ?? throw new IOException("Could not start server.");
        var identity = new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime(), process.MainModule!.FileName!, ProcessLifetime.Token(process));
        await gate.WaitAsync(token);
        try { owned[p.Id] = [identity]; store.Write("owned-processes.json", owned); }
        finally { gate.Release(); }
        await Task.Delay(1500, token);
        if (!(await InspectAsync(p, token)).Running) throw new IOException("Server exited during startup. Check its log and Linux runtime dependencies.");
    }
    public async Task StopAsync(ServerProfile p, CancellationToken token = default)
    {
        var state = await InspectAsync(p, token);
        if (!state.Accessible) throw new IOException("Cannot safely inspect game processes.");
        if (!state.Running) return;
        if (!owned.TryGetValue(p.Id, out var known) || state.Processes.Any(i => !known.Any(k => SameIdentity(k, i)))) throw new IOException("This server was started elsewhere. Stop it with its existing controls before starting it through Wyrmwatch.");
        foreach (var identity in state.Processes.OrderByDescending(i => i.StartUtc))
        {
            if (!IsSame(identity)) throw new IOException("Server identity changed during shutdown.");
            if (kill(identity.Id, 2) != 0) throw new IOException("Could not send SIGINT to the server.");
        }
        var until = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(1000, token); state = await InspectAsync(p, token);
            if (state.Accessible && !state.Running) return;
        }
        throw new IOException("The server did not stop within two minutes. It was not force-killed.");
    }
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int signal);
}
