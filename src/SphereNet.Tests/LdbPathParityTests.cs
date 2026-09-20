using System.Data.Common;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class LdbPathParityTests
{
    [Theory]
    [InlineData("adapter")]
    [InlineData("client")]
    [InlineData("server")]
    public void RelativeLdbPathUsesWorkingDirectoryInsteadOfScriptRoot(string route)
    {
        // Source-X CSQLite::Open passes the filename straight to sqlite3_open.
        // Keep the process CWD unchanged so parallel tests cannot be affected.
        string cwd = Directory.GetCurrentDirectory();
        string relative = Path.Combine($"ldb-probe-{Guid.NewGuid():N}", "db", "main.db");
        string file = Path.GetFullPath(relative, cwd);
        string root = Path.GetDirectoryName(Path.GetDirectoryName(file)!)!;
        string scripts = Path.Combine(root, "scripts");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        Directory.CreateDirectory(scripts);
        using var logs = LoggerFactory.Create(_ => { });
        DbProviderFactories.RegisterFactory("Microsoft.Data.Sqlite", SqliteFactory.Instance);
        try
        {
            using (var seed = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
            {
                seed.Open();
                using var command = seed.CreateCommand();
                command.CommandText = "CREATE TABLE restypes (name TEXT); INSERT INTO restypes VALUES ('working-directory');";
                command.ExecuteNonQuery();
            }
            using (var db = new ScriptDbAdapter(logs.CreateLogger<ScriptDbAdapter>()))
            {
                if (route == "adapter")
                    Assert.True(db.ConnectFile($"\"{relative}\"", out var error), error);
                else if (route == "client")
                {
                    var world = TestHarness.CreateWorld();
                    var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19436);
                    var ch = world.CreateCharacter();
                    TestHarness.AttachCharacter(client, ch);
                    client.SetScriptServices(scriptLdb: db, scriptDatabaseRoot: scripts);
                    Assert.True(client.TryExecuteScriptCommand(ch, "LDB.CONNECT", $"\"{relative}\"", null));
                }
                else
                {
                    var program = typeof(SphereNet.Server.Program);
                    var dbField = program.GetField("_scriptLdb", BindingFlags.NonPublic | BindingFlags.Static)!;
                    var resourcesField = program.GetField("_resources", BindingFlags.NonPublic | BindingFlags.Static)!;
                    var previousDb = dbField.GetValue(null);
                    var previousResources = resourcesField.GetValue(null);
                    try
                    {
                        dbField.SetValue(null, db);
                        resourcesField.SetValue(null, new ResourceHolder(logs.CreateLogger<ResourceHolder>()) { ScpBaseDir = scripts });
                        var method = program.GetMethod("HandleScriptDbVerb", BindingFlags.NonPublic | BindingFlags.Static)!;
                        Assert.Equal("1", method.Invoke(null, [$"LDB|CONNECT|\"{relative}\""]));
                    }
                    finally
                    {
                        dbField.SetValue(null, previousDb);
                        resourcesField.SetValue(null, previousResources);
                    }
                }
                Assert.True(db.Query("SELECT name FROM restypes", out int rows, out string queryError), queryError);
                Assert.Equal(1, rows);
                Assert.True(db.TryResolveRowValue("DB.ROW.0.name", out var name));
                Assert.Equal("working-directory", name);
                Assert.False(File.Exists(Path.Combine(scripts, relative)));
            }
        }
        finally
        {
            using var pooled = new SqliteConnection($"Data Source={file};");
            SqliteConnection.ClearPool(pooled);
            // Only remove the unique fixture directory created under our CWD.
            Assert.Equal(Path.GetFullPath(cwd), Path.GetDirectoryName(root));
            Directory.Delete(root, recursive: true);
        }
    }
}
