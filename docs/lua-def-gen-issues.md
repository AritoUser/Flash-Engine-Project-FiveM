# Tooling Audit: EmmyLua Definition Generator (Flash.LuaDefGen)

We conducted a review of the newly introduced EmmyLua definition generator (`Flash.LuaDefGen`) in version 0.5.0. While the tool is a major quality-of-life addition for C#↔Lua interop, we identified three concrete issues/bugs that can lead to incorrect type signatures, lost documentation, or build crashes under specific conditions.

---

## 1. Bug: ValueTask & ValueTask<T> Return Types Map to 'table'

* **Component:** `tools/Flash.LuaDefGen/Program.cs` (`LuaType.MapReturn` and `LuaType.Map`)
* **Severity:** Medium

### Description
In modern .NET development, `ValueTask` and `ValueTask<T>` are commonly used for high-performance async methods to reduce allocation overhead. 

Currently, `MapReturn` only unwraps or ignores `System.Threading.Tasks.Task` and `System.Threading.Tasks.Task`1`. If an export returns a `ValueTask` or `ValueTask<int>`, it falls back to the generic `Map(Type)` method, which resolves it as a custom C# struct, mapping it to `"table"`.

### Impact
* An export returning `ValueTask` (which is functionally `void` for Lua callers) is documented as returning a `table`.
* An export returning `ValueTask<int>` (which is functionally returning a `number`) is annotated as `---@return table` instead of `---@return number`, breaking Autocomplete and type-checking in VS Code.

### Proposed Fix
Update `MapReturn` to unwrap `ValueTask` and `ValueTask<T>`:
```csharp
public static string? MapReturn(Type t)
{
    if (t.FullName == "System.Void") return null;
    
    // Unwrap standard Tasks
    if (t.FullName == "System.Threading.Tasks.Task") return null;
    if (t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Threading.Tasks.Task`1")
        return Map(t.GetGenericArguments()[0]);

    // Unwrap ValueTasks
    if (t.FullName == "System.Threading.Tasks.ValueTask") return null;
    if (t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Threading.Tasks.ValueTask`1")
        return Map(t.GetGenericArguments()[0]);

    return Map(t);
}
```

---

## 2. Bug: XML Doc ID Resolution Mismatch for Generic Method Overloads

* **Component:** `tools/Flash.LuaDefGen/Program.cs` (`XmlDocs.TypeId` and `XmlDocs.DocId`)
* **Severity:** Medium

### Description
When the C# compiler generates an XML documentation file, it identifies generic parameters in method signatures using a backtick index notation (e.g. ```0` for the first generic argument of a generic method).

Currently, `TypeId` resolves any generic parameter by falling back to `(t.FullName ?? t.Name)` which returns `"T"` (the literal type parameter name). Therefore, `DocId` generates `M:Namespace.Type.MyMethod(T)` instead of the compiler's `M:Namespace.Type.MyMethod``1(``0)`. 

While `XmlDocs.For` has a prefix-matching fallback (`byPrefix.Count == 1`), this prefix-matching **fails** if a generic method has any overloads with the same name, as `byPrefix.Count` will be greater than 1.

### Impact
Any overloaded generic export methods will fail both the exact and prefix-matching lookups, resulting in **lost XML documentation comments** (empty hover tooltips) in VS Code.

### Proposed Fix
Enhance `TypeId` to check if a type is a generic parameter and format it with backticks. Note that we must distinguish between method-level generic parameters (prefixed with two backticks ```` ` in type reference names) and class-level generic parameters (prefixed with one backtick ``` `):
```csharp
private static string TypeId(Type t)
{
    if (t.IsByRef) return TypeId(t.GetElementType()!) + "@";
    if (t.IsArray) return TypeId(t.GetElementType()!) + "[]";
    
    if (t.IsGenericParameter)
    {
        // DeclaringMethod is not null for method-level generic parameters
        return t.DeclaringMethod != null ? "``" + t.GenericParameterPosition : "`" + t.GenericParameterPosition;
    }
    
    if (t.IsGenericType)
    {
        string def = (t.GetGenericTypeDefinition().FullName ?? "").Replace('+', '.');
        int tick = def.IndexOf('`');
        if (tick >= 0) def = def[..tick];
        return $"{def}{{{string.Join(",", t.GetGenericArguments().Select(TypeId))}}}";
    }
    return (t.FullName ?? t.Name).Replace('+', '.');
}
```

---

## 3. Bug: Potential ArgumentNullException Crash in CLI Path Resolver

* **Component:** `tools/Flash.LuaDefGen/Program.cs` (`CollectExports`)
* **Severity:** Low

### Description
In `CollectExports`, the tool adds all DLLs from the standard .NET runtime directory to its resolution path:
```csharp
foreach (var f in Directory.GetFiles(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll")) Add(f);
```
However, in certain environments (such as single-file publishing via `<PublishSingleFile>true</PublishSingleFile>` or virtualized execution hosts), `Assembly.Location` returns an empty string `""`. 
In this case, `Path.GetDirectoryName("")` returns `null`, and passing `null` to `Directory.GetFiles` throws a crash-level `ArgumentNullException`.

### Impact
An unhandled exception that crashes the build tool completely, breaking the post-build step in custom CI/CD or specialized hosting environments.

### Proposed Fix
Safeguard the `Location` lookup:
```csharp
string? coreLoc = typeof(object).Assembly.Location;
if (!string.IsNullOrEmpty(coreLoc))
{
    string? coreDir = Path.GetDirectoryName(coreLoc);
    if (coreDir != null && Directory.Exists(coreDir))
    {
        foreach (var f in Directory.GetFiles(coreDir, "*.dll")) Add(f);
    }
}
```
