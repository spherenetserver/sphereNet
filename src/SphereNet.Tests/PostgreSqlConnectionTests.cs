using System;
using System.Data.Common;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Npgsql;
using SphereNet.Core.Configuration;
using SphereNet.Scripting.Execution;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// [PGSQL name] connections: the section is read as an Npgsql connection on port
/// 5432, the connection string is one Npgsql accepts, the provider is registered at
/// startup, and DB.ESCAPEDATA escapes for PostgreSQL rather than MySQL.
///
/// A live round trip runs only when SPHERENET_TEST_PGSQL holds an Npgsql connection
/// string; without it that test returns early, like the real-data compatibility tests.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PostgreSqlConnectionTests
{
    private static SphereConfig LoadIni(string text)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_pg_{Guid.NewGuid():N}.ini");
        File.WriteAllText(tmp, text);
        try
        {
            var parser = new IniParser();
            parser.Load(tmp);
            var config = new SphereConfig();
            config.LoadFromIni(parser);
            return config;
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void APgsqlSectionIsAnNpgsqlConnectionOnPort5432()
    {
        var config = LoadIni("""
            [PGSQL stats]
            Host=pg.local
            User=sphere
            Database=shard
            [PGSQL other]
            Host=pg2
            Port=6543
            """);

        var stats = config.DbConnections.Single(c => c.Name == "stats");
        Assert.Equal(DbConnectionConfig.PostgreSqlProvider, stats.Provider);
        Assert.True(stats.IsPostgreSql);
        Assert.Equal(5432, stats.Port);
        Assert.Equal(6543, config.DbConnections.Single(c => c.Name == "other").Port);
    }

    [Fact]
    public void TheConnectionStringIsOneNpgsqlReadsBack()
    {
        var cfg = new DbConnectionConfig
        {
            Name = "stats",
            Provider = DbConnectionConfig.PostgreSqlProvider,
            Host = "pg.local",
            Port = 5433,
            User = "sphere",
            Password = "p;w=d",
            Database = "shard",
            ConnectTimeout = 5000,
            ReadTimeout = 12,
            KeepAlive = true,
        };

        var b = new NpgsqlConnectionStringBuilder(cfg.BuildConnectionString());

        Assert.Equal("pg.local", b.Host);
        Assert.Equal(5433, b.Port);
        Assert.Equal("sphere", b.Username);
        Assert.Equal("p;w=d", b.Password);
        Assert.Equal("shard", b.Database);
        Assert.Equal(1024, b.Timeout);
        Assert.Equal(12, b.CommandTimeout);
        Assert.Equal(60, b.KeepAlive);
    }

    [Fact]
    public void StartupRegistersTheNpgsqlProvider()
    {
        SphereNet.Server.Program.RegisterDbProvidersForTests();

        Assert.Same(NpgsqlFactory.Instance, DbProviderFactories.GetFactory(DbConnectionConfig.PostgreSqlProvider));
    }

    [Fact]
    public void EscapeDataFollowsTheActiveConnectionsDialect()
    {
        using var db = new ScriptDbAdapter(LoggerFactory.Create(_ => { }).CreateLogger<ScriptDbAdapter>());
        db.RegisterConnection(new DbConnectionConfig { Name = "my" });
        db.RegisterConnection(new DbConnectionConfig
        {
            Name = "pg",
            Provider = DbConnectionConfig.PostgreSqlProvider,
            Port = DbConnectionConfig.PostgreSqlDefaultPort,
        });
        const string input = "O'Brien\\x";

        Assert.True(db.Select("my", out string e1), e1);
        Assert.Equal("O\\'Brien\\\\x", db.EscapeData(input));

        Assert.True(db.Select("pg", out string e2), e2);
        Assert.Equal("O''Brien\\x", db.EscapeData(input));
        Assert.Equal("ab", db.EscapeData("a\0b"));
    }

    [Fact]
    public void ALiveServerRoundTrip()
    {
        string? conn = Environment.GetEnvironmentVariable("SPHERENET_TEST_PGSQL");
        if (string.IsNullOrWhiteSpace(conn))
            return;

        SphereNet.Server.Program.RegisterDbProvidersForTests();
        using var db = new ScriptDbAdapter(LoggerFactory.Create(_ => { }).CreateLogger<ScriptDbAdapter>());
        Assert.True(db.Connect(DbConnectionConfig.PostgreSqlProvider, conn, out string e1), e1);
        string table = $"sphnet_probe_{Guid.NewGuid():N}";
        try
        {
            Assert.True(db.Execute($"CREATE TABLE {table} (name TEXT)", out _, out string e2), e2);
            Assert.True(db.Execute($"INSERT INTO {table} VALUES ('{db.EscapeData("O'Brien")}')", out _, out string e3), e3);
            Assert.True(db.Query($"SELECT name FROM {table}", out int rows, out string e4), e4);
            Assert.Equal(1, rows);
            Assert.True(db.TryResolveRowValue("DB.ROW.0.name", out string name));
            Assert.Equal("O'Brien", name);
        }
        finally
        {
            db.Execute($"DROP TABLE IF EXISTS {table}", out _, out _);
        }
    }
}
