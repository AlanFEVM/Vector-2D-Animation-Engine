using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VectorAnimationEngine;

/// <summary>Stateless MCP Streamable HTTP transport; JSON responses, no SSE stream.</summary>
internal sealed class CodexBridgeServer : IDisposable
{
    internal const int MaximumRequestBytes = 1024 * 1024;
    private readonly Func<string, JsonElement, CancellationToken, Task<object>> _execute;
    private HttpListener? _listener;
    private CancellationTokenSource? _shutdown;
    private ApplicationSettings? _settings;
    private int _activeRequests;
    public string Status { get; private set; } = "Stopped";
    public bool IsRunning => _listener?.IsListening == true;
    public static string EndpointFor(int port) => $"http://127.0.0.1:{port}/mcp/";

    // Apply/Stop/Dispose belong to the UI thread. Each accepted request captures
    // its own settings and lifetime, so a restart cannot authorize an old request.
    public CodexBridgeServer(Func<string, JsonElement, CancellationToken, Task<object>> execute)
        => _execute = execute;

    public void Apply(ApplicationSettings settings)
    {
        if (IsRunning && _settings is { } previous
            && previous.CodexIntegrationEnabled == settings.CodexIntegrationEnabled
            && previous.CodexIntegrationPort == settings.CodexIntegrationPort
            && previous.CodexIntegrationAuthToken == settings.CodexIntegrationAuthToken
            && previous.CodexIntegrationAllowChanges == settings.CodexIntegrationAllowChanges) return;
        Stop();
        _settings = settings;
        if (!settings.CodexIntegrationEnabled) return;
        var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add(EndpointFor(settings.CodexIntegrationPort));
            listener.Start();
            _listener = listener;
            _shutdown = new CancellationTokenSource();
            Status = "Listening";
            _ = AcceptAsync(listener, settings, _shutdown.Token);
            AppLog.Info($"MCP bridge listening at {EndpointFor(settings.CodexIntegrationPort)}");
        }
        catch (Exception ex) when (ex is HttpListenerException or InvalidOperationException)
        {
            listener.Close();
            Status = ex.Message;
            AppLog.Error("Could not start the MCP bridge.", ex);
        }
    }

    private async Task AcceptAsync(HttpListener listener, ApplicationSettings settings, CancellationToken shutdown)
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().ConfigureAwait(false);
                if (Interlocked.Increment(ref _activeRequests) > 8)
                {
                    Interlocked.Decrement(ref _activeRequests);
                    context.Response.StatusCode = 503;
                    context.Response.Close();
                    continue;
                }
                // Do not pass a scheduling cancellation token: the handler owns
                // releasing the accepted context even if shutdown has begun.
                _ = Task.Run(() => HandleAsync(context, settings, shutdown));
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
            if (!shutdown.IsCancellationRequested) AppLog.Error("MCP listener stopped unexpectedly.", ex);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, ApplicationSettings settings, CancellationToken shutdown)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var cancellation = timeout.Token;
        try
        {
            var request = context.Request;
            var response = context.Response;
            // Native clients do not send Origin. Reject browser-originated calls
            // and non-literal hosts; never grant a wildcard CORS permission.
            if (!request.IsLocal || request.Url?.Host != "127.0.0.1"
                || request.Headers["Origin"] is not null)
            {
                response.StatusCode = 403;
                return;
            }
            if (!IsAuthorized(request.Headers["Authorization"], settings.CodexIntegrationAuthToken))
            {
                response.StatusCode = 401;
                response.Headers["WWW-Authenticate"] = "Bearer";
                return;
            }
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405;
                response.Headers["Allow"] = "POST";
                return;
            }
            if (request.ContentType?.Split(';')[0].Trim() != "application/json")
            {
                response.StatusCode = 415;
                return;
            }
            var version = request.Headers["MCP-Protocol-Version"];
            if (version is not null && !CodexBridgeProtocol.ProtocolVersions.Contains(version))
            {
                response.StatusCode = 400;
                return;
            }
            if (request.ContentLength64 > MaximumRequestBytes)
            {
                response.StatusCode = 413;
                return;
            }
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await request.InputStream.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
            {
                if (body.Length + count > MaximumRequestBytes)
                {
                    response.StatusCode = 413;
                    return;
                }
                body.Write(buffer, 0, count);
            }
            object? result;
            try
            {
                using var document = JsonDocument.Parse(body.ToArray());
                result = await CodexBridgeProtocol.HandleAsync(document.RootElement,
                    (name, args, ct) => _execute(name, args, ct), cancellation).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                result = CodexBridgeProtocol.Error(null, -32700, "Invalid JSON.");
            }
            if (result is null)
            {
                response.StatusCode = 202;
                return;
            }
            await WriteAsync(response, result, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { context.Response.Abort(); }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
        {
            if (!shutdown.IsCancellationRequested) AppLog.Warn($"MCP connection closed: {ex.GetType().Name}");
        }
        catch (Exception ex) { AppLog.Error("MCP request failed.", ex); context.Response.Abort(); }
        finally { context.Response.Close(); Interlocked.Decrement(ref _activeRequests); }
    }

    private static bool IsAuthorized(string? authorization, string token)
    {
        if (token.Length == 0) return true;
        return authorization is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(authorization), Encoding.UTF8.GetBytes($"Bearer {token}"));
    }

    private static async Task WriteAsync(HttpListenerResponse response, object value, CancellationToken cancellation)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, CodexBridgeProtocol.JsonOptions);
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellation).ConfigureAwait(false);
    }

    public void Stop()
    {
        _shutdown?.Cancel();
        _listener?.Close();
        _listener = null;
        _shutdown?.Dispose();
        _shutdown = null;
        Status = "Stopped";
    }

    public void Dispose() => Stop();
}
