using System.Security.Cryptography;
using Wyrmwatch.Core;

namespace Wyrmwatch.Platform;

internal static class DetachedJobKeeper
{
    internal static string Prepare(string signalHelper, string workspace)
    {
        var source = Path.Combine(Path.GetDirectoryName(signalHelper)!, "Wyrmwatch.Signal.JobKeeper.exe");
        SafePaths.NoLinks(source);
        if (!File.Exists(source) || new FileInfo(source).Length is <= 0 or > 262144)
            throw new IOException("The independent process ownership helper is missing or invalid. Reinstall the complete application.");
        string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
        var hash = Hash(source);
        var directory = Path.Combine(workspace, "process-support", hash);
        // The Windows Framework loader still requires a conventional executable
        // path, including its optional .config lookup. Fail before game code runs.
        var destination = Path.Combine(directory, "keep.exe");
        if (destination.Length + ".config".Length >= 260) throw new IOException("The workspace path is too long for the independent process helper. Choose a shorter workspace path before starting the server.");
        SafePaths.NoLinks(directory); Directory.CreateDirectory(directory);
        SafePaths.NoLinks(destination);
        if (!File.Exists(destination))
        {
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(source, temporary, false);
                if (Hash(temporary) != hash) throw new IOException("Independent process helper verification failed.");
                File.Move(temporary, destination, false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        if (Hash(destination) != hash) throw new IOException("The independent process helper has changed. The server was not started.");
        // Do not allow a CLR config or adjacent assembly to change activation.
        if (Directory.EnumerateFileSystemEntries(directory).Any(path => !SafePaths.Same(path, destination)))
            throw new IOException("The independent process helper cache contains unexpected files. The server was not started.");
        return destination;
    }
}
