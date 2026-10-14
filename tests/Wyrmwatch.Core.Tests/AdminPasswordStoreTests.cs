using Wyrmwatch.Core;

namespace Wyrmwatch.Core.Tests;

public class AdminPasswordStoreTests
{
    [Fact]
    public void ProtectedPasswordsAreIsolatedAndPersistAcrossReopen()
    {
        using var f = new Fixture(); var store = new JsonStore(f.Root);
        var other = f.Profile with { Id = "other-fixture" };
        var passwords = new AdminPasswordStore(store);
        passwords.Remember(f.Profile, "private-fixture-A"); passwords.Remember(other, "private-fixture-B");
        passwords.Remember(f.Profile, "");
        var reopened = new AdminPasswordStore(new(f.Root));
        Assert.Equal("private-fixture-A", reopened.Read(f.Profile)); Assert.Equal("private-fixture-B", reopened.Read(other));
        Assert.Null(reopened.Read(other with { Id = "unknown" }));
        var path = Path.Combine(f.Root, "server-admin-passwords.json");
        if (OperatingSystem.IsWindows()) Assert.DoesNotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("private-fixture-A")), File.ReadAllText(path));
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path + ".bak"));
        }
        Assert.Empty(Directory.GetFiles(f.Root, "*.tmp"));
    }
    [Fact]
    public void CorruptedCredentialFailsWithoutReplacingItsFile()
    {
        using var f = new Fixture(); var store = new JsonStore(f.Root);
        store.Write("server-admin-passwords.json", new Dictionary<string, string> { [f.Profile.Id] = "invalid!" });
        var original = File.ReadAllText(Path.Combine(f.Root, "server-admin-passwords.json"));
        Assert.Throws<IOException>(() => new AdminPasswordStore(store).Read(f.Profile));
        Assert.Equal(original, File.ReadAllText(Path.Combine(f.Root, "server-admin-passwords.json")));
    }
}
