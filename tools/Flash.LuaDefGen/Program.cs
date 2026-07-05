using System.Reflection;
using System.Text;
using System.Xml.Linq;

namespace Flash.LuaDefGen;

// =====================================================================================
//  EmmyLua definition generator for Flash-Engine C# exports (#173).
//
//  Usage:
//    Flash.LuaDefGen --assembly <resource.dll> --resource <name> --out <file.d.lua>
//                    [--xml <resource.xml>] [--sdk <Flash.Sdk.dll>] ...
//
//  HOW: the compiled resource assembly is opened in a MetadataLoadContext (inspection
//  only, no code runs), every [FlashExport]-annotated method is collected with its
//  parameter/return types, C# XML documentation (<summary>/<param>/<returns>) is pulled
//  from the compiler-generated .xml file, and an EmmyLua @meta file is written that the
//  sumneko Lua Language Server picks up for autocomplete + type hints + hover docs.
//
//  WHY MetadataLoadContext (not Assembly.LoadFrom): the tool must inspect assemblies
//  built for the Flash host environment without executing them and without resolving
//  their full runtime closure -- MLC reads pure metadata against a path resolver.
//  All type checks below compare FULL NAMES (never typeof(...) identity): types inside
//  the MLC universe are distinct from the tool's own runtime types.
// =====================================================================================

internal static class Program
{
    private const string ExportAttributeFullName = "Flash.FlashExportAttribute";

