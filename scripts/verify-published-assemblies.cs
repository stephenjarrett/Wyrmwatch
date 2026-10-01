// .NET 10 file-based build check. No extra packages or test framework required.
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

if (args.Length != 2) throw new ArgumentException("Expected assembly-input manifest and publish folder.");
var inputs = File.ReadAllLines(args[0]).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToArray();
if (inputs.Length == 0) throw new IOException("The publish assembly manifest is empty.");
var checkedAssemblies = 0;
var removed = 0;
foreach (var input in inputs)
{
    var published = Path.Combine(args[1], Path.GetFileName(input));
    if (!File.Exists(published)) { removed++; continue; }
    using var originalStream = File.OpenRead(input);
    using var publishedStream = File.OpenRead(published);
    using var originalPe = new PEReader(originalStream);
    using var publishedPe = new PEReader(publishedStream);
    var original = originalPe.GetMetadataReader();
    var result = publishedPe.GetMetadataReader();
    // Assembly references and unused type forwarders may change. Definitions must not.
    foreach (var table in new[] { TableIndex.TypeDef, TableIndex.MethodDef, TableIndex.Field,
        TableIndex.Property, TableIndex.Event, TableIndex.Param, TableIndex.InterfaceImpl,
        TableIndex.GenericParam, TableIndex.GenericParamConstraint, TableIndex.ManifestResource })
        if (original.GetTableRowCount(table) != result.GetTableRowCount(table))
            throw new IOException($"Member pruning detected in {Path.GetFileName(input)}: {table}.");
    // Cecil may reorder nested definitions when rewriting precompiled libraries.
    if (!Types(original).SequenceEqual(Types(result)))
        throw new IOException($"Type definitions changed in {Path.GetFileName(input)}.");
    checkedAssemblies++;
}
Console.WriteLine($"PASS: {checkedAssemblies} retained assemblies preserve member and resource definitions; {removed} unused assemblies omitted.");

static IEnumerable<string> Types(MetadataReader reader) => reader.TypeDefinitions
    .Select(handle => Name(reader, handle)).Order(StringComparer.Ordinal);
static string Name(MetadataReader reader, TypeDefinitionHandle handle)
{
    var type = reader.GetTypeDefinition(handle);
    var parent = type.GetDeclaringType();
    return (parent.IsNil ? reader.GetString(type.Namespace) : Name(reader, parent)) + "/" + reader.GetString(type.Name);
}
