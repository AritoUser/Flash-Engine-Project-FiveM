using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Flash;

// =====================================================================================
//  Standard HttpClient integration (#116).
//
//  Resource authors often want to use the standard System.Net.Http.HttpClient (and the
//  libraries built on it: Refit, Discord.Net, Octokit, System.Net.Http.Json ...). Using a
//  plain HttpClient inside a resource is a trap: its request runs on a thread-pool socket
//  and the await continuation resumes on a THREAD-POOL thread -> the first native call
//  after the await (Players.Get, Entity.Position, ...) crashes the server, because Cfx
//  natives are script-thread only.
//
//  This handler routes every HttpClient request through Flash.Http (the Cfx-native HTTP
//  client), whose Task already resumes on the resource's script thread via FlashSyncContext.
//  So `await httpClient.GetFromJsonAsync<T>(...)` lands the caller back on the script thread,
//  and it reuses FXServer's libcurl pool instead of opening extra OS sockets.
//
//  THREADING (critical): the native resource-name lookup inside Flash.Request must run on
//  the script thread. HttpClient.SendAsync invokes this handler inline on the caller's
//  context (the script thread when called from a handler/OnStart), and we deliberately do
//  NOT ConfigureAwait(false) before calling Flash.Http -> we stay on / resume to that
//  context for the native call.
// =====================================================================================

/// <summary>
/// An <see cref="HttpMessageHandler"/> that routes <see cref="HttpClient"/> requests through
/// the Cfx-native <see cref="Http"/> client, so continuations resume on the server script
/// thread (safe to touch natives after the await) and requests reuse FXServer's socket pool.
/// Lets resources use standard <c>HttpClient</c> and libraries built on it (Refit,
/// Discord.Net, <c>System.Net.Http.Json</c>). Construct with
/// <c>new HttpClient(new FlashHttpMessageHandler())</c>. (#116)
/// </summary>
public sealed class FlashHttpMessageHandler : HttpMessageHandler
{
    private readonly int _timeoutMs;

    // The resource's script-thread context, captured at construction. Used to marshal the
    // native request onto the script thread when SendAsync is invoked from a background/
    // thread-pool thread (Task.Run, or libraries like Discord.Net/Octokit) -> otherwise the
    // Cfx natives inside Flash.Http would run off-thread and hard-crash FXServer (#164).
    // CONSTRUCT THE HANDLER ON THE SCRIPT THREAD (e.g. in OnStart) so this is captured.
    private readonly FlashSyncContext? _ctx;

    /// <summary>Creates the handler. Construct it on the script thread (e.g. in <c>OnStart</c>)
    /// so it can be used safely from background threads. <paramref name="timeoutMs"/> is the
    /// per-request timeout passed to <see cref="Http.Request"/> (&lt;= 0 disables it — note
    /// <see cref="HttpClient.Timeout"/> still applies on top). Defaults to
    /// <see cref="Http.DefaultTimeoutMs"/>.</summary>
    public FlashHttpMessageHandler(int timeoutMs = Http.DefaultTimeoutMs)
    {
        _timeoutMs = timeoutMs;
        _ctx = SynchronizationContext.Current as FlashSyncContext;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.RequestUri is null)
            throw new InvalidOperationException("FlashHttpMessageHandler: request has no RequestUri.");

        // Collect request + content headers into the flat map Flash.Http expects. Do this
        // BEFORE any await so we don't need to be on a specific thread for it.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers)
            headers[h.Key] = string.Join(", ", h.Value);

        // Read the body. No ConfigureAwait(false): StringContent completes inline, and if a
        // content type must await, we must resume on the caller's (script-thread) context so
        // the Flash.Http call below runs on-thread.
        string body = "";
        if (request.Content is not null)
        {
            foreach (var h in request.Content.Headers)
                headers[h.Key] = string.Join(", ", h.Value);
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        // The actual request via the native HTTP client, guaranteed to execute on the script
        // thread (Flash.Http calls Cfx natives). Its Task resumes there too. (#164)
        HttpResponse resp = await SendOnScriptThread(
            request.RequestUri.ToString(), request.Method.Method, body, headers);

        // Status 0 == local/transport failure (couldn't start, timed out). Mirror a real
        // HttpClientHandler, which surfaces transport errors as HttpRequestException.
        if (resp.Status == 0)
            throw new HttpRequestException(resp.Error ?? "Flash HTTP request failed.");

        var message = new HttpResponseMessage((HttpStatusCode)resp.Status) { RequestMessage = request };
        var content = new StringContent(resp.Body ?? "");
        foreach (var kv in resp.Headers)
        {
            // Content-Length is recomputed by StringContent; a stale value would corrupt reads.
            if (string.Equals(kv.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            // Content-Type replaces the StringContent default (text/plain) so JSON etc. parses.
            if (string.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                try { content.Headers.ContentType = MediaTypeHeaderValue.Parse(kv.Value); } catch { /* keep default */ }
                continue;
            }
            // Response vs content header: try the message first, fall back to the content.
            if (!message.Headers.TryAddWithoutValidation(kv.Key, kv.Value))
                content.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }
        message.Content = content;
        return message;
    }

    // Runs Flash.Http (which calls Cfx natives) on the script thread regardless of which
    // thread SendAsync was invoked on. (#164)
    private Task<HttpResponse> SendOnScriptThread(string url, string method, string body,
        Dictionary<string, string> headers)
    {
        // Already on a script thread (the common case: awaited directly inside a handler/OnStart).
        if (SynchronizationContext.Current is FlashSyncContext)
            return Http.Request(url, method, body, headers, _timeoutMs);

        // Off-thread (Task.Run / background library). Marshal onto the captured resource context;
        // the posted work runs on the next frame's drain (script thread) and completes the TCS.
        if (_ctx != null)
        {
            var tcs = new TaskCompletionSource<HttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ctx.Post(async _ =>
            {
                try { tcs.SetResult(await Http.Request(url, method, body, headers, _timeoutMs)); }
                catch (Exception ex) { tcs.SetException(ex); }
            }, null);
            return tcs.Task;
        }

        // No context captured -> the handler was constructed off the script thread and we can't
        // know which resource to marshal to. Fail loudly instead of hard-crashing FXServer.
        return Task.FromException<HttpResponse>(new InvalidOperationException(
            "FlashHttpMessageHandler must be constructed on the script thread (e.g. in OnStart) " +
            "to be usable from a background thread."));
    }
}
