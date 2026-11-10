using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeReach.Core;
using CodeReach.Core.Tools;

namespace CodeReach.Mcp;

/// <summary>
/// The UI door: a page served on the loopback interface, calling the same tool registry as MCP and the CLI.
/// Every API call needs the one-time token; requests naming another host or coming from another origin are
/// refused, which keeps other web pages in the same browser (and DNS rebinding) away from the index.
/// </summary>
internal sealed class UiServer : IAsyncDisposable
{
    private const string TokenHeader = "X-CodeReach-Token";
    private const int MaxBodyBytes = Limits.MaxDiffBytes + 64 * 1024;

    private static readonly Dictionary<string, (string Resource, string ContentType)> Files = new(StringComparer.Ordinal)
    {
        ["/"] = ("index.html", "text/html; charset=utf-8"),
        ["/app.css"] = ("app.css", "text/css; charset=utf-8"),
        ["/app.js"] = ("app.js", "text/javascript; charset=utf-8"),
    };

    private readonly IIndexHost _host;
    private readonly HttpListener _listener;
    private readonly byte[] _token;
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;

    private UiServer(IIndexHost host, int port)
    {
        _host = host;
        Port = port;
        _token = Encoding.ASCII.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant());
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public int Port { get; }

    public string Origin => $"http://127.0.0.1:{Port}";

    public string Token => Encoding.ASCII.GetString(_token);

    /// <summary>The address to open. The token rides in the fragment, which browsers never send to a server or a referrer.</summary>
    public string Url => $"{Origin}/#t={Token}";

    public static UiServer Start(IIndexHost host, int port = 0)
    {
        var server = new UiServer(host, port == 0 ? FreePort() : port);
        server._listener.Start();
        server._loop = Task.Run(server.AcceptAsync);
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Close();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or HttpListenerException or OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ObjectDisposedException or HttpListenerException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers["Cache-Control"] = "no-store";
            await RouteAsync(context.Request, response).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"[codereach ui] {context.Request.HttpMethod} {context.Request.Url?.AbsolutePath} failed: {exception.Message}").ConfigureAwait(false);
            try
            {
                await WriteAsync(response, 500, "text/plain; charset=utf-8", "internal error; details are in the server's stderr").ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The client went away.
            }
        }
        finally
        {
            response.Close();
        }
    }

    private async Task RouteAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        // DNS rebinding: a hostile name that resolves to 127.0.0.1 still sends its own name as Host.
        if (request.Headers["Host"] != $"127.0.0.1:{Port}")
        {
            await WriteAsync(response, 421, "text/plain; charset=utf-8", "wrong host").ConfigureAwait(false);
            return;
        }

        var path = request.Url?.AbsolutePath ?? "/";
        if (request.HttpMethod == "GET" && Files.TryGetValue(path, out var file))
        {
            response.Headers["Content-Security-Policy"] =
                "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
            await WriteAsync(response, 200, file.ContentType, Resource(file.Resource)).ConfigureAwait(false);
            return;
        }

        if (!path.StartsWith("/api/", StringComparison.Ordinal))
        {
            await WriteAsync(response, 404, "text/plain; charset=utf-8", "not found").ConfigureAwait(false);
            return;
        }

        var origin = request.Headers["Origin"];
        if (origin is not null && origin != Origin)
        {
            await WriteAsync(response, 403, "text/plain; charset=utf-8", "wrong origin").ConfigureAwait(false);
            return;
        }

        var token = Encoding.ASCII.GetBytes(request.Headers[TokenHeader] ?? string.Empty);
        if (!CryptographicOperations.FixedTimeEquals(token, _token))
        {
            await WriteAsync(response, 401, "text/plain; charset=utf-8", "missing or wrong token; open the address codereach printed").ConfigureAwait(false);
            return;
        }

        switch (request.HttpMethod, path)
        {
            case ("GET", "/api/tools"):
                var tools = new JsonArray([.. ToolRegistry.All.Select(t => (JsonNode)new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["schema"] = JsonNode.Parse(t.InputSchema()),
                })]);
                await WriteJsonAsync(response, 200, new JsonObject { ["tools"] = tools, ["state"] = _host.DescribeState() }).ConfigureAwait(false);
                return;
            case ("POST", "/api/call"):
                await CallAsync(request, response).ConfigureAwait(false);
                return;
            default:
                await WriteAsync(response, 404, "text/plain; charset=utf-8", "not found").ConfigureAwait(false);
                return;
        }
    }

    private async Task CallAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
        {
            await WriteAsync(response, 415, "text/plain; charset=utf-8", "send application/json").ConfigureAwait(false);
            return;
        }

        var body = new byte[MaxBodyBytes + 1];
        var length = 0;
        int read;
        while ((read = await request.InputStream.ReadAsync(body.AsMemory(length, body.Length - length), _stop.Token).ConfigureAwait(false)) > 0)
        {
            length += read;
            if (length > MaxBodyBytes)
            {
                await WriteAsync(response, 413, "text/plain; charset=utf-8", "request too large").ConfigureAwait(false);
                return;
            }
        }

        string tool;
        JsonElement arguments;
        try
        {
            using var document = JsonDocument.Parse(body.AsMemory(0, length));
            tool = document.RootElement.GetProperty("tool").GetString() ?? string.Empty;
            arguments = document.RootElement.TryGetProperty("args", out var given) ? given.Clone() : default;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            await WriteAsync(response, 400, "text/plain; charset=utf-8", "expected {\"tool\": name, \"args\": {...}}").ConfigureAwait(false);
            return;
        }

        var result = await ToolRegistry.CallAsync(_host, tool, arguments, _stop.Token).ConfigureAwait(false);
        await WriteJsonAsync(response, 200, new JsonObject
        {
            ["text"] = result.Text,
            ["isError"] = result.IsError,
            ["finding"] = result.Finding,
            ["data"] = result.Data,
        }).ConfigureAwait(false);
    }

    private static string Resource(string name)
    {
        using var stream = typeof(UiServer).Assembly.GetManifestResourceStream("CodeReach.Mcp.ui." + name)
            ?? throw new InvalidOperationException("UI resource missing: " + name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static Task WriteJsonAsync(HttpListenerResponse response, int status, JsonObject body) =>
        WriteAsync(response, status, "application/json; charset=utf-8", body.ToJsonString());

    private static async Task WriteAsync(HttpListenerResponse response, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = status;
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }
}
