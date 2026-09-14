using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Core.Runtime;

namespace H5SoloLauncher.Services;

public sealed class IndexWorkerClient(string? executable = null) : IIndexWorker, IPlanWorker, IForgeWorker, IForgeLaunchWorker, IInputWorker, IPreparationWorker, ICampaignPlayWorker, ICampaignWatchWorker
{
    public async Task<IndexResult> RunAsync(IndexRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation)
        => (await Execute(new("index", Request: request), progress, cancellation)).Result!;
    public async Task<PlanResult> PlanAsync(PlanRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation)
        => (await Execute(new("plan", PlanRequest: request), progress, cancellation)).PlanResult!;
    public async Task<ForgeResult> PrepareForgeAsync(ForgeRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation)
        => (await Execute(new("forge", ForgeRequest: request), progress, cancellation)).ForgeResult!;
    public async Task<ForgeLaunchResult> StartForgeAsync(ForgeLaunchRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation)
        => (await Execute(new("launch", LaunchRequest: request), progress, cancellation)).LaunchResult!;
    public async Task<InputResult> BuildInputsAsync(InputRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation)
        => (await Execute(new("inputs", InputRequest: request), progress, cancellation)).InputResult!;
    public async Task<PreparationResult> PrepareAsync(PrepareRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation)
        => (await Execute(new("prepare", PrepareRequest: request), progress, cancellation)).PreparationResult!;
    public async Task<CampaignPlayResult> PlayAsync(CampaignPlayRequest request,IProgress<IndexProgress> progress,CancellationToken cancellation)
        => (await Execute(new("play",PlayRequest:request),progress,cancellation)).PlayResult!;
    public async Task<CampaignPlayResult> WatchAsync(CampaignWatchRequest request,IProgress<IndexProgress> progress,CancellationToken cancellation)
        => (await Execute(new("watch",WatchRequest:request),progress,cancellation)).PlayResult!;

    private async Task<WorkerMessage> Execute(WorkerMessage request, IProgress<IndexProgress> progress, CancellationToken cancellation)
    {
        Process? process = null;
        var errors = new StringBuilder();
        Task? errorReader = null;
        try
        {
            var path = executable ?? Path.Combine(AppContext.BaseDirectory, "h5sololauncher.Worker.exe");
            if (!File.Exists(path)) throw new CacheException("WORKER_MISSING", "The index worker is missing. Keep all published launcher files together.");
            var name = "h5solo-" + Guid.NewGuid().ToString("N");
            using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(path)! };
            start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(name);
            process = Process.Start(start) ?? throw new IOException("Could not start the index worker.");
            var running = process;
            errorReader = Task.Run(async () =>
            {
                var buffer = new char[1024]; int count;
                while ((count = await running.StandardError.ReadAsync(buffer)) > 0)
                    if (errors.Length < 32768) errors.Append(buffer, 0, Math.Min(count, 32768 - errors.Length));
            });
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            startup.CancelAfter(TimeSpan.FromSeconds(20));
            var connected = pipe.WaitForConnectionAsync(startup.Token);
            if (await Task.WhenAny(connected, process.WaitForExitAsync()) != connected)
                throw new IOException($"The index worker exited during startup (exit {process.ExitCode}).");
            await connected;
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            var hello = await Read(reader, startup.Token);
            if (hello.Type != "hello") throw new IOException("Unexpected worker handshake.");
            await writer.WriteLineAsync(JsonSerializer.Serialize(request));
            using var pause = cancellation.Register(() =>
            {
                try { lock (writer) writer.WriteLine(JsonSerializer.Serialize(new WorkerMessage("pause"))); }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            });
            while (true)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                using var stopTimeout = cancellation.Register(() => timeout.CancelAfter(TimeSpan.FromSeconds(20)));
                var message = await Read(reader, timeout.Token);
                if (message.Type == "progress" && message.Progress is not null) progress.Report(message.Progress);
                else if (message.Type == "result" && (request.Type == "index" ? message.Result is not null : request.Type == "plan" ? message.PlanResult is not null : request.Type == "forge" ? message.ForgeResult is not null : request.Type == "inputs" ? message.InputResult is not null : request.Type == "prepare" ? message.PreparationResult is not null : request.Type is "play" or "watch"?message.PlayResult is not null:message.LaunchResult is not null)) return message;
                else throw new IOException("Unexpected worker message.");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { return Failure("Paused", null, "The worker stopped. Completed database checkpoints were kept; resume to continue.", null); }
        catch (Exception exception)
        {
            if (process?.HasExited == true && errorReader is not null) await DrainErrors(errorReader);
            return Failure("Failed", exception is CacheException known ? known.Code : "WORKER_FAILED",
                exception is CacheException ? exception.Message : "The worker stopped unexpectedly. Completed checkpoints were kept. Resume or copy the details.",
                exception + (process?.HasExited == true ? "\nWorker output:\n" + errors : string.Empty));
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                    catch (TimeoutException)
                    {
                        if (!process.HasExited) process.Kill();
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                    }
                    if (errorReader is not null) await DrainErrors(errorReader);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
                { /* An exit race must not replace the original, copyable worker result. */ }
                finally { process.Dispose(); }
            }
        }
    }

    private static WorkerMessage Failure(string state, string? code, string message, string? details) => new("result",
        Result: new(state, Code: code, Message: message, Details: details),
        PlanResult: new(state, Code: code, Message: message, Details: details),
        ForgeResult: new(state, Code: code, Message: message, Details: details),
        LaunchResult: new(state, Code: code, Message: message, Details: details),
        InputResult: new(state, Code: code, Message: message, Details: details),
        PreparationResult: new(state, "Worker", message, code, details),
        PlayResult:new(state,"Worker",message,Code:code,Details:details));

    private static async Task DrainErrors(Task reader)
    {
        try { await reader.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or TimeoutException) { }
    }

    private static async Task<WorkerMessage> Read(StreamReader reader, CancellationToken cancellation)
    {
        var line = await reader.ReadLineAsync(cancellation);
        if (line is null || line.Length > 64 * 1024) throw new IOException("The worker connection closed or returned an oversized message.");
        var message = JsonSerializer.Deserialize<WorkerMessage>(line);
        if (message is null || message.Version != 1) throw new IOException("The worker protocol version does not match the launcher.");
        return message;
    }
}
