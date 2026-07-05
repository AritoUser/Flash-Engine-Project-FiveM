using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Flash;

// =====================================================================================
//  Declarative authorization (#131).
//
//  Instead of hand-writing `if (player.Job != "police" || grade < 2) return;` in every
//  handler (easy to forget -> an unguarded endpoint is an exploit), a handler declares its
//  requirement with an attribute and the dispatcher enforces it BEFORE the body runs.
//
//  Layering: the SDK is framework-agnostic — it does NOT know what a "faction" or an "admin
//  permission" is; that data lives in the gameplay framework (flash-core factions/grades)
//  and flash-admin (ACL). So the SDK owns the attributes + the enforcement point and delegates
//  the actual lookup to PLUGGABLE resolvers that flash-core/flash-admin register at startup.
//  Fail-closed: an attribute with no registered resolver denies (a missing resolver must not
//  silently open a guarded endpoint).
//
//  Enforcement today runs in the command router (#12). The same Authorization.IsAuthorized
//  check is public so attribute-based event routing (#111) — and hand-written handlers — can
//  reuse it.
// =====================================================================================

/// <summary>
/// Restricts a handler to members of a faction/job (optionally a minimum grade and on-duty).
/// Multiple attributes on one method are OR-combined (e.g. police OR ems). Enforced by the
/// command router; the actual membership lookup is provided by
/// <see cref="Authorization.FactionResolver"/>. (#131)
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class AuthorizeFactionAttribute : Attribute
{
    /// <summary>The required faction/job name (e.g. "police").</summary>
    public string FactionName { get; }
    /// <summary>Minimum grade/rank within the faction; 0 = any member.</summary>
    public int MinGrade { get; set; }
    /// <summary>Require the player to be on duty (framework-defined). Default true.</summary>
    public bool MustBeOnDuty { get; set; } = true;

    public AuthorizeFactionAttribute(string factionName) => FactionName = factionName;
}

/// <summary>
/// Restricts a handler to admins, optionally requiring a specific permission (e.g. "ban",
/// "teleport"). Enforced by the command router; the lookup is provided by
/// <see cref="Authorization.AdminResolver"/>. (#131)
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class AuthorizeAdminAttribute : Attribute
{
    /// <summary>The permission the caller must hold; null/empty = "is an admin at all".</summary>
    public string? RequiredPermission { get; set; }
}

/// <summary>
/// Central authorization gateway for <see cref="AuthorizeFactionAttribute"/> /
/// <see cref="AuthorizeAdminAttribute"/>. The gameplay framework registers the resolvers at
/// startup; the SDK evaluates them at the dispatch boundary. (#131)
/// </summary>
public static class Authorization
{
    /// <summary>
    /// Resolves whether a player satisfies a faction requirement:
    /// <c>(source netId, factionName, minGrade, mustBeOnDuty) =&gt; allowed</c>.
    /// Registered by the gameplay framework (e.g. flash-core). When null, any
    /// <see cref="AuthorizeFactionAttribute"/> denies (fail-closed).
    /// </summary>
    public static Func<int, string, int, bool, bool>? FactionResolver { get; set; }

    /// <summary>
    /// Resolves whether a player is an authorized admin:
    /// <c>(source netId, requiredPermission-or-null) =&gt; allowed</c>. Registered by
    /// flash-admin. When null, any <see cref="AuthorizeAdminAttribute"/> denies (fail-closed).
    /// </summary>
    public static Func<int, string?, bool>? AdminResolver { get; set; }

    /// <summary>
    /// Evaluates the authorization attributes on <paramref name="method"/> AND its declaring
    /// type for the caller <paramref name="source"/> (netId; ≤ 0 = server console / server-
    /// internal, trusted). Returns true if allowed; otherwise false with a human-readable
    /// <paramref name="reason"/> suitable for replying to the caller / logging.
    /// </summary>
    public static bool IsAuthorized(MemberInfo method, int source, out string reason)
        => PolicyFor(method).Check(source, out reason);

