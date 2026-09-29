using System.Text;

namespace Dragonwilds.Core;

public static class LogTail
{
    public static string Read(string path)
    {
        SafePaths.NoLinks(path);
        if (!File.Exists(path)) return "No game log has been written yet.";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - 64 * 1024); stream.Seek(start, SeekOrigin.Begin);
        var bytes = new byte[64 * 1024]; var count = stream.ReadAtLeast(bytes, Math.Min(bytes.Length, (int)Math.Min(stream.Length - start, bytes.Length)), false);
        var text = Encoding.UTF8.GetString(bytes, 0, count);
        if (start > 0) { var newline = text.IndexOf('\n'); text = newline < 0 ? "" : text[(newline + 1)..]; }
        return string.Join('\n', text.Split('\n').TakeLast(300));
    }
}
