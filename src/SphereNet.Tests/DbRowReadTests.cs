using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SphereNet.Scripting.Execution;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// A query's rows are copied by name and value only. DataTable.Load asked the reader
/// for its schema table, which Microsoft.Data.Sqlite answers with extra metadata
/// queries per column: a nine-column page of a staff dialog cost ~46 ms per render on
/// the main thread. What db.row.* reads must not change: cell text, the column-name
/// lookup, the Load-style suffix on a repeated name, and an empty NULL.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DbRowReadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sphnet_rows_{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private ScriptDbAdapter Open()
    {
        if (!DbProviderFactories.TryGetFactory("Microsoft.Data.Sqlite", out _))
            DbProviderFactories.RegisterFactory("Microsoft.Data.Sqlite", SqliteFactory.Instance);
        Directory.CreateDirectory(_dir);
        var db = new ScriptDbAdapter(LoggerFactory.Create(_ => { }).CreateLogger<ScriptDbAdapter>());
        Assert.True(db.ConnectFile("rows.db", _dir, out string error), error);
        Assert.True(db.Execute(
            "CREATE TABLE objs (id INTEGER PRIMARY KEY, uid TEXT, name TEXT, data JSON, " +
            "created_at TIMESTAMP NOT NULL DEFAULT '2026-09-23 10:00:00', note TEXT);" +
            "INSERT INTO objs (uid, name, data) VALUES ('04000400b', 'Inn', '{\"p\":\"1,2\"}');" +
            "INSERT INTO objs (uid, name, data) VALUES ('010096', 'Ice Snake', '{\"npc\":\"8\"}');",
            out _, out error), error);
        return db;
    }

    private static string Row(ScriptDbAdapter db, string key) =>
        db.TryResolveRowValue("db.row." + key, out string v) ? v : "<unresolved>";

    [Fact]
    public void CellsReadAsTheSameText()
    {
        using var db = Open();
        Assert.True(db.Query("SELECT * FROM objs ORDER BY id", out int rows, out string error), error);
        Assert.Equal(2, rows);
        Assert.Equal("2", Row(db, "numrows"));
        Assert.Equal("6", Row(db, "numcols"));
        Assert.Equal("1", Row(db, "0.id"));
        Assert.Equal("04000400b", Row(db, "0.uid"));
        Assert.Equal("Ice Snake", Row(db, "1.NAME"));            // names match case-insensitively
        Assert.Equal("{\"npc\":\"8\"}", Row(db, "1.data"));
        Assert.Equal("2026-09-23 10:00:00", Row(db, "0.created_at"));
        Assert.Equal("", Row(db, "0.note"));                   // NULL reads empty
        Assert.Equal("Inn", Row(db, "0.2"));                   // by column index
    }

    [Fact]
    public void ARepeatedColumnNameGetsTheLoadSuffix()
    {
        using var db = Open();
        Assert.True(db.Query("SELECT a.id, b.id FROM objs a JOIN objs b ON b.id = a.id + 1", out _, out string error), error);
        Assert.Equal("1", Row(db, "0.id"));
        Assert.Equal("2", Row(db, "0.id1"));
    }

    [Fact]
    public void AQueryDoesNotPayForSchemaMetadata()
    {
        using var db = Open();
        const string sql = "SELECT * FROM objs LIMIT 0,15";
        db.Query(sql, out _, out _);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
            Assert.True(db.Query(sql, out _, out _));
        // Measured ~46 ms per nine-column query through DataTable.Load, ~0.35 ms now.
        Assert.True(sw.Elapsed.TotalMilliseconds / 20 < 10,
            $"a small query took {sw.Elapsed.TotalMilliseconds / 20:F1} ms");
    }
}
