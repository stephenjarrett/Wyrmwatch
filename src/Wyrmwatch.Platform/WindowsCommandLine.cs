using System.Runtime.InteropServices;
using System.Text;

namespace Wyrmwatch.Platform;

public static class WindowsCommandLine
{
    public static List<string> ParseArguments(string arguments)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var block = CommandLineToArgv("Wyrmwatch.exe " + arguments, out var count);
        if (block == IntPtr.Zero) throw new IOException("Cannot read existing shortcut arguments.");
        try
        {
            var result = new List<string>();
            for (var index = 1; index < count; index++) result.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(block, index * IntPtr.Size)) ?? "");
            return result;
        }
        finally { LocalFree(block); }
    }
    public static string JoinArguments(IEnumerable<string> arguments) => string.Join(" ", arguments.Select(Quote));
    private static string Quote(string value)
    {
        var output = new StringBuilder("\""); var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            output.Append('\\', character == '"' ? slashes * 2 + 1 : slashes); slashes = 0; output.Append(character);
        }
        return output.Append('\\', slashes * 2).Append('"').ToString();
    }
    [DllImport("shell32.dll", EntryPoint = "CommandLineToArgvW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgv(string commandLine, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr block);
}
