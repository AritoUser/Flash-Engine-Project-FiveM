using System;
using System.Threading;

namespace Flash;

// =====================================================================================
//  Script-thread guard (shared).
//
//  FiveM server natives crash the whole PROCESS (not a catchable exception) when called
//  off the script thread. Public APIs that touch natives therefore fail fast with a clear
//  managed exception when used off-thread, instead of hard-crashing the server later --
//  the same decision already made for Exports (#178), Rpc (#169) and the DB layer (#89).
//  Centralised here so every native-touching surface uses the identical check.
// =====================================================================================
internal static class ThreadGuard
{
    /// <summary>Throws if not on a resource's script thread (no FlashSyncContext). Call at
    /// the top of any public API that invokes server natives.</summary>
    public static void AssertScriptThread(string api)
    {
        if (SynchronizationContext.Current is not FlashSyncContext)
            throw new InvalidOperationException(
                $"{api} must run on the script thread (inside a resource handler/tick/OnStart). " +
                "It reads server natives; dispatch background work back to the script thread first " +
                "(e.g. via an event, Async.NextFrame, or by awaiting on the script thread).");
    }
}
