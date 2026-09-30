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
    private sealed record OwnedSession(int Id, string BootId, DateTime StartedUtc);
    private readonly Dictionary<string, OwnedSession> sessions = store.Read("owned-sessions.json", () => new Dictionary<string, OwnedSession>());
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
                    // An exited orphan may remain as a zombie until its container/init reaps it.
                    // Its missing executable is not a permission failure or a running server.
                    if (ProcFields(process.Id)[0] is "Z" or "X") continue;
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
            // exec changes executable paths, but never changes the kernel lifetime token.
            var known = found.Where(candidate => registered.Any(i => SameLifetime(i, candidate))).ToList();
            if (sessions.TryGetValue(p.Id, out var session))
                foreach (var candidate in found)
                    if (!known.Contains(candidate) && InOwnedSession(candidate, session)) known.Add(candidate);
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
    private static bool SameLifetime(ProcessIdentity a, ProcessIdentity b) => a.Id == b.Id && a.StartToken is not null && a.StartToken == b.StartToken;
    private static bool InOwnedSession(ProcessIdentity candidate, OwnedSession session)
    {
        try
        {
            var fields = ProcFields(candidate.Id);
            return int.Parse(fields[3]) == session.Id && candidate.StartToken?.StartsWith(session.BootId + ":", StringComparison.Ordinal) == true && candidate.StartUtc >= session.StartedUtc.AddSeconds(-1);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException) { return false; }
    }
    private static string[] ProcFields(int id)
    {
        var text = File.ReadAllText($"/proc/{id}/stat");
        var fields = text[(text.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 4) throw new IOException("Cannot read the process session state.");
        return fields;
    }
    public async Task StartAsync(ServerProfile p, CancellationToken token = default)
    {
        p.Validate(); var state = await InspectAsync(p, token);
        if (!state.Accessible || state.Running) throw new IOException("The server is already running or its state cannot be verified.");
        if (!File.Exists(p.Launcher)) throw new IOException("Select the Linux server launcher under Settings.");
        GameConfiguration.Merge("", GameConfiguration.Read(p.ConfigPath));
        SafePaths.NoLinks(p.Launcher);
        var shell = p.Launcher.EndsWith(".sh", StringComparison.Ordinal);
        var setsid = new[] { "/usr/bin/setsid", "/bin/setsid" }.FirstOrDefault(File.Exists) ?? throw new IOException("Safe server startup requires the Linux setsid utility (util-linux).");
        var start = new ProcessStartInfo(setsid) { WorkingDirectory = p.InstallPath, UseShellExecute = false };
        // Stop an isolated bootstrap before game code runs, persist ownership, then release it.
        foreach (var arg in new[] { "/bin/bash", "-c", "kill -STOP $$; exec \"$@\"", "wyrmwatch-start" }) start.ArgumentList.Add(arg);
        if (shell) start.ArgumentList.Add("/bin/bash");
        start.ArgumentList.Add(p.Launcher); start.ArgumentList.Add("-log"); start.ArgumentList.Add($"-Port={p.Port}");
        using var process = Process.Start(start) ?? throw new IOException("Could not start server.");
        try
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException("The safe startup bootstrap exited before ownership was recorded.");
                var fields = ProcFields(process.Id);
                if (fields[0] == "T" && int.Parse(fields[3]) == process.Id) break;
                if (DateTime.UtcNow >= until) throw new IOException("The safe startup bootstrap did not enter its isolated session.");
                await Task.Delay(10, token);
            }
            var identity = new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime(), process.MainModule!.FileName!, ProcessLifetime.Token(process));
            await gate.WaitAsync(token);
            try
            {
                var startToken = identity.StartToken!;
                sessions[p.Id] = new(process.Id, startToken[..startToken.IndexOf(':')], identity.StartUtc);
                store.Write("owned-sessions.json", sessions);
                owned[p.Id] = [identity]; store.Write("owned-processes.json", owned);
            }
            finally { gate.Release(); }
            if (kill(process.Id, 18) != 0) throw new IOException("Could not release the safe startup bootstrap."); // SIGCONT
        }
        catch
        {
            // Abort only the stopped bootstrap, before exec has run any game code.
            if (!process.HasExited) { kill(process.Id, 15); kill(process.Id, 18); }
            throw;
        }
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
