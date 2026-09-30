using System.Security.Cryptography;
using System.Text;

namespace Wyrmwatch.Core;

public sealed record AgentEndpoint(int ProcessId, string StartToken, string Address, string Secret, string Version);
public sealed record AgentParent(int Id, string StartToken);
public sealed record ManagedServer(string Id, string Name, ServerSnapshot State, ScheduleState Schedule, string? Build)
{
    public bool RecoveryRequired { get; init; }
    public string[] Actions { get; init; } = ["start", "stop", "restart", "backup", "check", "update", "verify", "restore"];
}
public sealed record AgentStatus(bool Busy, string Version, List<ManagedServer> Servers, List<OperationRecord> Operations, List<string> Log, bool Background, string? RemoteAddress, bool PersistentHost = false);
public sealed record ServerAction(string Action, string? Archive = null, Dictionary<string, string>? Values = null, string? Confirmation = null);
public sealed record CreateServerRequest(ServerProfile Profile, Dictionary<string, string> Configuration);
public sealed record WorldImportRequest(ServerProfile Profile, Dictionary<string, string> Configuration, WorldImportPlan Source, bool SourceStoppedConfirmed = false);
public sealed record ActionResult(string Message);
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
