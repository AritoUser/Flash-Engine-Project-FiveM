namespace Flash;

/// <summary>
/// Logging for resources — writes to the server console with a level + resource
/// prefix. A cleaner, uniform alternative to Console.WriteLine. The resource name is
/// determined automatically (correct because the host has set the active runtime).
/// </summary>
public static class Log
{
    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Debug(string message) => Write("DEBUG", message);

    private static void Write(string level, string message)
    {
        string resource = global::Flash.Natives.Cfx.GetCurrentResourceName() ?? "?";
        System.Console.WriteLine($"[{resource}] [{level}] {message}");
    }
}
