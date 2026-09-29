using System.Security.Cryptography;
using System.Text;

namespace Dragonwilds.Core;

public sealed record AgentEndpoint(int ProcessId, long StartUtcTicks, string Address, string Secret, string Version);
public sealed record ManagedServer(string Id, string Name, ServerSnapshot State, ScheduleState Schedule, string? Build)
{
    public string[] Actions { get; init; } = ["start", "stop", "restart", "backup", "check", "update", "verify", "restore"];
}
public sealed record AgentStatus(bool Busy, string Version, List<ManagedServer> Servers, List<OperationRecord> Operations, List<string> Log, bool Background, string? RemoteAddress, bool PersistentHost = false);
public sealed record ServerAction(string Action, string? Archive = null, Dictionary<string, string>? Values = null, string? Confirmation = null);
public sealed record ActionResult(string Message);
public sealed record RemoteSettings(bool Enabled = false, int Port = 8843);
public sealed record AccessGrant(string Id, string Name, string SecretHash, string Role, string? ServerId, DateTimeOffset Expires);
public sealed record CreateAccessGrant(string Name, string Role, string? ServerId, int Days = 30);
public sealed record IssuedAccessGrant(AccessGrant Grant, string Secret);
public sealed record AccessGrantView(string Id, string Name, string Role, string? ServerId, DateTimeOffset Expires)
{
    public string Detail => $"{Role} · {(ServerId is null ? "All servers" : "One server")} · expires {Expires.LocalDateTime:MMM d, yyyy}";
}
public sealed record RemoteInfo(RemoteSettings Settings, string? Address, string? Certificate, string? Warning = null);

public static class AccessPolicy
{
    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    public static bool Matches(string secret, string hash)
    {
        if (secret.Length is < 32 or > 256 || hash.Length != 64) return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Hash(secret)), Convert.FromHexString(hash)); }
        catch (FormatException) { return false; }
    }
    public static bool Allows(AccessGrant grant, string serverId, string action, DateTimeOffset now)
    {
        if (grant.Expires <= now || (grant.ServerId is not null && grant.ServerId != serverId)) return false;
        return action switch
        {
            "view" or "backups" => grant.Role is "Viewer" or "Operator" or "Maintainer",
            "start" or "stop" or "restart" or "backup" or "check" => grant.Role is "Operator" or "Maintainer",
            "update" or "restore" or "verify" => grant.Role == "Maintainer",
            _ => false
        };
    }
}

public static class WorkspaceLease
{
    public static FileStream Acquire(string directory)
    {
        Directory.CreateDirectory(directory); SafePaths.NoLinks(directory);
        try { return new FileStream(Path.Combine(directory, "agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new IOException("A background manager already owns this workspace."); }
    }
    public static void Protect(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | (Directory.Exists(path) ? UnixFileMode.UserExecute : 0));
    }
}
