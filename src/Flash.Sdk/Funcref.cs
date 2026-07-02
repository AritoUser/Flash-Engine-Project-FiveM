namespace Flash;

/// <summary>
/// A function reference RECEIVED from the core (e.g. in event arguments or in the
/// deferrals object). Holds the canonical ref string and invokes the function through
/// the host when needed (host->InvokeFunctionReference). Counterpart to Flash.Funcrefs
/// (which registers LOCAL callbacks for the core).
///
/// In the msgpack stream a funcref arrives as ext type 10/11; the codec turns it into
/// this object (ref string = UTF-8 of the ext payload).
/// </summary>
public sealed class Funcref
{
    private readonly string _refId;

    internal Funcref(string refId) => _refId = refId;

    /// <summary>Invokes the referenced function with the given arguments.
    /// (The return value is currently not passed back.)</summary>
    public void Invoke(params object?[] args)
    {
        byte[] payload = Msgpack.EncodeArray(args ?? System.Array.Empty<object?>());
        Native.InvokeFunctionRef(_refId, payload);
    }
}
