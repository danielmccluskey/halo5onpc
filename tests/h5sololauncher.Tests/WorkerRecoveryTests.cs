using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Storage;
using H5SoloLauncher.Services;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class WorkerRecoveryTests
{
    [Fact]
    public async Task KilledWorkerLeavesCheckpointsThatAnotherWorkerCanResume()
    {
        using var f = new IndexFixture(); CacheFolders.Select(f.Destination, f.Root, null);
        for (var i = 0; i < 1000; i++) f.Write($"__cms__/recovery/{i:D4}.mapinfo", [1, 2, 3]);
        var executable = Environment.GetEnvironmentVariable("H5SOLO_TEST_WORKER") ?? Path.Combine(AppContext.BaseDirectory, "h5sololauncher.Worker.exe");
        var name = "h5solo-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(name);
        using var child = Process.Start(start)!;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await pipe.WaitForConnectionAsync(timeout.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            Assert.Equal("hello", JsonSerializer.Deserialize<WorkerMessage>((await reader.ReadLineAsync(timeout.Token))!)!.Type);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new WorkerMessage("index", Request: new(f.Root, f.Cache))));
            while (true)
            {
                var message = JsonSerializer.Deserialize<WorkerMessage>((await reader.ReadLineAsync(timeout.Token))!)!;
                Assert.NotEqual("result", message.Type);
                if (message.Progress?.Completed > 0) break;
            }
            child.Kill(); await child.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
        }
        var reused = 0;
        var result = await new IndexWorkerClient(executable).RunAsync(new(f.Root, f.Cache),
            new Callback(p => reused = Math.Max(reused, p.Reused)), default);
        Assert.Equal("Indexed", result.State); Assert.True(reused > 0);
        Assert.Equal(1007, result.Summary!.Files);
    }

    private sealed class Callback(Action<IndexProgress> callback) : IProgress<IndexProgress>
    { public void Report(IndexProgress value) => callback(value); }
}
