using System.Text;
using Wyrmwatch.Core;

namespace Wyrmwatch.Core.Tests;

public class WorldImportTests
{
    private sealed class ImportFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "wyrmwatch-worldimport-test-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "source", "Fixture world.sav");
        public string Destination => Path.Combine(Root, "stage", "SaveGames");
        public ImportFixture() { Directory.CreateDirectory(Path.GetDirectoryName(Source)!); File.WriteAllBytes(Source, Container()); }
        public void Dispose()
        {
            if (!SafePaths.Within(Root, Path.GetTempPath()) || !Path.GetFileName(Root).StartsWith("wyrmwatch-worldimport-test-", StringComparison.Ordinal)) throw new IOException("Unsafe test cleanup path.");
            Directory.Delete(Root, true);
        }
    }

    // Synthetic SAVE/SPUD envelopes exercise framing, not real world/game compatibility.
    private static byte[] Chunk(string kind, byte[] payload)
    {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.ASCII, true);
        writer.Write(Encoding.ASCII.GetBytes(kind)); writer.Write((uint)payload.Length); writer.Write(payload); return output.ToArray();
    }
    private static byte[] Container(params byte[][] chunks) => Chunk("SAVE", (chunks.Length == 0
        ? new[] { Chunk("INFO", [1, 0, 0, 0]), Chunk("GLOB", new byte[12]) } : chunks).SelectMany(b => b).ToArray());

    [Fact]
    public async Task InspectionIsReadOnlyAndReviewTokenDeterministic()
    {
        using var f = new ImportFixture(); var bytes = File.ReadAllBytes(f.Source); var written = File.GetLastWriteTimeUtc(f.Source);
        var first = await WorldImport.InspectAsync(f.Source); var second = await WorldImport.InspectAsync(f.Source);
        Assert.Equal(first, second); Assert.Equal(bytes.LongLength, first.Length); Assert.Equal(64, first.Sha256.Length); Assert.Equal(64, first.ReviewToken.Length);
        Assert.Equal(bytes, File.ReadAllBytes(f.Source)); Assert.Equal(written, File.GetLastWriteTimeUtc(f.Source));
        Assert.False(Directory.Exists(f.Destination)); Assert.Single(Directory.GetFiles(Path.GetDirectoryName(f.Source)!));
    }

    [Fact]
    public async Task CopiesOnlyReviewedWorldAndPreservesSourceExactly()
    {
        using var f = new ImportFixture(); var sourceParent = Path.GetDirectoryName(f.Source)!;
        File.WriteAllText(Path.Combine(sourceParent, "Fixture world.sav.backup"), "unrelated backup");
        File.WriteAllText(Path.Combine(sourceParent, "player.json"), "unrelated character");
        var bytes = File.ReadAllBytes(f.Source); var plan = await WorldImport.InspectAsync(f.Source);
        var copied = await WorldImport.CopyAsync(plan, f.Destination);
        Assert.Equal(Path.Combine(f.Destination, plan.FileName), copied); Assert.Equal(bytes, File.ReadAllBytes(copied));
        Assert.Equal(bytes, File.ReadAllBytes(f.Source)); Assert.Equal(plan.LastWriteUtc, File.GetLastWriteTimeUtc(f.Source));
        Assert.Equal(plan.LastWriteUtc, File.GetLastWriteTimeUtc(copied)); Assert.Single(Directory.GetFiles(f.Destination));
        Assert.Equal(3, Directory.GetFiles(sourceParent).Length);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("text")]
    [InlineData("gvas-input-settings")]
    [InlineData("wrong-root-length")]
    [InlineData("missing-info")]
    [InlineData("empty-info")]
    [InlineData("missing-world")]
    [InlineData("truncated-chunk")]
    [InlineData("trailing-fragment")]
    public async Task RejectsWrongOrIncompleteContainers(string defect)
    {
        using var f = new ImportFixture(); byte[] bytes = defect switch
        {
            "empty" => [],
            "text" => Encoding.UTF8.GetBytes(new string('x', 64)),
            "gvas-input-settings" => Encoding.ASCII.GetBytes("GVAS" + new string('x', 60)),
            "missing-info" => Container(Chunk("CINF", new byte[4]), Chunk("GLOB", new byte[12])),
            "empty-info" => Container(Chunk("INFO", []), Chunk("GLOB", new byte[12])),
            "missing-world" => Container(Chunk("INFO", new byte[32])),
            "trailing-fragment" => Chunk("SAVE", Chunk("INFO", new byte[4]).Concat(Chunk("GLOB", new byte[12])).Concat(new byte[3]).ToArray()),
            _ => Container()
        };
        if (defect == "wrong-root-length") bytes[4]--;
        if (defect == "truncated-chunk") Array.Fill(bytes, (byte)255, 12, 4);
        File.WriteAllBytes(f.Source, bytes);
        await Assert.ThrowsAsync<IOException>(() => WorldImport.InspectAsync(f.Source)); Assert.False(Directory.Exists(f.Destination));
        Assert.Equal(bytes, File.ReadAllBytes(f.Source));
    }

    [Fact]
    public async Task UnknownBoundedExtensionsArePreservedWithoutSemanticParsing()
    {
        using var f = new ImportFixture(); File.WriteAllBytes(f.Source, Container(Chunk("INFO", new byte[4]), Chunk("EXTR", [1, 2, 3]), Chunk("GLOB", new byte[12])));
        var plan = await WorldImport.InspectAsync(f.Source); var copied = await WorldImport.CopyAsync(plan, f.Destination);
        Assert.Equal(File.ReadAllBytes(f.Source), File.ReadAllBytes(copied));
    }

    [Fact]
    public async Task SameSizeSameTimestampContentChangeInvalidatesReview()
    {
        using var f = new ImportFixture(); var plan = await WorldImport.InspectAsync(f.Source);
        var bytes = File.ReadAllBytes(f.Source); bytes[^1] = 1; File.WriteAllBytes(f.Source, bytes); File.SetLastWriteTimeUtc(f.Source, plan.LastWriteUtc);
        await Assert.ThrowsAsync<IOException>(() => WorldImport.CopyAsync(plan, f.Destination)); Assert.False(Directory.Exists(f.Destination));
    }

    [Fact]
    public async Task SourceChangedAfterCopyIsNotPublished()
    {
        using var f = new ImportFixture(); var plan = await WorldImport.InspectAsync(f.Source);
        await Assert.ThrowsAsync<IOException>(() => WorldImport.CopyAsync(plan, f.Destination, _ =>
        {
            var bytes = File.ReadAllBytes(f.Source); bytes[^1] = 2; File.WriteAllBytes(f.Source, bytes); File.SetLastWriteTimeUtc(f.Source, plan.LastWriteUtc);
        }));
        Assert.Empty(Directory.GetFiles(f.Destination)); Assert.Equal(2, File.ReadAllBytes(f.Source)[^1]);
    }

    [Fact]
    public async Task ExistingDestinationIsPreserved()
    {
        using var f = new ImportFixture(); var plan = await WorldImport.InspectAsync(f.Source); Directory.CreateDirectory(f.Destination);
        var existing = Path.Combine(f.Destination, "existing.sav"); File.WriteAllText(existing, "keep");
        await Assert.ThrowsAsync<IOException>(() => WorldImport.CopyAsync(plan, f.Destination)); Assert.Equal("keep", File.ReadAllText(existing));
    }

    [Fact]
    public async Task NewManagedFolderCanShareAnAncestorWithSource()
    {
        using var f = new ImportFixture(); var plan = await WorldImport.InspectAsync(f.Source);
        var destination = Path.Combine(Path.GetDirectoryName(f.Source)!, "new-install", "SaveGames");
        var copied = await WorldImport.CopyAsync(plan, destination);
        Assert.Equal(File.ReadAllBytes(f.Source), File.ReadAllBytes(copied)); Assert.Equal(plan, await WorldImport.InspectAsync(f.Source));
    }

    [Fact]
    public async Task CancelledImportDoesNotCreateDestinationOrAlterSource()
    {
        using var f = new ImportFixture(); var plan = await WorldImport.InspectAsync(f.Source); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorldImport.CopyAsync(plan, f.Destination, cancel.Token));
        Assert.False(Directory.Exists(f.Destination)); Assert.Equal(plan, await WorldImport.InspectAsync(f.Source));
    }

    [Fact]
    public async Task LockedSourceFailsBeforeCreatingDestination()
    {
        using var f = new ImportFixture(); var plan = await WorldImport.InspectAsync(f.Source);
        using var writer = new FileStream(f.Source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => WorldImport.CopyAsync(plan, f.Destination)); Assert.False(Directory.Exists(f.Destination));
    }

    [Fact]
    public async Task CallerCannotSubstituteDestinationFilenameOrReviewFields()
    {
        using var f = new ImportFixture(); var plan = await WorldImport.InspectAsync(f.Source);
        await Assert.ThrowsAsync<IOException>(() => WorldImport.CopyAsync(plan with { FileName = "../other.sav" }, f.Destination));
        await Assert.ThrowsAsync<IOException>(() => WorldImport.CopyAsync(plan with { Sha256 = new string('0', 64) }, f.Destination));
        Assert.False(Directory.Exists(f.Destination));
    }

    [Fact]
    public async Task RelativePathsAndBackupExtensionsAreRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => WorldImport.InspectAsync("world.sav"));
        using var f = new ImportFixture(); var backup = f.Source + ".backup"; File.Copy(f.Source, backup);
        await Assert.ThrowsAsync<IOException>(() => WorldImport.InspectAsync(backup));
    }
}
