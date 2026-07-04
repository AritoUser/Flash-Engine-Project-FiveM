using System;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;

namespace Flash;

/// <summary>Marks a method as a chat/console command handler (#12) — register the
/// containing object via <see cref="Commands.RegisterAll"/>. Parameters are parsed
/// and validated automatically; parse errors reply a generated usage line.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CommandAttribute : Attribute
{
    public string Name { get; }
    /// <summary>true = only for ACE-authorized players (FiveM `restricted` flag).</summary>
    public bool Restricted { get; set; }
    public CommandAttribute(string name) => Name = name;
}

/// <summary>On the LAST string parameter: captures the rest of the line (spaces
/// included) instead of a single word — for reasons, messages, announcements.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class RestAttribute : Attribute { }

/// <summary>Invocation context of a routed command: who called, the raw line, and a
/// Reply that goes to the chat (or the console for source 0).</summary>
public readonly struct CommandContext
{
    /// <summary>NetID of the caller; 0 = server console.</summary>
    public int Source { get; }
    /// <summary>The full typed command line.</summary>
    public string Raw { get; }

    public CommandContext(int source, string raw) { Source = source; Raw = raw; }

    /// <summary>Replies to the caller: chat message for players, log line for the console.</summary>
    public void Reply(string message)
    {
        if (Source == 0) Log.Info(message);
        else Events.EmitClient(Source, "chat:addMessage",
            new System.Collections.Generic.Dictionary<string, object?> { ["args"] = new object?[] { message } });
    }
}

public static partial class Commands
{
    /// <summary>
    /// Registers every <c>[Command]</c>-annotated method of <paramref name="target"/>
    /// (public + non-public, instance + static). Supported parameter types:
    /// <see cref="CommandContext"/> (injected), int/long/float/double/bool/string and
    /// <see cref="ServerPlayer"/> (parsed from a netId argument). Optional parameters
    /// use their default when the caller omits them; a <c>[Rest]</c> string swallows
    /// the remaining line. Parse errors reply a generated usage line instead of
    /// reaching the handler. Async methods (Task return) are awaited safely.
    /// </summary>
    public static void RegisterAll(object target)
    {
        foreach (var m in target.GetType().GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            var attr = m.GetCustomAttribute<CommandAttribute>();
            if (attr == null) continue;

            var method = m;                       // capture
            var ps = m.GetParameters();
            string usage = BuildUsage(attr.Name, ps);

            Register(attr.Name, (src, args, raw) =>
            {
                if (!TryBind(ps, src, args, raw, out var call, out string? error))
                {
                    new CommandContext(src, raw).Reply(error ?? usage);
                    return;
                }
                try
                {
                    object? result = method.Invoke(method.IsStatic ? null : target, call);
                    if (result is Task task) _ = ObserveAsync(task, attr.Name);
                }
                catch (TargetInvocationException tie)
                {
                    Log.Error($"Command '{attr.Name}' threw: {tie.InnerException?.Message ?? tie.Message}");
                    Diagnostics.Report($"command:{attr.Name}", tie.InnerException ?? tie);
                }
            }, attr.Restricted);
        }
    }

    // Binds the command words to the method parameters. PURE (no natives) ->
    // deterministically testable; ServerPlayer is only wrapped as a handle, whether the
    // player is online is checked by the handler (a domain decision). false =
    // the caller replies with the generated usage line.
    internal static bool TryBind(ParameterInfo[] ps, int src, string[] args, string raw,
        out object?[] call, out string? error)
    {
        call = new object?[ps.Length];
        error = null; // currently always a usage reply; field kept for more specific messages
        int argIdx = 0;

        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            var t = p.ParameterType;

            if (t == typeof(CommandContext)) { call[i] = new CommandContext(src, raw); continue; }

            // [Rest]: the last string eats the rest of the line (spaces included).
            if (t == typeof(string) && p.GetCustomAttribute<RestAttribute>() != null)
            {
                if (argIdx < args.Length) { call[i] = string.Join(' ', args[argIdx..]); argIdx = args.Length; }
                else if (p.HasDefaultValue) call[i] = p.DefaultValue;
                else return false;
                continue;
            }

            if (argIdx >= args.Length)
            {
                if (p.HasDefaultValue) { call[i] = p.DefaultValue; continue; }
                return false;
            }

            string word = args[argIdx++];
            object? bound = t switch
            {
                _ when t == typeof(string) => word,
                _ when t == typeof(int) => int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vi) ? vi : null,
                _ when t == typeof(long) => long.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vl) ? vl : null,
                _ when t == typeof(float) => float.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var vf) ? vf : null,
                _ when t == typeof(double) => double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var vd) ? vd : null,
                _ when t == typeof(bool) => word is "true" or "1" ? true : word is "false" or "0" ? (object?)false : null,
                _ when t == typeof(ServerPlayer) => int.TryParse(word, out var netId) ? Players.Get(netId) : null,
                _ => null, // unsupported parameter type
            };
            if (bound == null) return false;
            call[i] = bound;
        }
        return true;
    }

    private static string BuildUsage(string name, ParameterInfo[] ps)
    {
        var parts = new System.Text.StringBuilder($"Usage: /{name}");
        foreach (var p in ps)
        {
            if (p.ParameterType == typeof(CommandContext)) continue;
            parts.Append(p.HasDefaultValue ? $" [{p.Name}]" : $" <{p.Name}>");
        }
        return parts.ToString();
    }

    private static async Task ObserveAsync(Task task, string name)
    {
        try { await task; }
        catch (Exception ex)
        {
            Log.Error($"Command '{name}' (async) threw: {ex.Message}");
            Diagnostics.Report($"command:{name}", ex);
        }
    }
}
