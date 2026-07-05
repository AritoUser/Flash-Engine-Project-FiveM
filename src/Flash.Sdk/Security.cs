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
}
