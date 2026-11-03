// Prints every type in a managed assembly: kind|full name|base type|serialized fields (type name:field).
// Usage: TypeDump <assembly.dll> <search dir> [namespace filter]
using System.Reflection;
using System.Runtime.InteropServices;

var dll = args[0]; var dir = args[1]; var filter = args.Length > 2 ? args[2] : "";
// The game folder carries its own mscorlib/netstandard; do not mix in the host runtime (duplicate core assembly).
var paths = Directory.GetFiles(dir, "*.dll").ToList();
if (!paths.Any(p => Path.GetFileName(p).Equals(Path.GetFileName(dll), StringComparison.OrdinalIgnoreCase))) paths.Add(dll);
using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths), "mscorlib");
var asm = mlc.LoadFromAssemblyPath(dll);
Type[] types;
try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
foreach (var t in types.OrderBy(t => t.FullName))
{
    if (t.FullName is null || (filter.Length > 0 && !t.FullName.StartsWith(filter))) continue;
    if (!(t.IsPublic || t.IsNestedPublic)) continue;
    string kind = t.IsEnum ? "enum" : t.IsValueType ? "struct" : t.IsInterface ? "interface" : "class";
    var fields = new List<string>();
    if (kind is "class" or "struct")
    {
        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            bool ser = f.IsPublic || f.GetCustomAttributesData().Any(a => a.AttributeType.Name == "SerializeField");
            if (f.GetCustomAttributesData().Any(a => a.AttributeType.Name == "NonSerializedAttribute")) ser = false;
            if (ser) fields.Add(f.FieldType.ToString() + ":" + f.Name);
        }
    }
    string baseName = t.BaseType?.FullName ?? "";
    Console.WriteLine($"{kind}|{t.FullName}|{baseName}|{(t.IsAbstract && kind == "class" ? "abstract" : "")}|{string.Join(";", fields)}");
}
