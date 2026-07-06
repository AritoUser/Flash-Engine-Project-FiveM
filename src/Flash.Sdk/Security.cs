using System;
using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

namespace Flash;

// =====================================================================================
//  Server-side input sanitization for user-generated content (#65).
//
//  User strings (character names, chat/twitter messages, transfer reasons, plates) are
//  replicated via events/state bags and eventually rendered in an NUI (Chromium). If a
//  name is `<img src=x onerror=...>` and an admin opens a player-list UI, the payload runs
//  in the admin's NUI context -> XSS -> privilege escalation. The ONLY un-bypassable place
//  to stop this is the server, BEFORE the value is stored or replicated.
//
//  This module is deliberately server-side and rendering-agnostic:
//    - Validate at the boundary: Args.StrRegex(...) rejects anything not matching an
//      allow-list pattern / length (reject > sanitize where you can).
//    - Encode on the way out: Security.HtmlEncode(...) turns `<script>` into inert text
//      for the cases where arbitrary text must be allowed (chat).
//  NUI authors still must treat replicated data as untrusted -- the engine can guarantee
//  input validation, not output encoding in a UI it does not own.
// =====================================================================================

/// <summary>
/// Server-side sanitization helpers for user-generated content that ends up in an NUI
/// (anti-XSS). Validate inputs with an allow-list (<see cref="IsMatch"/> /
/// <see cref="Flash.Args.StrRegex"/>) and/or neutralize markup with <see cref="HtmlEncode"/>
/// before storing or replicating it. See also #65.
/// </summary>
public static class Security
{
    // Compiled patterns are cached: validation runs on every inbound string, and recompiling
    // a Regex per call is wasteful. A match timeout guards against catastrophic-backtracking
    // (ReDoS) from a badly written server-side pattern.
    private static readonly ConcurrentDictionary<string, Regex> s_patterns = new();
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    // Fast heuristic for the characters that begin the common injection vectors. Not a
    // validator (allow-list validation is the real defense) -- a cheap "does this even need
    // a closer look" gate for HasMarkup and callers that only accept plain text.
    private static readonly char[] MarkupChars = { '<', '>', '&', '"', '\'', '`' };

    /// <summary>
    /// HTML-encodes <paramref name="value"/> so any markup renders as inert text
    /// (<c>&lt;script&gt;</c> becomes visible text, not an executing tag). Use when a value
    /// must allow arbitrary characters (e.g. chat) but will be shown in an NUI. Null → "".
    /// </summary>
    public static string HtmlEncode(string? value)
        => value is null ? "" : WebUtility.HtmlEncode(value);

    /// <summary>
    /// True if <paramref name="value"/> contains any character used to open an HTML/JS
    /// injection vector (&lt; &gt; &amp; " ' `). A quick screen — prefer an allow-list
    /// (<see cref="IsMatch"/>) when you know the legal shape of the input.
    /// </summary>
    public static bool HasMarkup(string? value)
        => value is not null && value.IndexOfAny(MarkupChars) >= 0;

    /// <summary>
    /// True if <paramref name="value"/> fully matches <paramref name="pattern"/> (anchored;
    /// the pattern must describe the ENTIRE string). Patterns are cached and evaluated with a
    /// short match timeout (ReDoS guard); a timeout is treated as "no match" (reject). A null
    /// value never matches.
    /// </summary>
    public static bool IsMatch(string? value, string pattern)
    {
        if (value is null) return false;
        var regex = s_patterns.GetOrAdd(pattern,
            p => new Regex(p, RegexOptions.CultureInvariant, MatchTimeout));
        try { var m = regex.Match(value); return m.Success && m.Index == 0 && m.Length == value.Length; }
        catch (RegexMatchTimeoutException) { return false; } // pathological input -> reject
    }

