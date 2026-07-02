using Flash;

namespace MyResource;

// Your own server-side C# resource: simply a class implementing Flash.IResource.
// The host finds it automatically (through the interface -- class/namespace names are
// up to you), creates an instance and calls OnStart()/OnStop().
// Optionally also implement Flash.ITickable for per-server-frame logic (OnTick).
public sealed class Main : IResource, ITickable
{
    public void OnStart()
    {
        System.Console.WriteLine("[MyResource] started");

        // Example 1: react to a player connecting (event fired by the core).
        Events.On("playerConnecting", args =>
        {
            string name = args.Length > 0 ? args[0]?.ToString() ?? "?" : "?";
            int online = Players.Count;
            System.Console.WriteLine($"[MyResource] {name} is connecting ({online} online)");
        });

        // Example 2: receive an event from a client and respond (server<->client).
        Events.On("myresource:hello", args =>
        {
            var player = Players.Get(Events.SourceNetId);
            System.Console.WriteLine($"[MyResource] hello from {player.Name}");
            player.Emit("myresource:welcome", $"Hi {player.Name}!");
        });
    }

    public void OnTick()
    {
        // Runs every server frame. Leave empty if you don't need per-frame logic.
        // (Only implement ITickable if you really need it.)
    }

    public void OnStop()
    {
        // On resource stop: clean up here (release references etc.).
        System.Console.WriteLine("[MyResource] stopped");
    }
}
