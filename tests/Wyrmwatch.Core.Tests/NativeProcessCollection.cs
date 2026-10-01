namespace Wyrmwatch.Core.Tests;

// These integration tests intentionally share the host's global process inventory.
// Separate directories do not isolate same-name actors during startup/teardown.
// Run the collection exclusively while leaving pure fixture/unit tests parallel.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NativeProcessCollection
{
    public const string Name = "Native process inventory";
}
