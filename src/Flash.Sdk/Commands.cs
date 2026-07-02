using System;
using System.Runtime.InteropServices;

namespace Flash;

/// <summary>
/// Chat/console commands in C#. Register(name, handler) registers a command with the
/// FiveM core (native REGISTER_COMMAND) — the handler is a C# callback the core calls
/// back through the funcref system (Flash.Funcrefs + IScriptRefRuntime.CallRef).
/// </summary>
public static class Commands
{
    /// <summary>
    /// Registers a server command. handler(source, args, rawCommand):
    /// source = the player's NetID (0 = server console), args = the arguments,
    /// rawCommand = the full typed line. restricted = true → only for authorized
    /// players (ace permissions).
    /// </summary>
    public static unsafe void Register(string name, Action<int, string[], string> handler, bool restricted = false)
    {
        // Register the handler as a funcref. On invocation the core delivers [source, args, raw].
        int refIdx = Funcrefs.Register(callArgs =>
        {
            int source = callArgs.Length > 0 && callArgs[0] != null ? Convert.ToInt32(callArgs[0]) : 0;
            string[] args = (callArgs.Length > 1 && callArgs[1] is object?[] arr)
                ? Array.ConvertAll(arr, o => o?.ToString() ?? "")
                : Array.Empty<string>();
            string raw = callArgs.Length > 2 ? callArgs[2]?.ToString() ?? "" : "";
            handler(source, args, raw);
            return null; // commands do not return a value
        });

        // Fetch the canonical ref string for the funcref and pass it to REGISTER_COMMAND
        // as the func argument (natives take a funcref as a char* ref string).
        string refString = Native.Canonicalize(refIdx);
        nint namePtr = Marshal.StringToCoTaskMemUTF8(name);
        nint refPtr = Marshal.StringToCoTaskMemUTF8(refString);
        try
        {
            Span<nuint> a = stackalloc nuint[3];
            a[0] = (nuint)namePtr;
            a[1] = (nuint)refPtr;
            a[2] = restricted ? 1u : 0u;
            Native.Invoke(0x5FA79B0FUL, a); // REGISTER_COMMAND(name, handler, restricted)
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePtr);
            Marshal.FreeCoTaskMem(refPtr);
        }
    }
}
