using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace Flash;

// =====================================================================================
//  HTTP API -- server-side HTTP requests (webhooks, Discord, external APIs, auth).
//
//  WHY/HOW:
//    FiveM provides PERFORM_HTTP_REQUEST_INTERNAL (0x8e8cc653, JSON variant): it takes a
//    JSON request {url, method, data, headers, followLocation}, immediately returns a
//    token (int) and later delivers the response through the event
//    "__cfx_internal:httpResponse" (token, status, body, headers, errorData). Together
//    with the async scheduler we build a modern Task API on top: `await Http.Get(url)`
//    resumes on the script thread after the response.
//
//  DECISION:
//    One TaskCompletionSource per request, stored under the token. The response handler
//    is registered LAZILY once per resource. RunContinuationsAsynchronously -> the await
//    continuation runs in the caller's (captured) context, not inline.
// =====================================================================================

/// <summary>Response of an HTTP request.</summary>
public sealed class HttpResponse
{
    /// <summary>HTTP status code (e.g. 200). 0 on local failure.</summary>
    public int Status { get; }
    /// <summary>Response body (on failure possibly the error message).</summary>
    public string Body { get; }
    /// <summary>Response headers.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }
    /// <summary>Error text if the request failed; otherwise null.</summary>
    public string? Error { get; }
    /// <summary>true for a 2xx status.</summary>
    public bool Ok => Status >= 200 && Status < 300;

    internal HttpResponse(int status, string body, IReadOnlyDictionary<string, string> headers, string? error)
    {
        Status = status; Body = body; Headers = headers; Error = error;
    }

    /// <summary>Deserializes the body as JSON into <typeparamref name="T"/> (or default).</summary>
    public T? Json<T>() => string.IsNullOrEmpty(Body) ? default : JsonSerializer.Deserialize<T>(Body);
}

/// <summary>Server-side HTTP requests. <c>await Http.Get(url)</c> resumes on the server
/// script thread after the response (non-blocking).</summary>
public static class Http
{
    private const ulong PERFORM_HTTP_REQUEST_INTERNAL = 0x8e8cc653UL; // JSON variant

    /// <summary>Default timeout per request (ms). If the core's response never arrives
    /// (e.g. lost event), the await would otherwise hang until resource stop.</summary>
    public const int DefaultTimeoutMs = 30_000;

    // Open requests: token -> (completion, resource). Single-threaded (script thread) -> no lock.
    private static readonly Dictionary<int, (TaskCompletionSource<HttpResponse> Tcs, string Res)> s_pending = new();
    // Resources whose response handler is already registered.
    private static readonly HashSet<string> s_registered = new();
    private static readonly IReadOnlyDictionary<string, string> s_emptyHeaders =
        new Dictionary<string, string>();

    /// <summary>GET request.</summary>
    public static Task<HttpResponse> Get(string url, IDictionary<string, string>? headers = null)
        => Request(url, "GET", "", headers);

    /// <summary>POST request with a body.</summary>
    public static Task<HttpResponse> Post(string url, string body, IDictionary<string, string>? headers = null)
        => Request(url, "POST", body, headers);

    /// <summary>General request (method/body/headers free). <paramref name="timeoutMs"/>
    /// &lt;= 0 disables the timeout (wait for the response without a fallback).</summary>
    public static Task<HttpResponse> Request(string url, string method = "GET", string data = "",
        IDictionary<string, string>? headers = null, int timeoutMs = DefaultTimeoutMs)
    {
        string res = global::Flash.Natives.Cfx.GetCurrentResourceName() ?? "";
        EnsureResponseHandler(res);

        // Request as JSON (the 0x8e8cc653 variant parses JSON). Property names exactly as
        // the core expects: url/method/data/headers/followLocation.
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            url,
            method,
            data = data ?? "",
            headers = headers ?? new Dictionary<string, string>(),
            followLocation = true,
        });

        var tcs = new TaskCompletionSource<HttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        int token = Invoke(json);
        if (token == -1)
        {
            tcs.SetResult(new HttpResponse(0, "", s_emptyHeaders, "The request could not be started."));
            return tcs.Task;
        }
        s_pending[token] = (tcs, res);

        // Timeout fallback via the resource scheduler (script thread): if the response
        // hasn't arrived by then, resolve the request with status 0 + error text. If the
        // response is already in, the token is no longer pending -> no-op.
        if (timeoutMs > 0 && Scheduler.Get(res) is { } ctx)
        {
            int t = token;
            ctx.ScheduleTimer(Environment.TickCount64 + timeoutMs, () =>
            {
                if (s_pending.Remove(t))
                    tcs.TrySetResult(new HttpResponse(0, "", s_emptyHeaders, $"Timed out after {timeoutMs} ms."));
            });
        }
        return tcs.Task;
    }

    // Invoke the native raw: arg0 = pointer to the JSON bytes, arg1 = length. Returns the token.
    private static unsafe int Invoke(byte[] json)
    {
        fixed (byte* p = json)
        {
            Span<nuint> a = stackalloc nuint[2];
            a[0] = (nuint)p;
            a[1] = (nuint)json.Length;
            return unchecked((int)global::Flash.Native.Invoke(PERFORM_HTTP_REQUEST_INTERNAL, a));
        }
    }

    // Register the response handler once per resource (otherwise the core won't deliver
    // the event). The handler routes responses to open requests by token.
    private static void EnsureResponseHandler(string res)
    {
        if (!s_registered.Add(res)) return;
        Events.On("__cfx_internal:httpResponse", OnHttpResponse);
    }

    private static void OnHttpResponse(object?[] args)
    {
        if (args.Length < 2) return;
        int token = Convert.ToInt32(args[0]);
        if (!s_pending.TryGetValue(token, out var entry)) return; // not our request
        s_pending.Remove(token);

        int status = Convert.ToInt32(args[1]);
        string? body = args.Length > 2 ? args[2] as string : null;
        var rawHeaders = (args.Length > 3 ? args[3] : null) as IDictionary<string, object?>;
        string? error = args.Length > 4 ? args[4] as string : null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (rawHeaders != null)
        {
            foreach (var kv in rawHeaders)
                headers[kv.Key] = kv.Value switch
                {
                    string s => s,
                    object?[] arr => string.Join(", ", arr),
                    _ => kv.Value?.ToString() ?? "",
                };
        }

        // body==null => failure: use errorData as body + set Error.
        entry.Tcs.TrySetResult(new HttpResponse(status, body ?? error ?? "", headers, body == null ? error : null));
    }

    /// <summary>On resource stop: discard the registration + open requests of this resource
    /// (their await continuations would otherwise pin the collectible ALC).</summary>
    internal static void ClearResource(string resource)
    {
        s_registered.Remove(resource);

        // Remove open tokens of this resource (do NOT complete them -> task + continuation
        // become garbage and release the ALC reference).
        List<int>? drop = null;
        foreach (var kv in s_pending)
            if (kv.Value.Res == resource) (drop ??= new List<int>()).Add(kv.Key);
        if (drop != null)
            foreach (var t in drop) s_pending.Remove(t);
    }
}
