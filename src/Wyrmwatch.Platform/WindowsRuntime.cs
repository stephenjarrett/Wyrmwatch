using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.Versioning;
using Wyrmwatch.Core;

namespace Wyrmwatch.Platform;

[SupportedOSPlatform("windows")]
public sealed class WindowsRuntime(JsonStore store, string signalHelper) : IServerRuntime
{
    private readonly Dictionary<string, List<ProcessIdentity>> owned = store.Read("owned-processes.json", () => new Dictionary<string, List<ProcessIdentity>>());
    private readonly Dictionary<string, (DateTime At, double Cpu)> cpu = [];
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<ServerSnapshot> InspectAsync(ServerProfile profile, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try { return await Task.Run(() => Inspect(profile), token); }
        finally { gate.Release(); }
    }
    private ServerSnapshot Inspect(ServerProfile profile)
    {
        var found = new List<ProcessIdentity>(); var accessible = true; double cpuMs = 0; long memory = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.ProcessName is not ("RSDragonwildsServer" or "RSDragonwildsServer-Win64-Shipping")) continue;
                    var path = process.MainModule?.FileName;
                    if (path is null) { accessible = false; continue; }
                    if (!ExpectedPath(profile, path)) continue;
                    found.Add(new(process.Id, process.StartTime.ToUniversalTime(), path)); cpuMs += process.TotalProcessorTime.TotalMilliseconds; memory += process.WorkingSet64;
                }
                catch (Win32Exception) { accessible = false; }
                catch (InvalidOperationException) { }
                catch (ArgumentException) { }
            }
        }
        if (found.Count == 0) { cpu.Remove(profile.Id); return ServerSnapshot.Offline with { Accessible = accessible, Players = accessible ? 0 : null, ActivityReason = accessible ? "Server is stopped" : "A server process cannot be inspected. Match its permissions before continuing." }; }
        found.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
        var now = DateTime.UtcNow; double percent = 0;
        var key = profile.Id + ":" + string.Join(';', found.Select(p => $"{p.Id}:{p.StartUtc.Ticks}"));
        if (cpu.TryGetValue(key, out var previous) && now > previous.At) percent = Math.Clamp((cpuMs - previous.Cpu) / (now - previous.At).TotalMilliseconds / Environment.ProcessorCount * 100, 0, 100);
        foreach (var old in cpu.Keys.Where(k => k.StartsWith(profile.Id + ":") && k != key).ToArray()) cpu.Remove(old);
        cpu[key] = (now, cpuMs);
        TrackChildren(profile, found);
        var (players, reason) = PlayerActivity.Read(profile.LogPath, found.Min(p => p.StartUtc));
        return new(true, accessible, players, reason, percent, memory, now - found.Min(p => p.StartUtc), found);
    }
    public static bool ExpectedPath(ServerProfile p, string path) => SafePaths.Same(path, p.Launcher) || SafePaths.Same(path, Path.Combine(p.InstallPath, "RSDragonwilds", "Binaries", "Win64", "RSDragonwildsServer-Win64-Shipping.exe"));
    private void TrackChildren(ServerProfile p, IReadOnlyList<ProcessIdentity> found)
    {
        if (!owned.TryGetValue(p.Id, out var existing)) return;
        var known = existing.Where(e => found.Any(f => f.Id == e.Id && f.StartUtc == e.StartUtc && SafePaths.Same(f.Path, e.Path))).ToList();
        var parents = NativeProcesses.Parents();
        foreach (var child in found)
        {
            if (known.Any(x => x.Id == child.Id)) continue;
            var parent = child.Id; var visited = new HashSet<int>();
            while (parents.TryGetValue(parent, out parent) && visited.Add(parent))
                if (known.Any(x => x.Id == parent && x.StartUtc <= child.StartUtc)) { known.Add(child); break; }
        }
        if (!known.SequenceEqual(existing)) { owned[p.Id] = known; store.Write("owned-processes.json", owned); }
    }
    public async Task StartAsync(ServerProfile profile, CancellationToken token = default)
    {
        profile.Validate();
        var before = await InspectAsync(profile, token);
        if (!before.Accessible) throw new IOException("Cannot safely inspect game processes.");
        if (before.Running) throw new IOException("The server is already running.");
        if (!File.Exists(profile.Launcher)) throw new IOException("The server launcher was not found. Select the folder containing RSDragonwildsServer.exe.");
        var config = GameConfiguration.Read(profile.ConfigPath);
        GameConfiguration.Merge("", config); // Validate mandatory values without writing anything.
        SafePaths.NoLinks(profile.Launcher);
        var identity = NativeProcesses.StartServer(profile);
        await gate.WaitAsync(token);
        try { owned[profile.Id] = [identity]; store.Write("owned-processes.json", owned); }
        finally { gate.Release(); }
        await Task.Delay(1500, token);
        var state = await InspectAsync(profile, token);
        if (!state.Running) throw new IOException("The server exited during startup. Inspect its game log.");
    }
    public async Task StopAsync(ServerProfile profile, CancellationToken token = default)
    {
        var state = await InspectAsync(profile, token);
        if (!state.Accessible) throw new IOException("Cannot safely inspect server processes.");
        if (!state.Running) return;
        if (!owned.TryGetValue(profile.Id, out var identities) || state.Processes.Any(p => !identities.Contains(p)))
            throw new IOException("This server was started outside this manager. Stop it using its existing controls; later starts from this app support safe shutdown.");
        if (!File.Exists(signalHelper)) throw new IOException("The safe shutdown helper is missing.");
        var target = state.Processes.OrderByDescending(p => p.Path.Contains("Shipping", StringComparison.OrdinalIgnoreCase)).First();
        await CommandRunner.RunAsync(signalHelper, [target.Id.ToString(), target.StartUtc.Ticks.ToString(), profile.InstallPath], AppContext.BaseDirectory, _ => { }, TimeSpan.FromSeconds(15), token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            while (true)
            {
                await Task.Delay(1000, deadline.Token);
                var current = await InspectAsync(profile, deadline.Token);
                if (!current.Accessible) throw new IOException("Lost access to the server while waiting for shutdown.");
                if (!current.Running) return;
            }
        }
        catch (OperationCanceledException) { throw new IOException("The server did not stop within two minutes. It was not force-killed."); }
    }
}

