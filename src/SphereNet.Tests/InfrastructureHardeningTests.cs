using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Security;
using SphereNet.Game.Scripting;
using SphereNet.Host;
using SphereNet.Panel;
using SphereNet.Panel.Auth;
using SphereNet.Panel.Logging;
using SphereNet.Server.Ipc;

namespace SphereNet.Tests;

public sealed class InfrastructureHardeningTests
{
    [Fact]
    public void SafePath_RejectsSiblingPrefixAndAbsolutePaths()
    {
        string root = Path.Combine(Path.GetTempPath(), "spherenet-safe-root");

        Assert.False(SafePath.TryResolveUnderRoot(root,
            Path.Combine("..", "spherenet-safe-root-backup", "secret.scp"), out _, out _));
        Assert.False(SafePath.TryResolveUnderRoot(root,
            Path.Combine(Path.GetPathRoot(root)!, "spherenet-safe-root-backup", "secret.scp"), out _, out _));
        Assert.True(SafePath.TryResolveUnderRoot(root,
            Path.Combine("sub", "valid.scp"), out string resolved, out _));
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "sub", "valid.scp")), resolved);
    }

    [Fact]
    public void ScriptFileHandle_CannotEscapeItsRoot()
    {
        string parent = Path.Combine(Path.GetTempPath(), "spherenet-file-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(parent, "files");
        string sibling = Path.Combine(parent, "files-private");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(root, "inside.txt"), "inside");
        string outside = Path.Combine(sibling, "secret.txt");
        File.WriteAllText(outside, "secret");

        try
        {
            using var handle = new ScriptFileHandle(root);
            Assert.True(handle.Open("inside.txt"));
            handle.Close();
            Assert.False(handle.Open(Path.Combine("..", "files-private", "secret.txt")));
            Assert.False(handle.Open(outside));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void TokenStore_ExpiresPurgesAndRevokesTokens()
    {
        DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var store = new TokenStore(TimeSpan.FromMinutes(5), () => now);

        string expired = store.Create();
        Assert.True(store.Validate(expired));
        now = now.AddMinutes(6);
        Assert.False(store.Validate(expired));
        Assert.Equal(0, store.Count);

        string revoked = store.Create();
        store.Revoke(revoked);
        Assert.False(store.Validate(revoked));
    }

    [Fact]
    public async Task IpcBridge_PreservesFalseResultsAndRemoteErrors()
    {
        string pipeName = "spherenet-test-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server = new IpcServer(pipeName);
        server.SetContext(new PanelContext
        {
            CreateAccount = (_, _) => false,
            SetAccountBanned = (_, _) => true,
        });

        Task serverTask = server.RunAsync(cts.Token);
        using var bridge = new IpcBridge();
        await bridge.ConnectAsync(pipeName);

        Assert.False(await bridge.MutateAsync("account_create", new { name = "duplicate", pass = "x" }));
        await Assert.ThrowsAsync<PanelBackendOperationException>(
            () => bridge.MutateAsync("unknown_operation"));
        var invalidRequest = await Assert.ThrowsAsync<PanelBackendOperationException>(
            () => bridge.MutateAsync("account_ban", new { name = "missing-flag" }));
        Assert.Equal("invalid_request", invalidRequest.Code);

        bridge.Disconnect();
        cts.Cancel();
        await WaitForShutdownAsync(serverTask);
    }

    [Fact]
    public async Task IpcServer_SerializesConcurrentMutations()
    {
        string pipeName = "spherenet-serial-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int active = 0;
        int maxActive = 0;

        using var server = new IpcServer(pipeName);
        server.SetContext(new PanelContext
        {
            CreateAccount = (_, _) =>
            {
                int current = Interlocked.Increment(ref active);
                int observed;
                do
                {
                    observed = Volatile.Read(ref maxActive);
                    if (observed >= current) break;
                } while (Interlocked.CompareExchange(ref maxActive, current, observed) != observed);

                Thread.Sleep(40);
                Interlocked.Decrement(ref active);
                return true;
            },
        });

        Task serverTask = server.RunAsync(cts.Token);
        using var bridge = new IpcBridge();
        await bridge.ConnectAsync(pipeName);

        Task<bool>[] requests = Enumerable.Range(0, 4)
            .Select(i => bridge.MutateAsync("account_create", new { name = "a" + i, pass = "x" }))
            .ToArray();
        Assert.All(await Task.WhenAll(requests), Assert.True);
        Assert.Equal(1, maxActive);

        bridge.Disconnect();
        cts.Cancel();
        await WaitForShutdownAsync(serverTask);
    }

    [Fact]
    public async Task PanelHost_ReturnsTruthfulMutationAndBackendStatuses()
    {
        int port = GetFreeTcpPort();
        var context = new PanelContext
        {
            AdminPassword = PasswordHelper.Hash("panel-test"),
            CreateAccount = (_, _) => false,
            SetAccountBanned = (_, _) => false,
            GetAllAccounts = () => throw new PanelBackendUnavailableException("offline"),
        };

        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
        using var sink = new PanelLogSink();
        using var host = new PanelHost(context, port, sink, loggerFactory.CreateLogger<PanelHost>());
        host.Start();

        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        await WaitForHealthAsync(client);

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new { password = "panel-test" });
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(login);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        var create = await client.PostAsJsonAsync("/api/accounts", new { name = "duplicate", password = "x" });
        Assert.Equal(HttpStatusCode.Conflict, create.StatusCode);

        var ban = await client.PostAsync("/api/accounts/missing/ban", null);
        Assert.Equal(HttpStatusCode.NotFound, ban.StatusCode);

        var accounts = await client.GetAsync("/api/accounts");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, accounts.StatusCode);
    }

    [Fact]
    public async Task PanelHost_LocalHint_OnlyLeaksPlaintextPasswordWhenOptedIn()
    {
        int port = GetFreeTcpPort();
        string dir = Path.Combine(Path.GetTempPath(), "spherenet-hint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string iniPath = Path.Combine(dir, "sphere.ini");

        try
        {
            // Opted out: the flag is absent, so the plaintext password stays server-side.
            File.WriteAllText(iniPath, "[SPHERE]\nAdminPassword=1234\n");
            var context = new PanelContext { AdminPassword = "1234", IniPath = iniPath };

            using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
            using var sink = new PanelLogSink();
            using var host = new PanelHost(context, port, sink, loggerFactory.CreateLogger<PanelHost>());
            host.Start();

            using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
            await WaitForHealthAsync(client);

            Assert.Null(await GetHintAsync(client));

            // Opted in with a plaintext password: the login page gets the hint.
            File.WriteAllText(iniPath, "[SPHERE]\nAdminPassword=1234\nAdminPanelAutoFill=1\n");
            Assert.Equal("1234", await GetHintAsync(client));

            // Opted in but hashed: nothing to recover, so no hint despite the flag.
            context.AdminPassword = PasswordHelper.Hash("1234");
            Assert.Null(await GetHintAsync(client));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// With no AppUpdateRepo configured, the update routes must answer 404
    /// themselves rather than being left unmapped. An unmapped /api path is not
    /// a 404: the SPA fallback claims it, so GET returns index.html with 200
    /// (the panel then reads markup as a status object and renders a
    /// half-broken page) and POST returns 405 against that GET-only fallback.
    /// </summary>
    [Fact]
    public async Task PanelHost_UnconfiguredUpdater_Returns404NotFallbackHtmlOr405()
    {
        int port = GetFreeTcpPort();
        // UpdateSettings left null = sphere.ini without AppUpdateRepo.
        var context = new PanelContext { AdminPassword = PasswordHelper.Hash("panel-test") };

        using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
        using var sink = new PanelLogSink();
        using var host = new PanelHost(context, port, sink, loggerFactory.CreateLogger<PanelHost>());
        host.Start();

        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        await WaitForHealthAsync(client);

        // The update routes sit behind the bearer gate like the rest of /api.
        var anonymous = await client.GetAsync("/api/update/status");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new { password = "panel-test" });
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(login);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        var status = await client.GetAsync("/api/update/status");
        Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
        // Not the SPA fallback: markup here is what made the panel render a
        // half-broken "Sunucu guncel" instead of the "not configured" notice.
        Assert.DoesNotContain("<!doctype", await status.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);

        var check = await client.PostAsync("/api/update/check", null);
        Assert.Equal(HttpStatusCode.NotFound, check.StatusCode);

        var apply = await client.PostAsync("/api/update/apply", null);
        Assert.Equal(HttpStatusCode.NotFound, apply.StatusCode);
    }

    private static async Task<string?> GetHintAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/auth/local-hint");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<HintResponse>())?.Password;
    }

    private static async Task WaitForHealthAsync(HttpClient client)
    {
        Exception? lastError = null;
        for (int i = 0; i < 40; i++)
        {
            try
            {
                using var response = await client.GetAsync("/health");
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("Panel host did not become healthy", lastError);
    }

    private static async Task WaitForShutdownAsync(Task serverTask)
    {
        try { await serverTask.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (OperationCanceledException) { }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed record LoginResponse(string Token, string ServerName);

    private sealed record HintResponse(string? Password);
}
