using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using SphereNet.Panel.Auth;
using SphereNet.Panel.Hubs;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// A logout that lands while a panel client is still connecting (review work item D08).
///
/// Admitting a connection is a check and then an act: validate the token, then register
/// the connection so a later revocation can find it. Between those two steps there is a
/// window, and a logout falling into it sweeps a registry this connection is not in yet
/// — it aborts nothing, and a moment later the connection registers itself under a
/// token that no longer exists.
///
/// What makes that worse than it sounds is who it leaves behind. The only other place a
/// token is re-checked is a hub METHOD, and a client that only watches the log and stats
/// stream never calls one. So the window produces exactly the thing the logout was for:
/// a live listener on a dead session, with nothing left to close it.
///
/// The race is entered from INSIDE rather than raced for: registering a connection
/// reads its id, so a test connection whose id getter logs the session out puts the
/// revocation exactly where it hurts. Threads and a barrier would only be a slower way
/// to arrange the same interleaving, and one that can pass by missing it.
/// </summary>
public sealed class PanelConnectionRaceTests
{
    private readonly ITestOutputHelper _out;
    public PanelConnectionRaceTests(ITestOutputHelper output) => _out = output;

    /// <summary>The smallest thing that can stand in for a live connection: it
    /// remembers whether it was aborted.</summary>
    private sealed class FakeConnection : HubCallerContext
    {
        private readonly CancellationTokenSource _aborted = new();
        private readonly string _id;
        private Action? _onFirstIdRead;

        /// <param name="onFirstIdRead">Fires the first time the connection id is read,
        /// which is what Register does with it - so this is the window, entered from
        /// inside rather than guessed at from outside.</param>
        public FakeConnection(string id, Action? onFirstIdRead = null)
        {
            _id = id;
            _onFirstIdRead = onFirstIdRead;
        }

        public bool Aborted { get; private set; }

        public override string ConnectionId
        {
            get
            {
                Interlocked.Exchange(ref _onFirstIdRead, null)?.Invoke();
                return _id;
            }
        }
        public override string? UserIdentifier => null;
        public override System.Security.Claims.ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => _aborted.Token;

        public override void Abort()
        {
            Aborted = true;
            _aborted.Cancel();
        }
    }

    private static (TokenStore Tokens, HubConnectionRegistry Connections) Panel()
    {
        var tokens = new TokenStore(TimeSpan.FromHours(1));
        var connections = new HubConnectionRegistry();
        // How the panel wires them together: a token that stops being valid takes its
        // open connections down with it.
        tokens.TokenInvalidated += t => connections.AbortToken(t);
        return (tokens, connections);
    }

    [Fact]
    public void ALogoutInsideTheRegistrationDoesNotLeaveAListenerBehind()
    {
        var (tokens, connections) = Panel();
        string token = tokens.Create();

        // The logout lands where it does the damage: after the handshake check has
        // passed, while the connection is being registered. The sweep it triggers runs
        // against a registry entry this connection has not finished reaching, so it
        // aborts nothing - and a moment later the connection is in place, holding a
        // token that no longer exists.
        var connection = new FakeConnection("c1", onFirstIdRead: () => tokens.Revoke(token));

        bool accepted = ServerHub.TryAcceptConnection(tokens, connections, token, connection);

        _out.WriteLine($"accepted={accepted} aborted={connection.Aborted} " +
                       $"a later sweep would abort {connections.AbortToken(token)} connection(s)");

        // Nothing is left holding the dead session. The sweep count above is why it
        // has to be caught here: by then there is nothing for a sweep to find.
        Assert.False(accepted);
        Assert.True(connection.Aborted);
    }

    [Fact]
    public void AnOrdinaryConnectionIsAdmittedAndRegistered()
    {
        // The control: closing the window must not have closed the door. Every
        // assertion above would pass if connections had stopped being accepted.
        var (tokens, connections) = Panel();
        string token = tokens.Create();
        var connection = new FakeConnection("c1");

        Assert.True(ServerHub.TryAcceptConnection(tokens, connections, token, connection));
        Assert.False(connection.Aborted);
        Assert.Equal(1, connections.ConnectionCount);
    }

    [Fact]
    public void ALogoutAfterTheConnectionIsRegisteredStillClosesIt()
    {
        // The path that already worked, kept honest: once the connection is in the
        // registry, the sweep is what closes it.
        var (tokens, connections) = Panel();
        string token = tokens.Create();
        var connection = new FakeConnection("c1");
        Assert.True(ServerHub.TryAcceptConnection(tokens, connections, token, connection));

        tokens.Revoke(token);

        Assert.True(connection.Aborted);
        Assert.Equal(0, connections.ConnectionCount);
    }

    [Fact]
    public void AConnectionWithATokenThatWasNeverValidIsRefusedAtTheDoor()
    {
        var (tokens, connections) = Panel();
        var connection = new FakeConnection("c1");

        Assert.False(ServerHub.TryAcceptConnection(tokens, connections, "not-a-token", connection));
        Assert.True(connection.Aborted);
        Assert.Equal(0, connections.ConnectionCount);
    }

    [Fact]
    public void OneSessionsLogoutDoesNotDisturbAnother()
    {
        var (tokens, connections) = Panel();
        string mine = tokens.Create();
        string theirs = tokens.Create();
        var myConnection = new FakeConnection("c1");
        var theirConnection = new FakeConnection("c2");

        Assert.True(ServerHub.TryAcceptConnection(tokens, connections, mine, myConnection));
        Assert.True(ServerHub.TryAcceptConnection(tokens, connections, theirs, theirConnection));

        tokens.Revoke(mine);

        _out.WriteLine($"after one logout: mine aborted={myConnection.Aborted}, " +
                       $"theirs aborted={theirConnection.Aborted}, registry={connections.ConnectionCount}");
        Assert.True(myConnection.Aborted);
        Assert.False(theirConnection.Aborted);
        Assert.Equal(1, connections.ConnectionCount);
    }
}