public static class PlayerActivity
{
    public static (int? Count, string Reason) Read(string log, DateTime processStartedUtc)
    {
        try
        {
            if (!File.Exists(log)) return (null, "Waiting for the current server log");
            var info = new FileInfo(log);
            // A complete current-session log is required; truncated tails must never be interpreted as empty.
            if (info.LastWriteTimeUtc < processStartedUtc || info.Length > 32_000_000) return (null, "Current-session activity cannot be verified");
            using var file = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file);
            return Parse(reader.ReadToEnd(), processStartedUtc);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (null, "Server log is unavailable"); }
    }
    public static (int? Count, string Reason) Parse(string text, DateTime processStartedUtc)
    {
        var players = new HashSet<string>(StringComparer.Ordinal); var ready = false; var session = false;
        // Unreal log timestamps are UTC. A fresh startup marker anchors the player-event replay to this process.
        foreach (var line in text.Split('\n'))
        {
            var timestamp = Regex.Match(line, @"\[(\d{4}\.\d{2}\.\d{2}-\d{2}\.\d{2}\.\d{2})(?::\d+)?\]");
            DateTime? at = timestamp.Success && DateTime.TryParseExact(timestamp.Groups[1].Value, "yyyy.MM.dd-HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
            if (line.Contains("LogInit:") && at >= processStartedUtc.AddSeconds(-3)) session = true;
            if (!session) continue;
            if (line.Contains("Game Engine Initialized") || Regex.IsMatch(line, @"GameNetDriver.*[Ll]istening")) ready = true;
            var join = Regex.Match(line, @"Join succeeded:\s*(.+)");
            var leave = Regex.Match(line, @"Player Removed from session \[[^\]]+\]-\[(.+)\]");
            if (join.Success) players.Add(join.Groups[1].Value.Trim());
            if (leave.Success && !players.Remove(leave.Groups[1].Value.Trim())) return (null, "Player log contains an unmatched departure");
        }
        return ready ? (players.Count, players.Count == 0 ? "No connected players found in this session's log" : $"{players.Count} player(s) online") : (null, "Waiting for verified world startup");
    }
}

[SupportedOSPlatform("windows")]
internal static class NativeProcesses
{
    public static ProcessIdentity StartServer(ServerProfile profile)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 1, Show = 0 };
        var command = new StringBuilder($"\"{profile.Launcher}\" -log -NewConsole -Port={profile.Port}");
        if (!CreateProcess(profile.Launcher, command, IntPtr.Zero, IntPtr.Zero, false, 0x10, IntPtr.Zero, profile.InstallPath, ref startup, out var result)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try { using var process = Process.GetProcessById((int)result.Id); return new((int)result.Id, process.StartTime.ToUniversalTime(), profile.Launcher); }
        finally { CloseHandle(result.Thread); CloseHandle(result.Process); }
    }
    public static Dictionary<int, int> Parents()
    {
        var result = new Dictionary<int, int>(); var snapshot = CreateToolhelp32Snapshot(2, 0); if (snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExeFile = "" };
            if (Process32First(snapshot, ref entry)) do { result[(int)entry.Id] = (int)entry.ParentId; } while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return result;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { public int Size; public string? Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags; public short Show, Reserved2; public IntPtr ReservedPtr, Input, Output, Error; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint Id, ThreadId; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ProcessEntry { public uint Size, Usage, Id; public UIntPtr Heap; public uint Module, Threads, ParentId; public int Priority; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile; }
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string app, StringBuilder command, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint id);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
}
