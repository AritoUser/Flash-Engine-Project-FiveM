using Flash;

namespace MyCharcreator;

// Character-creator SERVER half. It owns one thing the client can't be trusted with:
// PERSISTING the chosen appearance. flash-core owns identity (accounts) + the save-game
// (characters, incl. position); the LOOK of a character is this resource's own domain, so
// it lives in this resource's own table -- keyed by the stable character id (cid).
//
// WHY cid, not netId: netId is the session slot (1..64) and is reused for the next player
// the moment someone leaves. cid is the character's permanent id in flash-core. Key the
// appearance by cid and it follows the character across sessions and multichar slots.
//
// The client talks to us over flash-core's RPC bridge (request/response on the event bus),
// so there is no ProjectReference to flash-core -- just the public Flash.Sdk.
public sealed class Main : IResource
{
    public void OnStart()
    {
        // One tiny table. `data` is an opaque JSON blob (the client owns its shape) -- the
        // server only stores and returns it, it never interprets the appearance itself.
        // SQLite dialect (the Flash default DB); adjust for MySQL if you switch providers.
        Db.Execute(
            "CREATE TABLE IF NOT EXISTS char_appearance (cid INTEGER PRIMARY KEY, data TEXT NOT NULL)");

        // Client on join: "do we already have a look for this character?"
        //   -> returns the stored JSON string, or null for a brand-new character.
        // null is the signal the client uses to OPEN the creator; a string means
        // "returning character, apply this and spawn straight in".
        //
        // JOIN RACE (important): the client fires this the moment the session starts, but
        // flash-core loads the character asynchronously (on playerJoining). If we read getCid
        // too early it's still 0 and we'd wrongly report "new" for a returning player. So we
        // wait briefly for a real cid -- the same pattern flash-core uses in its spawn reply.
        Rpc.Register("charcreator:load", async (src, args) =>
        {
            int cid = 0;
            for (int i = 0; i < 100; i++) // up to ~10s, then give up and treat as new
            {
                cid = Exports.Call<int>("flash-core", "getCid", src);
                if (cid != 0) break;
                await Async.Delay(100);
            }
            if (cid == 0) return null;
            return Db.Scalar("SELECT data FROM char_appearance WHERE cid = @p0", cid);
        });

        // Client sends the finished look as a JSON string -> upsert it for this cid.
        // Returns true on success (the client can ignore it; save is best-effort before spawn).
        Rpc.Register("charcreator:save", (src, args) =>
        {
            int cid = Exports.Call<int>("flash-core", "getCid", src);
            string json = args.Length > 0 ? args[0]?.ToString() ?? "" : "";
            if (cid == 0 || json.Length == 0) return false;
            Db.Execute(
                "INSERT OR REPLACE INTO char_appearance (cid, data) VALUES (@p0, @p1)", cid, json);
            return true;
        });

        // Dev helper: wipe THIS player's saved look -> the next connect opens the creator
        // again (otherwise the returning-character path skips it). Console (src 0) with an
        // argument wipes a specific cid: cc_reset <cid>.
        Commands.Register("cc_reset", (src, args, raw) => { _ = ResetLookAsync(src, args); });

        Log.Info("[MyCharcreator] ready. Reminder: set `flash_spawn_adapter \"custom\"` so " +
                 "flash-core's built-in spawn does not race this creator.");
    }

    private static async System.Threading.Tasks.Task ResetLookAsync(int src, string[] args)
    {
        int cid = src == 0 && args.Length > 0 && int.TryParse(args[0]?.ToString(), out int c) ? c
                : Exports.Call<int>("flash-core", "getCid", src);
        if (cid == 0) { Log.Info("[MyCharcreator] cc_reset: no character found."); return; }
        await Db.ExecuteAsync("DELETE FROM char_appearance WHERE cid = @p0", cid);
        Log.Info($"[MyCharcreator] Look for cid={cid} wiped -- reconnect opens the creator again.");
    }

    public void OnStop() { }
}
