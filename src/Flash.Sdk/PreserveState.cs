using System;

namespace Flash;

/// <summary>
/// Flags a STATIC field or property to survive a resource restart/hot-reload (#117).
///
/// A restart unloads the resource's collectible AssemblyLoadContext — all in-memory
/// state dies with it, which makes iterative development painful (log back in, rebuild
/// the test lobby, ...). Before the unload the host serializes every decorated static
/// member to JSON, and after the new assembly loads it deserializes the values back
/// into the matching members BEFORE <c>OnStart()</c> runs.
///
/// RULES:
///  - static members only (instance state dies with the instance by design);
///  - the value must be System.Text.Json-serializable (primitives, strings, arrays,
///    lists, dictionaries, POCOs with public properties). Unserializable values are
///    logged and skipped — never fatal;
///  - the JSON round-trip is deliberate: it breaks object references into the OLD
///    assembly, so preserved state can never pin the unloaded ALC;
///  - matching is by key: <see cref="CustomKey"/> if given, otherwise the fully
///    qualified member name — rename the member (or set a stable key) and the old
///    value is dropped;
///  - state lives in host memory only (a dev/QoL feature): a server restart clears it.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class PreserveStateAttribute : Attribute
{
    /// <summary>Optional stable key; default is the fully qualified member name.</summary>
    public string? CustomKey { get; }
    public PreserveStateAttribute(string? customKey = null) => CustomKey = customKey;
}
