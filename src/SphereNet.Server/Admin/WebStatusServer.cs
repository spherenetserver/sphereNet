using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Game.World;

namespace SphereNet.Server.Admin;

/// <summary>
/// Lightweight HTTP status endpoint for monitoring.
/// Serves JSON status on GET / and GET /status.
/// </summary>
public sealed class WebStatusServer : IDisposable
{
    private HttpListener? _listener;
    private readonly ILogger _logger;
    private readonly GameWorld _world;
    private readonly AccountManager _accounts;
    private readonly Func<int> _getActiveConnections;
    private readonly Func<object?>? _getRuntimeMetrics;
    private readonly DateTime _startTime;
    private bool _running;

    /// <summary>Dialog designer hooks — wired by the host. Null = the
    /// /dialogedit routes answer 404.</summary>
    public delegate bool GumpArtLookup(int id, out int width, out int height, out byte[] rgba);
    public GumpArtLookup? GetGumpArt { get; set; }
    public Func<List<string>>? ListDialogNames { get; set; }
    public Func<string, string?>? GetDialogSource { get; set; }

    private readonly Dictionary<int, byte[]?> _gumpPngCache = [];

    public WebStatusServer(GameWorld world, AccountManager accounts,
        Func<int> getActiveConnections, ILogger logger, Func<object?>? getRuntimeMetrics = null)
    {
        _world = world;
        _accounts = accounts;
        _getActiveConnections = getActiveConnections;
        _getRuntimeMetrics = getRuntimeMetrics;
        _logger = logger;
        _startTime = DateTime.UtcNow;
    }

    public bool Start(int port)
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            _running = true;
            _logger.LogInformation("Web status endpoint on http://localhost:{Port}/", port);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start web status on port {Port}", port);
            return false;
        }
    }

    private IAsyncResult? _pendingAccept;

    public void Tick()
    {
        if (!_running || _listener == null) return;

        while (_listener.IsListening)
        {
            HttpListenerContext? ctx;
            try
            {
                // ONE pending accept survives across ticks. Starting a fresh
                // BeginGetContext every tick abandons the previous one — an
                // arriving request is then consumed by an accept nobody ever
                // reaps (client times out) and the pending ops pile up.
                _pendingAccept ??= _listener.BeginGetContext(null, null);
                if (!_pendingAccept.AsyncWaitHandle.WaitOne(0)) return;
                ctx = _listener.EndGetContext(_pendingAccept);
                _pendingAccept = null;
            }
            catch (Exception ex)
            {
                _pendingAccept = null;
                if (_running)
                    _logger.LogWarning(ex, "Web status listener error");
                return;
            }

            try
            {
                HandleRequest(ctx);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Web status request error");
            }
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        var request = ctx.Request;
        var response = ctx.Response;

        string path = request.Url?.AbsolutePath ?? "/";

        if (path is "/" or "/status")
        {
            ServeStatus(response);
        }
        else if (path == "/health")
        {
            ServeHealth(response);
        }
        else if (path == "/dialogedit")
        {
            ServeHtml(response, DialogEditorPage.Html);
        }
        else if (path == "/dialog/list" && ListDialogNames != null)
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(ListDialogNames());
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = body.Length;
            response.OutputStream.Write(body);
        }
        else if (path == "/dialog/source" && GetDialogSource != null)
        {
            string? name = request.QueryString["name"];
            string? src = string.IsNullOrWhiteSpace(name) ? null : GetDialogSource(name);
            if (src == null)
            {
                response.StatusCode = 404;
                src = "";
            }
            byte[] body = Encoding.UTF8.GetBytes(src);
            response.ContentType = "text/plain; charset=utf-8";
            response.ContentLength64 = body.Length;
            response.OutputStream.Write(body);
        }
        else if (path.StartsWith("/gump/", StringComparison.Ordinal) && GetGumpArt != null)
        {
            ServeGump(response, path);
        }
        else
        {
            response.StatusCode = 404;
            byte[] body = Encoding.UTF8.GetBytes("{\"error\":\"not found\"}");
            response.ContentType = "application/json";
            response.ContentLength64 = body.Length;
            response.OutputStream.Write(body);
        }

        response.Close();
    }

    private static void ServeHtml(HttpListenerResponse response, string html)
    {
        byte[] body = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = body.Length;
        response.OutputStream.Write(body);
    }

    private void ServeGump(HttpListenerResponse response, string path)
    {
        // /gump/{id}.png  (id decimal or 0x hex)
        string idPart = path["/gump/".Length..];
        if (idPart.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            idPart = idPart[..^4];
        int id;
        if (idPart.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            _ = int.TryParse(idPart[2..], System.Globalization.NumberStyles.HexNumber, null, out id);
        else
            _ = int.TryParse(idPart, out id);

        byte[]? png;
        lock (_gumpPngCache)
        {
            if (!_gumpPngCache.TryGetValue(id, out png))
            {
                png = GetGumpArt!(id, out int w, out int h, out byte[] rgba)
                    ? MiniPng.Encode(w, h, rgba)
                    : null;
                _gumpPngCache[id] = png;
            }
        }

        if (png == null)
        {
            response.StatusCode = 404;
            return;
        }
        response.ContentType = "image/png";
        response.Headers["Cache-Control"] = "public, max-age=86400";
        response.ContentLength64 = png.Length;
        response.OutputStream.Write(png);
    }

    private void ServeStatus(HttpListenerResponse response)
    {
        var (chars, items, sectors) = _world.GetStats();
        var uptime = DateTime.UtcNow - _startTime;

        var status = new
        {
            server = "SphereNet",
            uptime = uptime.ToString(@"d\.hh\:mm\:ss"),
            uptimeSeconds = (int)uptime.TotalSeconds,
            characters = chars,
            items = items,
            sectors = sectors,
            connections = _getActiveConnections(),
            accounts = _accounts.Count,
            ticks = _world.TickCount,
            memoryMB = GC.GetTotalMemory(false) / (1024 * 1024),
            runtime = _getRuntimeMetrics?.Invoke()
        };

        string json = JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });
        byte[] body = Encoding.UTF8.GetBytes(json);
        response.ContentType = "application/json";
        response.ContentLength64 = body.Length;
        response.OutputStream.Write(body);
    }

    private static void ServeHealth(HttpListenerResponse response)
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
        response.ContentType = "application/json";
        response.ContentLength64 = body.Length;
        response.OutputStream.Write(body);
    }

    public void Dispose()
    {
        _running = false;
        try { _listener?.Stop(); }
        catch (Exception ex) { _logger.LogDebug(ex, "WebStatus listener stop error"); }
        try { _listener?.Close(); }
        catch (Exception ex) { _logger.LogDebug(ex, "WebStatus listener close error"); }
    }
}
