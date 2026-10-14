using Wyrmwatch.Core;

namespace Wyrmwatch.Core.Tests;

public class LogTailTests
{
    [Fact]
    public void LargeAndGrowingLogRetainsOnlyRecentBoundedLines()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "dummy-game.log");
        using (var writer = new StreamWriter(path))
            for (var i = 0; i < 20000; i++) writer.WriteLine($"fixture-{i:D5} " + new string('x', 100));
        for (var sample = 0; sample < 10; sample++)
        {
            File.AppendAllText(path, $"latest-fixture-{sample}\n");
            var tail = LogTail.Read(path);
            Assert.InRange(tail.Length, 1, 65536); Assert.InRange(tail.Split('\n').Length, 1, 300);
            Assert.Contains($"latest-fixture-{sample}", tail); Assert.DoesNotContain("fixture-00000", tail);
        }
    }
}