    private static int Main(string[] rawArgs)
    {
        try
        {
            var args = CliArgs.Parse(rawArgs);
            if (args == null)
            {
                Console.Error.WriteLine(
                    "usage: Flash.LuaDefGen --assembly <resource.dll> --resource <name> --out <file.d.lua> " +
                    "[--xml <docs.xml>] [--sdk <Flash.Sdk.dll>]...");
                return 2;
            }

            var exports = CollectExports(args);
            var docs = XmlDocs.Load(args.XmlPath);
            string lua = EmitLua(args.ResourceName, exports, docs);

            // Only touch the file when the content changed -> no needless rebuild churn
            // for tools that watch the output folder.
            if (!File.Exists(args.OutPath) || File.ReadAllText(args.OutPath) != lua)
                File.WriteAllText(args.OutPath, lua, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            Console.WriteLine($"Flash.LuaDefGen: {exports.Count} export(s) -> {args.OutPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Flash.LuaDefGen: {ex.Message}");
            return 1;
        }
    }

    // ---- Collection: open the assembly in an MLC and find [FlashExport] methods. ----
    //
    // Everything needed for emission is copied into plain records HERE, while the
    // MetadataLoadContext is still alive -- MLC reflection objects become invalid the
    // moment the context is disposed, so no Type/MethodInfo may escape this method.

    internal sealed record ExportParam(string Name, string LuaType, bool Optional);
    internal sealed record ExportMethod(string LuaName, string DocId, List<ExportParam> Params, string? ReturnLua);

    private static List<ExportMethod> CollectExports(CliArgs args)
    {
        // Resolver paths: the .NET runtime (core assemblies), the resource's own output
        // folder (private helper assemblies), any explicitly passed Flash.Sdk, and the
        // tool's folder as a last resort. Duplicate simple names keep the FIRST hit.
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string file) { var key = Path.GetFileNameWithoutExtension(file); paths.TryAdd(key, file); }

        Add(args.AssemblyPath);
        foreach (var sdk in args.SdkPaths) Add(sdk);
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath(args.AssemblyPath))!, "*.dll")) Add(f);
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll")) Add(f);
        foreach (var f in Directory.GetFiles(AppContext.BaseDirectory, "*.dll")) Add(f);

        var resolver = new PathAssemblyResolver(paths.Values);
        using var mlc = new MetadataLoadContext(resolver);
        var asm = mlc.LoadFromAssemblyPath(Path.GetFullPath(args.AssemblyPath));

        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException rtle) { types = rtle.Types.Where(t => t != null).ToArray()!; }

        var result = new List<ExportMethod>();
        foreach (var type in types)
        {
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                              BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                foreach (var cad in SafeAttributes(m))
                {
                    if (cad.AttributeType.FullName != ExportAttributeFullName) continue;
                    if (cad.ConstructorArguments.Count > 0 && cad.ConstructorArguments[0].Value is string luaName
                        && luaName.Length > 0)
                    {
                        var ps = m.GetParameters().Select((p, i) => new ExportParam(
                            p.Name ?? $"arg{i}",
                            LuaType.Map(p.ParameterType),
                            (p.Attributes & ParameterAttributes.Optional) != 0)).ToList();
                        result.Add(new ExportMethod(luaName, XmlDocs.DocId(m), ps, LuaType.MapReturn(m.ReturnType)));
                    }
                }
            }
        }
        // Stable output: sort by export name so regeneration is deterministic.
        return result.OrderBy(e => e.LuaName, StringComparer.Ordinal).ToList();
    }

    private static IList<CustomAttributeData> SafeAttributes(MethodInfo m)
    {
        // An attribute whose defining assembly is unresolvable must not kill the scan --
        // the method simply contributes nothing.
        try { return m.GetCustomAttributesData(); }
        catch { return Array.Empty<CustomAttributeData>(); }
    }

    // ---- Emission: the EmmyLua @meta file. ----

    private static string EmitLua(string resourceName, List<ExportMethod> exports, XmlDocs docs)
    {
        // "@class SampleResourceExports" needs an identifier-safe name even when the
        // resource is called "my-resource".
        string className = Sanitize(resourceName) + "Exports";

        var sb = new StringBuilder();
        sb.AppendLine("---@meta");
        sb.AppendLine("-- Auto-generated by Flash.LuaDefGen from the [FlashExport] methods of this resource.");
        sb.AppendLine("-- Do not edit -- regenerated on every build.");
        sb.AppendLine();
        sb.AppendLine($"---@class {className}");
        sb.AppendLine($"local {className} = {{}}");

        foreach (var export in exports)
        {
            var doc = docs.For(export.DocId);

            sb.AppendLine();
            if (doc.Summary.Length > 0)
                sb.AppendLine($"--- {doc.Summary}");

            var luaParamNames = new List<string>();
            foreach (var p in export.Params)
            {
                string name = SafeLuaIdentifier(p.Name);
                luaParamNames.Add(name);
                string pdoc = doc.Params.TryGetValue(p.Name, out var d) && d.Length > 0 ? " " + d : "";
                // Optional C# parameter (has a default) -> optional Lua parameter (name?).
                sb.AppendLine($"---@param {name}{(p.Optional ? "?" : "")} {p.LuaType}{pdoc}");
            }

            if (export.ReturnLua != null)
                sb.AppendLine($"---@return {export.ReturnLua}{(doc.Returns.Length > 0 ? " # " + doc.Returns : "")}");

            sb.AppendLine($"function {className}:{SafeLuaIdentifier(export.LuaName)}({string.Join(", ", luaParamNames)}) end");
        }

        sb.AppendLine();
        sb.AppendLine($"---@type {className}");
        // Bracket syntax keeps resource names with '-' (not valid Lua identifiers) working.
        sb.AppendLine(IsLuaIdentifier(resourceName)
            ? $"exports.{resourceName} = nil"
            : $"exports[\"{resourceName}\"] = nil");
        return sb.ToString();
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    private static readonly HashSet<string> s_luaKeywords = new(StringComparer.Ordinal)
    {
        "and","break","do","else","elseif","end","false","for","function","goto","if","in",
        "local","nil","not","or","repeat","return","then","true","until","while",
    };

    private static string SafeLuaIdentifier(string name)
        => s_luaKeywords.Contains(name) || !IsLuaIdentifier(name) ? Sanitize(name) + "_" : name;

    private static bool IsLuaIdentifier(string s)
        => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_')
           && s.All(c => char.IsLetterOrDigit(c) || c == '_') && !s_luaKeywords.Contains(s);
}

/// <summary>C# type → EmmyLua type annotation (full-name based; MLC types never compare
/// equal to the tool's own runtime types).</summary>
internal static class LuaType
{
    private static readonly HashSet<string> s_numbers = new(StringComparer.Ordinal)
    {
        "System.Byte","System.SByte","System.Int16","System.UInt16","System.Int32","System.UInt32",
        "System.Int64","System.UInt64","System.Single","System.Double","System.Decimal",
    };

    public static string Map(Type t)
    {
        // Nullable<T> -> T|nil.
        if (t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Nullable`1")
            return Map(t.GetGenericArguments()[0]) + "|nil";
        if (t.IsArray) return Map(t.GetElementType()!) + "[]";

        string full = t.IsGenericType ? t.GetGenericTypeDefinition().FullName ?? "" : t.FullName ?? "";
        if (s_numbers.Contains(full)) return "number";
        if (full is "System.String" or "System.Char") return "string";
        if (full == "System.Boolean") return "boolean";
        if (full == "System.Object") return "any";
        if (full == "Flash.Funcref") return "fun(...):any";
        if (full.StartsWith("System.Collections.Generic.Dictionary`2", StringComparison.Ordinal)
            || full.StartsWith("System.Collections.Generic.IDictionary`2", StringComparison.Ordinal)
            || full.StartsWith("System.Collections.Generic.IReadOnlyDictionary`2", StringComparison.Ordinal))
        {
            var a = t.GetGenericArguments();
            return $"table<{Map(a[0])}, {Map(a[1])}>";
        }
        if (full.StartsWith("System.Collections.Generic.List`1", StringComparison.Ordinal)
            || full.StartsWith("System.Collections.Generic.IList`1", StringComparison.Ordinal)
            || full.StartsWith("System.Collections.Generic.IReadOnlyList`1", StringComparison.Ordinal)
            || full.StartsWith("System.Collections.Generic.IEnumerable`1", StringComparison.Ordinal)
            || full.StartsWith("System.Collections.Generic.ICollection`1", StringComparison.Ordinal))
            return Map(t.GetGenericArguments()[0]) + "[]";
        if (IsDelegate(t)) return "fun(...):any";
        // POCOs/structs cross the export boundary as msgpack maps -> a Lua table.
        return "table";
    }

    /// <summary>Return-type mapping; null = no @return line (void).</summary>
    public static string? MapReturn(Type t)
    {
        if (t.FullName == "System.Void") return null;
        // Task / Task<T>: exports are synchronous from the Lua side; unwrap for the annotation.
        if (t.FullName == "System.Threading.Tasks.Task") return null;
        if (t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Threading.Tasks.Task`1")
            return Map(t.GetGenericArguments()[0]);
        return Map(t);
    }

    private static bool IsDelegate(Type t)
    {
        for (Type? b = t.BaseType; b != null; b = b.BaseType)
            if (b.FullName == "System.MulticastDelegate") return true;
        return false;
    }
}

/// <summary>Reads the compiler-generated XML documentation file and resolves the
/// &lt;summary&gt;/&lt;param&gt;/&lt;returns&gt; blocks for a method.</summary>
internal sealed class XmlDocs
{
    internal sealed record Entry(string Summary, Dictionary<string, string> Params, string Returns);
    private static readonly Entry s_empty = new("", new(), "");

    private readonly Dictionary<string, Entry> _members = new(StringComparer.Ordinal);

    public static XmlDocs Load(string? xmlPath)
    {
        var docs = new XmlDocs();
        if (xmlPath == null || !File.Exists(xmlPath)) return docs;
        foreach (var member in XDocument.Load(xmlPath).Descendants("member"))
        {
            string? name = member.Attribute("name")?.Value;
            if (name == null || !name.StartsWith("M:", StringComparison.Ordinal)) continue;
            var ps = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in member.Elements("param"))
                ps[p.Attribute("name")?.Value ?? ""] = Flatten(p);
            docs._members[name] = new Entry(
                Flatten(member.Element("summary")), ps, Flatten(member.Element("returns")));
        }
        return docs;
    }

    public Entry For(string docId)
    {
        // Exact doc-comment ID first; if the parameter-list encoding has an edge we did
        // not cover (rare generics), fall back to the unique "M:Type.Name(" prefix.
        if (_members.TryGetValue(docId, out var exact)) return exact;
        string prefix = docId.Contains('(') ? docId[..(docId.IndexOf('(') + 1)] : docId;
        var byPrefix = _members.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        return byPrefix.Count == 1 ? byPrefix[0].Value : s_empty;
    }

    // Builds the compiler's doc-comment ID: M:Namespace.Type.Method(Param1,Param2).
    internal static string DocId(MethodInfo m)
    {
        string type = (m.DeclaringType?.FullName ?? "").Replace('+', '.');
        var ps = m.GetParameters();
        if (ps.Length == 0) return $"M:{type}.{m.Name}";
        return $"M:{type}.{m.Name}({string.Join(",", ps.Select(p => TypeId(p.ParameterType)))})";
    }

    private static string TypeId(Type t)
    {
        if (t.IsByRef) return TypeId(t.GetElementType()!) + "@";
        if (t.IsArray) return TypeId(t.GetElementType()!) + "[]";
        if (t.IsGenericType)
        {
            string def = (t.GetGenericTypeDefinition().FullName ?? "").Replace('+', '.');
            int tick = def.IndexOf('`');
            if (tick >= 0) def = def[..tick];
            return $"{def}{{{string.Join(",", t.GetGenericArguments().Select(TypeId))}}}";
        }
        return (t.FullName ?? t.Name).Replace('+', '.');
    }

    private static string Flatten(XElement? e)
        => e == null ? "" : string.Join(" ", e.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>Minimal CLI parsing (no dependency): --assembly/--resource/--out required.</summary>
internal sealed record CliArgs(
    string AssemblyPath, string ResourceName, string OutPath, string? XmlPath, List<string> SdkPaths)
{
    public static CliArgs? Parse(string[] args)
    {
        string? asm = null, res = null, @out = null, xml = null;
        var sdks = new List<string>();
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            string value = args[i + 1];
            switch (args[i])
            {
                case "--assembly": asm = value; break;
                case "--resource": res = value; break;
                case "--out": @out = value; break;
                case "--xml": xml = value; break;
                case "--sdk": sdks.Add(value); break;
                default: return null;
            }
        }
        if (asm == null || res == null || @out == null || !File.Exists(asm)) return null;
        return new CliArgs(asm, res, @out, xml, sdks);
    }
}
