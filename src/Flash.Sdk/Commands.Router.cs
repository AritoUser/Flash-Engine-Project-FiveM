using System;
using System.Globalization;
using System.Linq;
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
    /// <summary>Help text shown in the chat autocomplete tooltip (#156). Alternatively
    /// put a <see cref="DescriptionAttribute"/> on the method.</summary>
    public string? Description { get; set; }
    public CommandAttribute(string name) => Name = name;
}

/// <summary>Help text for the chat autocomplete: on a command method it describes the
/// command, on a parameter it describes that argument (#156). Picked up automatically
/// by <see cref="Commands.RegisterAll"/> and pushed to the chat UI as a suggestion.</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Method)]
public sealed class DescriptionAttribute : Attribute
{
    public string Text { get; }
    public DescriptionAttribute(string text) => Text = text;
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
        // A Type argument (static/utility classes) scans that type directly; an object
        // scans its runtime type — previously typeof(X) silently registered nothing (#180).
        var (type, instance) = RouterTarget.Resolve(target);
        foreach (var m in type.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            var attr = m.GetCustomAttribute<CommandAttribute>();
            if (attr == null) continue;
            RouterTarget.RequireInvokable(m, instance, "Commands");

            var method = m;                       // capture
            var ps = m.GetParameters();
            string usage = BuildUsage(attr.Name, ps);

            // Precompute the authorization policy ONCE at registration (#131) so the
            // per-invocation path doesn't re-reflect. Includes class-level attributes (#162).
            var authPolicy = Authorization.PolicyFor(m);

            Register(attr.Name, (src, args, raw) =>
            {
                // Authorization gate: reject unauthorized callers before the handler runs
                // (and before argument binding leaks a usage line). Console (src 0) is trusted.
                if (authPolicy.HasAny && !authPolicy.Check(src, out string deny))
                {
                    new CommandContext(src, raw).Reply($"Access denied: {deny}.");
                    Log.Warn($"[SECURITY] command '{attr.Name}' denied for src={src}: {deny}.");
                    return;
                }

                if (!TryBind(ps, src, args, raw, out var call, out string? error))
                {
                    new CommandContext(src, raw).Reply(error ?? usage);
                    return;
                }
                try
                {
                    object? result = method.Invoke(method.IsStatic ? null : instance, call);
                    if (result is Task task) _ = ObserveAsync(task, attr.Name);
                }
                catch (TargetInvocationException tie)
                {
                    Log.Error($"Command '{attr.Name}' threw: {tie.InnerException?.Message ?? tie.Message}");
                    Diagnostics.Report($"command:{attr.Name}", tie.InnerException ?? tie);
                }
            }, attr.Restricted);

            RegisterSuggestion(attr, m, ps); // chat autocomplete tooltip (#156)
        }
    }

    // ---- Chat suggestions (#156) ----------------------------------------------------
    // [Command]-routed commands automatically feed the chat UI's autocomplete: name,
    // help text ([Command].Description / [Description] on the method) and one entry per
    // parameter ([Description] on the parameter, otherwise a generated hint). Pushed to
    // all connected clients at registration; the chat resource's clients announce
    // themselves with the "chat:init" NET event when their UI is ready, so late joiners
    // get the suggestions re-sent then. Removed again on resource stop.

    // Per resource: the suggestion payloads (for chat:init re-sends and removal on stop).
    private static readonly System.Collections.Generic.Dictionary<string,
        System.Collections.Generic.List<(string Name, string Help, object?[] Params)>> s_suggestions = new();
    private static readonly System.Collections.Generic.HashSet<string> s_initWired = new();

    private static void RegisterSuggestion(CommandAttribute attr, MethodInfo m, ParameterInfo[] ps)
    {
        string help = attr.Description
            ?? m.GetCustomAttribute<DescriptionAttribute>()?.Text
            ?? "";

        var paramList = new System.Collections.Generic.List<object?>();
        foreach (var p in ps)
        {
            if (p.ParameterType == typeof(CommandContext)) continue; // injected, not typed by the user
            string hint = p.GetCustomAttribute<DescriptionAttribute>()?.Text ?? ParamHint(p);
            if (p.HasDefaultValue)
                hint += p.DefaultValue is string s && s.Length > 0 ? $" (default: {s})" : " (optional)";
            paramList.Add(new System.Collections.Generic.Dictionary<string, object?>
            {
                ["name"] = p.Name ?? "arg",
                ["help"] = hint,
            });
        }
        object?[] paramArr = paramList.ToArray();

        string resource = global::Flash.Natives.Cfx.GetCurrentResourceName() ?? "";
        if (!s_suggestions.TryGetValue(resource, out var list)) s_suggestions[resource] = list = new();
        list.Add((attr.Name, help, paramArr));

        // Everyone currently connected gets it now; late joiners via chat:init below.
        Events.EmitAllClients("chat:addSuggestion", "/" + attr.Name, help, (object?)paramArr);

        if (s_initWired.Add(resource))
            Events.On("chat:init", _ =>
            {
                int src = Events.SourceNetId;
                if (src <= 0) return;
                if (!s_suggestions.TryGetValue(resource, out var mine)) return;
                foreach (var (name, h, pars) in mine)
                    Events.EmitClient(src, "chat:addSuggestion", "/" + name, h, (object?)pars);
            });
    }

    // Generated argument hint when no [Description] is given.
    private static string ParamHint(ParameterInfo p)
    {
        var t = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
        return
            t == typeof(ServerPlayer) ? "player (netId or account id)" :
            t == typeof(int) || t == typeof(long) ? "number" :
            t == typeof(float) || t == typeof(double) ? "decimal number" :
            t == typeof(bool) ? "true/false" :
            "text";
    }

    /// <summary>On resource stop: retract this resource's chat suggestions (the commands
    /// themselves die with the funcref registry). Called by the host.</summary>
    internal static void ClearResource(string resource)
    {
        s_initWired.Remove(resource);
        if (!s_suggestions.Remove(resource, out var list)) return;
        foreach (var (name, _, _) in list)
        {
            try { Events.EmitAllClients("chat:removeSuggestion", "/" + name); } catch { }
        }
    }

    // Binds the command words to the method parameters. Deterministically testable
    // outside the server: the only native-touching arm (ServerPlayer resolution) falls
    // back to a plain handle wrap when natives are unavailable. false =
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
            // A nullable primitive param (int?/bool?/… for optional args) has ParameterType
            // Nullable<T>; match on the UNDERLYING type so it binds like its non-nullable form
            // instead of falling through to "unsupported" and making the command unusable (#151).
            Type u = Nullable.GetUnderlyingType(t) ?? t;
            object? bound = u switch
            {
                _ when u == typeof(string) => word,
                _ when u == typeof(int) => int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vi) ? vi : null,
                _ when u == typeof(long) => long.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vl) ? vl : null,
                _ when u == typeof(float) => float.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var vf) ? vf : null,
                _ when u == typeof(double) => double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var vd) ? vd : null,
                _ when u == typeof(bool) => word is "true" or "1" ? true : word is "false" or "0" ? (object?)false : null,
                _ when u == typeof(ServerPlayer) => int.TryParse(word, out var netId) ? ResolvePlayer(netId) : null,
                _ => null, // unsupported parameter type
            };
            if (bound == null) return false;
            call[i] = bound;
        }
        return true;
    }

    // Resolves a numeric command argument to a player: a CONNECTED netId wins, otherwise
    // the persistent AccountId (fixed user ID, #174) is matched against connected players
    // — so "/givemoney 15 100" works with the stable ID players actually see. Outside a
    // server (standalone tests) the native calls throw -> fall back to the plain handle
    // wrap, keeping TryBind deterministic.
    private static object? ResolvePlayer(int id)
    {
        try
        {
            var byNet = Players.Get(id);
            if (byNet.Connected) return byNet;
            return Players.GetByAccountId(id); // null -> usage reply
        }
        catch
        {
            return Players.Get(id);
        }
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
