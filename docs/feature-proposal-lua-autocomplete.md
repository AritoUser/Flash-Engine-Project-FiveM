# [Feature Request] Auto-generate EmmyLua definitions (.d.lua) for C# exports

## Status Quo & Motivation

The Flash-Engine supports calling C# exports from other resources. However, when writing a C# export to be consumed by Lua client/server scripts, there is currently no integration or link between the two environments in the code editor (e.g., VS Code using the sumneko Lua Language Server).

* **Manual Maintenance:** Developers have to manually type the export name, parameter types, and count in the Lua script, hoping they match the C# implementation.
* **No IntelliSense:** There is no autocomplete, type checking, or inline documentation (hover descriptions) for C# exports inside Lua files.
* **Error-prone:** Simple typos in export names or arguments are only caught at runtime as errors.

---

## Proposed Goal

When compiling a C# resource (e.g., via `dotnet build` or the Flash compilation CLI), the engine should automatically generate an EmmyLua-compliant definition file (e.g., `<resource_name>.d.lua`) inside the resource's output folder.

When a developer types `exports.MyFirstResource:` in a Lua script, VS Code should immediately show:
1. **Autocomplete suggestions** for all C# exported functions.
2. **Type hints** for both arguments and return values.
3. **Hover documentation** populated from C# XML documentation comments.

---

## Technical Design

To achieve this, we need to extract metadata from the C# code and map it to EmmyLua structure.

### 1. Extracting C# Exports

Since C# exports are currently registered dynamically at runtime via `Exports.Register(...)`, static code analysis is required. Two approaches could be considered:

* **Approach A: Roslyn Source Generator / Build Task (Recommended)**
  An MSBuild task or a Roslyn Source Generator scans the resource source code for calls to `Flash.Exports.Register` (both untyped and typed overloads like `Exports.Register<T1, T2, TResult>`).
* **Approach B: Declarative `[FlashExport]` Attribute**
  Introduce a `[FlashExport("name")]` attribute that can be placed on methods (similar to `[EventHandler]`). The resource can register all marked methods via `Exports.RegisterAll(this)`. The generator parses these marked methods at compile time. This is extremely robust and type-safe.

#### Declarative Export Example (C#):
```csharp
public sealed class Main : IResource
{
    public void OnStart()
    {
        // Automatically registers all methods marked with [FlashExport] in this instance
        Exports.RegisterAll(this); 
    }

    /// <summary>
    /// Calculates the sum of two integers.
    /// </summary>
    /// <param name="a">The first operand</param>
    /// <param name="b">The second operand</param>
    /// <returns>The sum of a and b</returns>
    [FlashExport("addTyped")]
    public int AddTyped(int a, int b)
    {
        return a + b;
    }
}
```

---

### 2. Generating the EmmyLua Definition (`.d.lua`)

Based on the parsed metadata, the compiler outputs a `.d.lua` file.

#### Generated Output Example (`MyFirstResource.d.lua`):
```lua
---@meta

---@class MyFirstResourceExports
local MyFirstResourceExports = {}

--- Calculates the sum of two integers.
---@param a number The first operand
---@param b number The second operand
---@return number The sum of a and b
function MyFirstResourceExports:addTyped(a, b) end

--- Map the exports instance for VS Code IntelliSense
---@type MyFirstResourceExports
exports.MyFirstResource = nil
```

---

### 3. C# to Lua Type Mapping

The generator should translate C# types into EmmyLua type annotations:

| C# Type | EmmyLua Type | Notes |
|---|---|---|
| `int`, `float`, `double`, `long`, `short`, `decimal` | `number` | All numeric types |
| `string`, `char` | `string` | Text |
| `bool` | `boolean` | Booleans |
| `object[]`, `List<T>`, `T[]` | `table` or `any[]` | Array / List collections |
| `Dictionary<K, V>` | `table<any, any>` | Key-value dictionaries |
| `Action`, `Func` | `fun(...)` | Callbacks (Funcrefs) |
| `void` | `void` | No return value |
| Custom classes/structs | `table` or `any` | Complex structures |

---

## Workflow Integration

1. **Build Process:** An MSBuild build target (e.g., in `Flash.Sdk.targets`) runs the generator during `dotnet build`, dumping the `.d.lua` file directly into the resource's output folder.
2. **VS Code configuration:** The Lua Language Server (sumneko.lua) scans the workspace directory. By having the generated `.d.lua` file in the workspace library or resource folder, VS Code picks up the definitions automatically without any extra configuration.

---

## Benefits for Developers

* **No Context Switching:** Developers no longer need to switch back and forth to inspect C# files for export names and arguments.
* **Type Safety:** The Lua compiler in VS Code warns the developer if the wrong type is passed to a C# export.
* **Self-Documenting:** Developer documentation written in C# via XML comments is preserved and displayed as hover tips in Lua.