    /// <summary>
    /// Precomputes the authorization policy of a method, combining its own attributes with the
    /// attributes on its declaring type. The routers call this ONCE at registration so the
    /// per-dispatch path never re-reflects. Class-level attributes secure every member of the
    /// type (an <c>[AuthorizeAdmin]</c> on the class must NOT be silently ignored — #162).
    /// </summary>
    internal static AuthPolicy PolicyFor(MemberInfo method)
    {
        var declaring = (method as MethodInfo)?.DeclaringType;
        return new AuthPolicy(
            method.GetCustomAttributes<AuthorizeFactionAttribute>().ToArray(),
            method.GetCustomAttribute<AuthorizeAdminAttribute>(),
            declaring?.GetCustomAttributes<AuthorizeFactionAttribute>().ToArray() ?? Array.Empty<AuthorizeFactionAttribute>(),
            declaring?.GetCustomAttribute<AuthorizeAdminAttribute>());
    }

    // OR within one faction group (multiple [AuthorizeFaction] at the same level = any-of);
    // fail-closed when no resolver is registered. Empty group -> pass.
    internal static bool CheckFactionGroup(IReadOnlyList<AuthorizeFactionAttribute> factions, int source, out string reason)
    {
        reason = "";
        if (factions.Count == 0) return true;
        if (FactionResolver == null) { reason = "authorization unavailable (no faction resolver registered)"; return false; }
        foreach (var f in factions)
            if (FactionResolver(source, f.FactionName, f.MinGrade, f.MustBeOnDuty)) return true;
        reason = "requires faction: " + string.Join(" or ", factions.Select(FormatFaction));
        return false;
    }

    internal static bool CheckAdmin(AuthorizeAdminAttribute? admin, int source, out string reason)
    {
        reason = "";
        if (admin == null) return true;
        if (AdminResolver == null) { reason = "authorization unavailable (no admin resolver registered)"; return false; }
        if (AdminResolver(source, admin.RequiredPermission)) return true;
        reason = string.IsNullOrEmpty(admin.RequiredPermission)
            ? "requires admin"
            : $"requires admin permission '{admin.RequiredPermission}'";
        return false;
    }

    private static string FormatFaction(AuthorizeFactionAttribute f)
        => f.MinGrade > 0 ? $"{f.FactionName} (grade >= {f.MinGrade})" : f.FactionName;
}

/// <summary>
/// Precomputed authorization requirement of a handler: the method-level and the class-level
/// attribute groups. Each non-empty group must authorize (AND across the four groups; OR within
/// one faction group). Server-originated callers (source ≤ 0: console / server-internal emit)
/// are trusted. (#131, #162)
/// </summary>
internal sealed class AuthPolicy
{
    private readonly AuthorizeFactionAttribute[] _methodFactions;
    private readonly AuthorizeAdminAttribute? _methodAdmin;
    private readonly AuthorizeFactionAttribute[] _classFactions;
    private readonly AuthorizeAdminAttribute? _classAdmin;

    public AuthPolicy(AuthorizeFactionAttribute[] methodFactions, AuthorizeAdminAttribute? methodAdmin,
        AuthorizeFactionAttribute[] classFactions, AuthorizeAdminAttribute? classAdmin)
    {
        _methodFactions = methodFactions;
        _methodAdmin = methodAdmin;
        _classFactions = classFactions;
        _classAdmin = classAdmin;
    }

    /// <summary>True if any authorization attribute is declared at method OR class level.</summary>
    public bool HasAny => _methodFactions.Length > 0 || _methodAdmin != null
                       || _classFactions.Length > 0 || _classAdmin != null;

    /// <summary>Allowed if every declared group authorizes; server-originated callers (≤ 0) trusted.</summary>
    public bool Check(int source, out string reason)
    {
        reason = "";
        if (!HasAny) return true;
        if (source <= 0) return true; // server console / server-internal emit -> trusted
        // AND across the groups (short-circuits on the first denial and keeps its reason).
        return Authorization.CheckFactionGroup(_methodFactions, source, out reason)
            && Authorization.CheckFactionGroup(_classFactions, source, out reason)
            && Authorization.CheckAdmin(_methodAdmin, source, out reason)
            && Authorization.CheckAdmin(_classAdmin, source, out reason);
    }
}
