using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Wyrmwatch.Core;

// AdminPassword may be absent from the INI after a launch. Retain the setting in
// the manager workspace. Windows protects it for the current user with DPAPI;
// Unix stores it in a file readable/writable only by its owner.
public sealed class AdminPasswordStore(JsonStore store)
{
    private const string FileName = "server-admin-passwords.json";
    public string? Read(ServerProfile profile)
    {
        SafePaths.NoLinks(Path.Combine(store.DirectoryPath, FileName));
        var entries = store.Read(FileName, () => new Dictionary<string, string>());
        if (!entries.TryGetValue(profile.Id, out var value)) return null;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value); }
        catch (FormatException error) { throw new IOException("The saved admin password cannot be read. Its file was preserved; re-enter the password in Server settings.", error); }
        return Encoding.UTF8.GetString(OperatingSystem.IsWindows() ? Protect(bytes, false) : bytes);
    }
    public void Remember(ServerProfile profile, string password)
    {
        if (string.IsNullOrWhiteSpace(password)) return;
        var entries = store.Read(FileName, () => new Dictionary<string, string>());
        var bytes = Encoding.UTF8.GetBytes(password);
        entries[profile.Id] = Convert.ToBase64String(OperatingSystem.IsWindows() ? Protect(bytes, true) : bytes);
        store.Write(FileName, entries, privateFile: true);
    }
    private static byte[] Protect(byte[] bytes, bool encrypt)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            Blob output;
            var success = encrypt ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new IOException("The saved admin password could not be protected or read for this user.", new Win32Exception(Marshal.GetLastWin32Error()));
            try { var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
}
