namespace Flash;

/// <summary>
/// Logging for resources — writes to the server console with a level + resource
/// prefix. A cleaner, uniform alternative to Console.WriteLine. The resource name is
/// determined automatically (correct because the host has set the active runtime).
/// Levels are color-coded via FiveM's inline console colors (#38): warnings yellow,
/// errors red, debug cyan — so problems stop drowning in the startup wall of text.
/// </summary>
public static class Log
{
    public static void Info(string message) => Write("INFO", "", message);
    public static void Warn(string message) => Write("WARN", "^3", message);   // yellow
    public static void Error(string message) => Write("ERROR", "^1", message); // red
    public static void Debug(string message) => Write("DEBUG", "^5", message); // cyan

    private static void Write(string level, string color, string message)
    {
        string resource = global::Flash.Natives.Cfx.GetCurrentResourceName() ?? "?";
        // ^7 = reset to default, otherwise the color would bleed into everything after.
        System.Console.WriteLine($"[{resource}] {color}[{level}]{(color.Length > 0 ? "^7" : "")} {message}");
    }
}