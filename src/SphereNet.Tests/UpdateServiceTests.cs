using SphereNet.Host;

namespace SphereNet.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public void FindRepositoryRoot_WalksUpFromPublishDirectory()
    {
        var root = CreateTempRepo();
        var nested = Path.Combine(root, "bin", "Release");
        Directory.CreateDirectory(nested);

        var found = UpdateService.FindRepositoryRoot(nested);

        Assert.Equal(root, found);
    }

    [Fact]
    public async Task StartAsync_RejectsConcurrentUpdate()
    {
        var root = CreateTempRepo();
        var releaseDir = Path.Combine(root, "bin", "Release");
        Directory.CreateDirectory(releaseDir);

        var firstCommandStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCommandToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new RecordingRunner(async command =>
        {
            firstCommandStarted.TrySetResult();
            await allowCommandToFinish.Task;
            return new UpdateCommandResult(0, "");
        });

        var service = new UpdateService(
            isServerRunning: () => false,
            saveServer: () => { },
            stopServer: () => { },
            startServer: () => { },
            hostBaseDir: releaseDir,
            runner: runner);

        var first = await service.StartAsync();
        await firstCommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await service.StartAsync();

        allowCommandToFinish.SetResult();
        await WaitUntilCompleted(service);

        Assert.True(first.Started);
        Assert.False(second.Started);
        Assert.Equal("Update is already running.", second.Message);
    }

    [Fact]
    public async Task StartAsync_RunsSafeUpdateSequence()
    {
        var root = CreateTempRepo();
        var releaseDir = Path.Combine(root, "bin", "Release");
        Directory.CreateDirectory(releaseDir);

        var serverRunning = true;
        var actions = new List<string>();
        var runner = new RecordingRunner(_ => Task.FromResult(new UpdateCommandResult(0, "")));
        var service = new UpdateService(
            isServerRunning: () => serverRunning,
            saveServer: () => actions.Add("save"),
            stopServer: () =>
            {
                actions.Add("stop");
                serverRunning = false;
            },
            startServer: () =>
            {
                actions.Add("start");
                serverRunning = true;
            },
            hostBaseDir: releaseDir,
            runner: runner);

        var started = await service.StartAsync();
        await WaitUntilCompleted(service);

        Assert.True(started.Started);
        Assert.Equal(["save", "stop", "start"], actions);
        Assert.Collection(
            runner.Commands,
            c =>
            {
                Assert.Equal("git", c.FileName);
                Assert.Equal("status --porcelain", c.Arguments);
            },
            c =>
            {
                Assert.Equal("git", c.FileName);
                Assert.Equal("pull --ff-only", c.Arguments);
            },
            c =>
            {
                Assert.Equal("powershell", c.FileName);
                Assert.Contains("-ServerOnly", c.Arguments);
            });

        var status = service.GetStatus();
        Assert.Equal("Completed", status.State);
        Assert.True(status.RequiresHostRestart);
    }

    private static string CreateTempRepo()
    {
        var root = Path.Combine(Path.GetTempPath(), "spherenet-update-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        return root;
    }

    private static async Task WaitUntilCompleted(UpdateService service)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (service.GetStatus().IsRunning)
            await Task.Delay(50, cts.Token);
    }

    private sealed class RecordingRunner(Func<UpdateCommand, Task<UpdateCommandResult>> handler) : IUpdateCommandRunner
    {
        public List<UpdateCommand> Commands { get; } = [];

        public async Task<UpdateCommandResult> RunAsync(UpdateCommand command)
        {
            Commands.Add(command);
            return await handler(command);
        }
    }
}
