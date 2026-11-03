// Prints every type in a managed assembly: kind|full name|base type|serialized fields (type name:field).
// Usage: TypeDump <assembly.dll> <search dir> [namespace filter]
using System.Reflection;
using System.Runtime.InteropServices;

if (args.Length >= 2 && args[0] == "--plugins")
{
    // Usage: TypeDump --plugins <BepInEx/plugins dir> <Managed dir> [BepInEx/core dir]
    // Prints GUID|name|version for every BepInPlugin in the tree (attribute data read from metadata, no loading).
    var pluginDir = args[1];
    var search = new List<string>();
    foreach (var d in args.Skip(2)) search.AddRange(Directory.GetFiles(d, "*.dll"));
    var pluginDlls = Directory.GetFiles(pluginDir, "*.dll", SearchOption.AllDirectories);
    search.AddRange(pluginDlls);
    using var ctx = new MetadataLoadContext(new PathAssemblyResolver(search.Distinct()), "mscorlib");
    foreach (var pd in pluginDlls)
    {
        Type[] ts;
        try { ts = ctx.LoadFromAssemblyPath(pd).GetTypes(); }
        catch (ReflectionTypeLoadException e) { ts = e.Types.Where(t => t != null).ToArray()!; }
        catch (Exception e) { Console.WriteLine($"!|{Path.GetFileName(pd)}|{e.GetType().Name}"); continue; }
        foreach (var t in ts)
        {
            CustomAttributeData? a = null;
            try { a = t.GetCustomAttributesData().FirstOrDefault(x => x.AttributeType.Name == "BepInPlugin"); } catch { }
            if (a == null || a.ConstructorArguments.Count < 3) continue;
            Console.WriteLine($"{a.ConstructorArguments[0].Value}|{a.ConstructorArguments[1].Value}|{a.ConstructorArguments[2].Value}|{Path.GetFileName(pd)}");
        }
    }
    return;
}
var dll = args[0]; var dir = args[1]; var filter = args.Length > 2 ? args[2] : "";
// --members: every type (not only public) with its fields, methods (name(param types)) and properties.
bool members = args.Contains("--members");
filter = filter == "--members" ? "" : filter;
// Search dirs are ';'-separated (game Managed + BepInEx/core + plugin folders). The game folder carries its own
// mscorlib/netstandard; do not mix in the host runtime (duplicate core assembly).
var paths = dir.Split(';').Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "*.dll"))
    .GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
if (!paths.Any(p => Path.GetFileName(p).Equals(Path.GetFileName(dll), StringComparison.OrdinalIgnoreCase))) paths.Add(dll);
using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths), "mscorlib");
var asm = mlc.LoadFromAssemblyPath(dll);
Type[] types;
try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
foreach (var t in types.OrderBy(t => t.FullName))
{
    if (t.FullName is null || (filter.Length > 0 && !t.FullName.StartsWith(filter))) continue;
    if (!members && !(t.IsPublic || t.IsNestedPublic)) continue;
    if (members && t.Name.StartsWith("<")) continue;
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
    string baseName = "";
    try { baseName = t.BaseType?.FullName ?? ""; } catch { baseName = "?"; }
    Console.WriteLine($"{kind}|{t.FullName}|{baseName}|{(t.IsAbstract && kind == "class" ? "abstract" : "")}|{string.Join(";", fields)}");
    if (!members || kind is not ("class" or "struct")) continue;
    var all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    foreach (var f in t.GetFields(all))
        Console.WriteLine($"    field {(f.IsStatic ? "static " : "")}{f.FieldType.Name} {f.Name}");
    foreach (var pr in t.GetProperties(all))
        Console.WriteLine($"    prop {pr.PropertyType.Name} {pr.Name}");
    foreach (var m in t.GetMethods(all))
    {
        if (m.IsSpecialName) continue;
        string ps;
        try { ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)); } catch { ps = "?"; }
        string ret; try { ret = m.ReturnType.Name; } catch { ret = "?"; }
        Console.WriteLine($"    method {(m.IsStatic ? "static " : "")}{ret} {m.Name}({ps})");
    }
}