    // =================================================================================
    //  Anti-cheat exception / bypass registry (#54, #55).
    //
    //  A rigid anti-cheat causes false positives when a module LEGITIMATELY does the thing
    //  it guards against — a paintball script hands out weapons, a dealership spawns cars.
    //  These helpers let any resource grant a SCOPED, TIME-LIMITED exception that the
    //  server-side enforcement (flash-core AntiCheat) consults before it acts. Pure
    //  time+dictionary state — no natives — so the decision is deterministic and testable.
    //  Keyed by netId; entries expire on their own and are dropped on player disconnect.
    // =================================================================================

    // netId -> UTC ticks until which entity-limit checks are bypassed.
    private static readonly ConcurrentDictionary<int, long> s_entityBypass = new();
    // netId -> (weapon JOAAT hash) -> UTC ticks until allowed. Keyed on the HASH, not the
    // name, because the server's weaponDamageEvent delivers the weapon as a JOAAT hash --
    // AddWeaponException hashes the name the same way so both sides match. (#194)
    private static readonly ConcurrentDictionary<int, ConcurrentDictionary<uint, long>> s_weaponExc = new();

    /// <summary>The GTA weapon hash (Jenkins one-at-a-time over the lower-cased name) — the
    /// same value GET_HASH_KEY / weaponDamageEvent's weaponType produces, computed without a
    /// native so it works anywhere. (#194)</summary>
    public static uint WeaponHash(string name)
    {
        uint h = 0;
        foreach (char c in name)
        {
            h += (uint)char.ToLowerInvariant(c);
            h += h << 10;
            h ^= h >> 6;
        }
        h += h << 3;
        h ^= h >> 11;
        h += h << 15;
        return h;
    }

    /// <summary>Lets <paramref name="netId"/> spawn entities without tripping the
    /// mass-spawn limit for the next <paramref name="seconds"/> (e.g. a shop spawning a
    /// fleet). (#54)</summary>
    public static void BypassEntityLimits(int netId, int seconds)
        => s_entityBypass[netId] = DateTime.UtcNow.AddSeconds(Math.Max(0, seconds)).Ticks;

    /// <summary>True while <paramref name="netId"/> has an active entity-limit bypass.</summary>
    public static bool IsEntityLimitBypassed(int netId)
        => s_entityBypass.TryGetValue(netId, out long until) && until >= DateTime.UtcNow.Ticks;

    /// <summary>Allows <paramref name="netId"/> to use <paramref name="weapon"/> without the
    /// weapon-integrity check flagging it, for the next <paramref name="minutes"/>
    /// (e.g. paintball). Weapon name is matched case-insensitively. (#54)</summary>
    public static void AddWeaponException(int netId, string weapon, int minutes)
    {
        var map = s_weaponExc.GetOrAdd(netId, _ => new());
        map[WeaponHash(weapon)] = DateTime.UtcNow.AddMinutes(Math.Max(0, minutes)).Ticks;
    }

    /// <summary>True if <paramref name="netId"/> currently has an exception for
    /// <paramref name="weapon"/> (matched by weapon hash, case-insensitive).</summary>
    public static bool IsWeaponAllowed(int netId, string weapon)
        => IsWeaponAllowed(netId, WeaponHash(weapon));

    /// <summary>True if <paramref name="netId"/> has an exception for the weapon with this
    /// JOAAT hash (e.g. weaponDamageEvent's weaponType). (#194)</summary>
    public static bool IsWeaponAllowed(int netId, uint weaponHash)
        => s_weaponExc.TryGetValue(netId, out var map)
           && map.TryGetValue(weaponHash, out long until) && until >= DateTime.UtcNow.Ticks;

    /// <summary>Drops all exceptions of a player (called by the enforcement layer on
    /// disconnect so a recycled netId can't inherit them).</summary>
    public static void ClearPlayerExceptions(int netId)
    {
        s_entityBypass.TryRemove(netId, out _);
        s_weaponExc.TryRemove(netId, out _);
    }
}
