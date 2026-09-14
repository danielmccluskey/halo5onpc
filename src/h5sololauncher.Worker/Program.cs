using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Storage;
using H5SoloLauncher.Core.Planning;
using H5SoloLauncher.Core.Forge;
using H5SoloLauncher.Core.Preparation;
using H5SoloLauncher.Worker;

try
{
    if(args.Length==4 && args[0] is "--check-cache" or "--play-cache")
    {
        var cache=H5SoloLauncher.Core.Runtime.PlayableCache.Open(args[1],args[2],args[3]);
        if(args[0]=="--check-cache") { var runtime=H5SoloLauncher.Core.Runtime.PlayableCache.Resolve(cache);Console.WriteLine(JsonSerializer.Serialize(new{State="Ready",cache.Id,cache.Root,cache.Manifest.Language,Files=cache.Manifest.Files.Length,DumpRequired=false,runtime.ContentId}));return 0; }
        using var stop=new CancellationTokenSource();Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
        var result=CampaignPlayback.Run(new(new("",cache.Root,args[2],args[3],cache.Manifest.Language),cache.Id),new CallbackProgress(p=>Console.WriteLine(JsonSerializer.Serialize(p))),stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(result));return result.State=="Ready"?0:1;
    }
    if(args.Length==3 && args[0] is "--show-forge" or "--forge-window" or "--windowed-forge")
    {Console.WriteLine(JsonSerializer.Serialize(ForgePresentation.Run(args[1],args[2],args[0]=="--show-forge",default,args[0]=="--windowed-forge")));return 0;}
    if(args.Length is 5 or 6 && args[0]=="--play")
    {
        using var stop=new CancellationTokenSource();Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
        var locations=new PrepareRequest(args[1],args[2],args[3],args[4],args.Length==6?args[5]:"English(US)");
        var id=H5SoloLauncher.Core.Runtime.PlayableCache.Open(locations.CacheRoot,locations.ForgeRoot,locations.PackageFullName).Id;
        var last=DateTime.MinValue;var result=CampaignPlayback.Run(new(locations,id),new CallbackProgress(p=>{if((DateTime.UtcNow-last).TotalSeconds<5)return;last=DateTime.UtcNow;Console.WriteLine(JsonSerializer.Serialize(p));}),stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(result));return result.State=="Ready"?0:1;
    }
    if(args.Length==8 && args[0]=="--prepare-completion")
    {
        var result=H5SoloLauncher.Core.Runtime.CompletionConfiguration.Prepare(new(new(args[1],args[2],args[3],args[4],args[5]),args[6],args[7]),new CallbackProgress(p=>Console.WriteLine(JsonSerializer.Serialize(p))),default);
        Console.WriteLine(JsonSerializer.Serialize(result));return result.State=="Prepared"?0:1;
    }
    if(args.Length==5 && args[0] is "--arm-completion" or "--completion-status" or "--test-ending")
    {
        Console.WriteLine(JsonSerializer.Serialize(CampaignCompletionRuntime.Run(args[1],args[2],args[3],args[4],args[0]=="--arm-completion"?1u:args[0]=="--test-ending"?2u:0u,default)));return 0;
    }
    if(args.Length==3 && args[0] is "--arm-controls" or "--controls-status")
    {
        Console.WriteLine(JsonSerializer.Serialize(CampaignControlsRuntime.Run(args[1],args[2],args[0]=="--arm-controls",default)));return 0;
    }
    if(args.Length==5 && args[0] is "--arm-movie" or "--movie-status")
    {
        Console.WriteLine(JsonSerializer.Serialize(CampaignMovieRuntime.Run(args[1],args[2],args[3],args[4],args[0]=="--arm-movie",default)));return 0;
    }
    if(args.Length==6 && args[0]=="--prepare-movie")
    {
        var last=DateTime.MinValue;var result=H5SoloLauncher.Core.Runtime.CampaignMovie.Prepare(new(args[1],args[2],args[3],args[4],args[5]),new CallbackProgress(p=>{if((DateTime.UtcNow-last).TotalSeconds<5)return;last=DateTime.UtcNow;Console.WriteLine(JsonSerializer.Serialize(p));}),default);
        Console.WriteLine(JsonSerializer.Serialize(result));return result.State=="Prepared"?0:1;
    }
    if(args.Length==3 && args[0] is "--register-audio" or "--audio-status")
    {
        Console.WriteLine(JsonSerializer.Serialize(CampaignAudioRuntime.Run(args[1],args[2],args[0]=="--register-audio",default)));return 0;
    }
    if(args.Length==5 && args[0] is "--arm-ui-residency" or "--ui-residency-status")
    {
        Console.WriteLine(JsonSerializer.Serialize(CampaignUiRuntime.Run(args[1],args[2],args[3],args[4],args[0]=="--arm-ui-residency",default)));return 0;
    }
    if(args.Length==3 && args[0] is "--arm-renderer" or "--renderer-status")
    {
        Console.WriteLine(JsonSerializer.Serialize(CampaignRendererRuntime.Run(args[1],args[2],args[0]=="--arm-renderer",default)));return 0;
    }
    if(args.Length==9 && args[0]=="--prepare-ui-residency")
    {
        var last=DateTime.MinValue;var result=H5SoloLauncher.Core.Runtime.UiResidency.Run(new(new(new(args[1],args[2],args[3],args[4],args[5]),args[6],args[7]),args[8]),new CallbackProgress(p=>{if((DateTime.UtcNow-last).TotalSeconds<5)return;last=DateTime.UtcNow;Console.WriteLine(JsonSerializer.Serialize(p));}),default);
        Console.WriteLine(JsonSerializer.Serialize(result));return result.State=="Prepared"?0:1;
    }
    if(args.Length==8 && args[0]=="--prepare-menu-config")
    {
        var result=H5SoloLauncher.Core.Runtime.MenuConfiguration.Run(new(new(args[1],args[2],args[3],args[4],args[5]),args[6],args[7]),default);
        Console.WriteLine(JsonSerializer.Serialize(result));return result.State=="Prepared"?0:1;
    }
    if(args.Length==5 && args[0] is "--activate-menu" or "--open-solo")
    {
        var last=DateTime.MinValue;CampaignMenuRuntime.Activate(args[1],args[2],args[3],args[4],new CallbackProgress(p=>{if((DateTime.UtcNow-last).TotalSeconds<5)return;last=DateTime.UtcNow;Console.WriteLine(JsonSerializer.Serialize(p));}),default,args[0]=="--open-solo");
        Console.WriteLine("Campaign menu active.");return 0;
    }
    if(args.Length==7 && args[0]=="--prepare-menu-artwork")
    {
        using var stop=new CancellationTokenSource();Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
        var result=H5SoloLauncher.Core.Runtime.MenuArtwork.Run(new(new(args[1],args[2],args[3],args[4],args[5]),args[6]),new CallbackProgress(p=>Console.WriteLine(JsonSerializer.Serialize(p))),stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(result));return result.State=="Prepared"?0:1;
    }
    if(args.Length==7 && args[0]=="--prepare-menu-sources")
    {
        using var stop=new CancellationTokenSource();Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
        var result=H5SoloLauncher.Core.Runtime.MenuSources.Run(new(new(args[1],args[2],args[3],args[4],args[5]),args[6]),new CallbackProgress(p=>Console.WriteLine(JsonSerializer.Serialize(p))),stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(result));return result.State=="Prepared"?0:1;
    }
    if(args.Length is 8 or 9 && args[0]=="--activate-content")
    {
        using var stop=new CancellationTokenSource();Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};var last=DateTime.MinValue;
        var result=H5SoloLauncher.Core.Runtime.CampaignContent.Run(new(new(args[1],args[2],args[3],args[4],args[5]),args[6],args[7],args.Length==9?args[8]:null),()=>new CampaignContentRuntime(args[3],args[4]),
            new CallbackProgress(p=>{if((DateTime.UtcNow-last).TotalSeconds<5)return;last=DateTime.UtcNow;Console.WriteLine(JsonSerializer.Serialize(p));}),stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(result));return result.State=="Active"?0:1;
    }
    if (args.Length == 7 && args[0] == "--register-campaign")
    {
        using var stop = new CancellationTokenSource(); Console.CancelKeyPress += (_,e)=>{e.Cancel=true;stop.Cancel();};
        var last=DateTime.MinValue;
        var result=H5SoloLauncher.Core.Runtime.CampaignMetadata.Run(new(new(args[1],args[2],args[3],args[4],args[5]),args[6]),
            ()=>new CampaignRegistry(args[3],args[4],args[2]),new CallbackProgress(p=>{if((DateTime.UtcNow-last).TotalSeconds<5)return;last=DateTime.UtcNow;Console.WriteLine(JsonSerializer.Serialize(p));}),stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(result)); return result.State=="Registered"?0:1;
    }
    if (args.Length == 3 && args[0] == "--test-auto-continue")
    {
        using var stop = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var last = DateTime.MinValue;
        var entered = TitleAutomation.Run(new(args[1], args[2]), new CallbackProgress(p =>
        {
            if ((DateTime.UtcNow - last).TotalSeconds < 5) return;
            last = DateTime.UtcNow; Console.WriteLine(JsonSerializer.Serialize(p));
        }), stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(entered)); return entered.State == "MainMenu" ? 0 : 1;
    }
    if (args.Length == 8 && args[0] == "--prepare-audio")
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var last = DateTime.MinValue;
        var prepared = H5SoloLauncher.Core.Conversion.AudioPreparation.Run(new(new(new(args[1], args[2], args[3], args[4], args[5]), args[6]), args[7]),
            () => new ForgeFiles(new(args[1], args[2], args[3], args[4], args[5]), stop.Token), new CallbackProgress(p =>
            {
                if ((DateTime.UtcNow - last).TotalSeconds < 5) return;
                last = DateTime.UtcNow; Console.WriteLine(JsonSerializer.Serialize(p));
            }), stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(prepared)); return prepared.State == "Prepared" ? 0 : 1;
    }
    if (args.Length == 9 && args[0] == "--assemble-modules")
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var last = DateTime.MinValue;
        var assembled = H5SoloLauncher.Core.Conversion.ModuleAssembly.Run(new(new(new(new(args[1], args[2], args[3], args[4], args[5]), args[6]), args[7]), args[8]), new CallbackProgress(p =>
        {
            if ((DateTime.UtcNow - last).TotalSeconds < 5) return;
            last = DateTime.UtcNow; Console.WriteLine(JsonSerializer.Serialize(p));
        }), stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(assembled)); return assembled.State == "Assembled" ? 0 : 1;
    }
    if (args.Length == 8 && args[0] == "--prepare-shaders")
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var last = DateTime.MinValue;
        var prepared = H5SoloLauncher.Core.Conversion.ShaderPreparation.Run(new(new(new(args[1], args[2], args[3], args[4], args[5]), args[6]), args[7]), () => new ShaderValidator(), new CallbackProgress(p =>
        {
            if ((DateTime.UtcNow - last).TotalSeconds < 5) return;
            last = DateTime.UtcNow; Console.WriteLine(JsonSerializer.Serialize(p));
        }), stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(prepared)); return prepared.State == "Prepared" ? 0 : 1;
    }
    if (args.Length == 7 && args[0] == "--convert-assets")
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var last = DateTime.MinValue;
        var converted = H5SoloLauncher.Core.Conversion.ConvertedAssets.Run(new(new(args[1], args[2], args[3], args[4], args[5]), args[6]), new CallbackProgress(p =>
        {
            if ((DateTime.UtcNow - last).TotalSeconds < 5) return;
            last = DateTime.UtcNow; Console.WriteLine(JsonSerializer.Serialize(p));
        }), stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(converted)); return converted.State == "Converted" ? 0 : 1;
    }
    if (args.Length == 7 && args[0] == "--audit-conversion")
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var last = DateTime.MinValue;
        var audit = H5SoloLauncher.Core.Conversion.ConversionAudit.Run(new(new(args[1], args[2], args[3], args[4], args[5]), args[6]), new CallbackProgress(p =>
        {
            if ((DateTime.UtcNow - last).TotalSeconds < 5) return;
            last = DateTime.UtcNow; Console.WriteLine(JsonSerializer.Serialize(p));
        }), stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(audit)); return audit.State == "Audited" ? 0 : 1;
    }
    if (args.Length is 5 or 6 && args[0] == "--prepare")
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var last = DateTime.MinValue;
        var prepared = new PreparationCoordinator(new PreparationServices()).Run(new(args[1], args[2], args[3], args[4], args.Length == 6 ? args[5] : "English(US)"), new CallbackProgress(p =>
        {
            if ((DateTime.UtcNow - last).TotalSeconds < 5) return;
            last = DateTime.UtcNow; Console.WriteLine(JsonSerializer.Serialize(p));
        }), stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(prepared)); return prepared.State == "Failed" ? 1 : 0;
    }
    if (args.Length == 8 && args[0] == "--effective-plan")
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var last = DateTime.MinValue;
        var effective = EffectivePlanBuilder.Run(new(new(args[1], args[2], args[3], args[4], args[5]), args[6], args[7]), new CallbackProgress(p =>
        {
            if ((DateTime.UtcNow - last).TotalSeconds < 5) return;
            last = DateTime.UtcNow; Console.WriteLine(JsonSerializer.Serialize(p));
        }), stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(effective)); return effective.State == "Planned" ? 0 : 1;
    }
    if (args.Length == 6 && args[0] == "--native-modules")
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var last = DateTime.MinValue;
        var native = NativeModuleCache.Run(new(args[1], args[2], args[3], args[4], args[5]),
            () => new ForgeFiles(new(args[1], args[2], args[3], args[4], args[5]), stop.Token), new CallbackProgress(p =>
            {
                if ((DateTime.UtcNow - last).TotalSeconds < 5) return;
                last = DateTime.UtcNow; Console.WriteLine(JsonSerializer.Serialize(p));
            }), stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(native)); return native.State == "Cached" ? 0 : 1;
    }
    if (args.Length == 3 && args[0] == "--launch")
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var launched = WindowsForgeLaunch.Run(new(args[1], args[2]), new CallbackProgress(p => Console.WriteLine(JsonSerializer.Serialize(p))), stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(launched)); return launched.State == "Running" ? 0 : 1;
    }
    if (args.Length == 6 && args[0] == "--native-layouts")
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var layouts = NativePreparation.CaptureLayouts(new(args[1], args[2], args[3], args[4], args[5]),
            () => new ForgeMemory(args[3], args[4]), new CallbackProgress(p => Console.WriteLine(JsonSerializer.Serialize(p))), stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(layouts)); return layouts.State == "Captured" ? 0 : 1;
    }
    if (args.Length == 6 && args[0] == "--inputs")
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var last = DateTime.MinValue;
        var cached = new InputCacheBuilder().Run(new(args[1], args[2], args[3], args[4], args[5]), new CallbackProgress(p =>
        {
            if ((DateTime.UtcNow - last).TotalSeconds < 5) return;
            last = DateTime.UtcNow; Console.WriteLine(JsonSerializer.Serialize(p));
        }), stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(cached)); return cached.State == "Failed" ? 1 : 0;
    }
    if (args.Length == 3 && args[0] == "--launch-check")
    {
        var inspected = WindowsForgeLaunch.Inspect(new(args[1], args[2]));
        Console.WriteLine(JsonSerializer.Serialize(inspected)); return inspected.State == "Failed" ? 1 : 0;
    }
    if (args.Length == 6 && args[0] == "--forge")
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var input = new ForgeRequest(args[1], args[2], args[3], args[4], args[5]);
        var last = DateTime.MinValue;
        var prepared = Prepare(input, new CallbackProgress(p =>
        {
            if ((DateTime.UtcNow - last).TotalSeconds < 5) return;
            last = DateTime.UtcNow; Console.WriteLine(JsonSerializer.Serialize(p));
        }), stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(prepared)); return prepared.State == "Failed" ? 1 : 0;
    }
    if (args.Length is 3 or 4 && args[0] == "--plan")
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var last = DateTime.MinValue;
        var cliPlan = new BuildPlanner().Run(new(args[1], args[2], Language: args.Length == 4 ? args[3] : "English(US)"), new CallbackProgress(p =>
        {
            if ((DateTime.UtcNow - last).TotalSeconds < 5) return;
            last = DateTime.UtcNow; Console.WriteLine(JsonSerializer.Serialize(p));
        }), stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(cliPlan)); return cliPlan.State == "Failed" ? 1 : 0;
    }
    if (args.Length == 3 && args[0] == "--index")
    {
        // Developer integration entry point; the WPF app uses the typed pipe protocol.
        var source = SourceDiscovery.Describe(args[1]);
        var cache = CacheFolders.Select(args[2], source.Root, null);
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var last = DateTime.MinValue;
        var result = new SourceIndexer().Run(new(source.Root, cache.Root), new CallbackProgress(p =>
        {
            if ((DateTime.UtcNow - last).TotalSeconds < 5 && p.Completed != p.Total) return;
            last = DateTime.UtcNow; Console.WriteLine(JsonSerializer.Serialize(p));
        }), stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(result));
        return result.State == "Failed" ? 1 : 0;
    }
    if (args.Length != 2 || args[0] != "--pipe" || !args[1].StartsWith("h5solo-", StringComparison.Ordinal) || args[1].Length > 100)
        throw new ArgumentException("Expected a launcher pipe, or --index <dump> <cache parent> for integration testing.");

    using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await pipe.ConnectAsync(15000);
    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
    using var cancellation = new CancellationTokenSource();
    void Send(WorkerMessage message) { lock (writer) writer.WriteLine(JsonSerializer.Serialize(message)); }
    Send(new("hello"));
    using var handshake = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var requestLine = await reader.ReadLineAsync(handshake.Token);
    if (requestLine is null || requestLine.Length > 64 * 1024) throw new IOException("Missing or oversized index request.");
    var request = JsonSerializer.Deserialize<WorkerMessage>(requestLine);
    if (request?.Version != 1 || !((request.Type == "index" && request.Request is not null) || (request.Type == "plan" && request.PlanRequest is not null) || (request.Type == "forge" && request.ForgeRequest is not null) || (request.Type == "launch" && request.LaunchRequest is not null) || (request.Type == "inputs" && request.InputRequest is not null) || (request.Type == "prepare" && request.PrepareRequest is not null) || (request.Type=="play" && request.PlayRequest is not null) || (request.Type=="watch" && request.WatchRequest is not null)))
        throw new IOException("Incompatible worker request.");
    var control = Task.Run(async () =>
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellation.Token);
                if (line is null || line.Length > 64 * 1024) break;
                if (JsonSerializer.Deserialize<WorkerMessage>(line)?.Type == "pause") break;
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or JsonException) { }
        finally { cancellation.Cancel(); }
    });
    var lastProgress = DateTime.MinValue;
    IndexProgress latest = new("Starting indexer", 0, 0);
    var heartbeat = Task.Run(async () =>
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(cancellation.Token))
                Send(new("progress", Progress: Volatile.Read(ref latest)));
        }
        catch (Exception e) when (e is IOException or OperationCanceledException) { cancellation.Cancel(); }
    });
    var progress = new CallbackProgress(p =>
    {
        Volatile.Write(ref latest, p);
        if ((DateTime.UtcNow - lastProgress).TotalMilliseconds < 100 && p.Completed != p.Total) return;
        lastProgress = DateTime.UtcNow;
        try { Send(new("progress", Progress: p)); }
        catch (IOException) { cancellation.Cancel(); }
    });
    var output = await Task.Run(() => request.Type == "index"
        ? new WorkerMessage("result", Result: new SourceIndexer().Run(request.Request!, progress, cancellation.Token))
        : request.Type == "plan" ? new WorkerMessage("result", PlanResult: new BuildPlanner().Run(request.PlanRequest!, progress, cancellation.Token))
        : request.Type == "forge" ? new WorkerMessage("result", ForgeResult: Prepare(request.ForgeRequest!, progress, cancellation.Token))
        : request.Type == "inputs" ? new WorkerMessage("result", InputResult: new InputCacheBuilder().Run(request.InputRequest!, progress, cancellation.Token))
        : request.Type == "prepare" ? new WorkerMessage("result", PreparationResult: new PreparationCoordinator(new PreparationServices()).Run(request.PrepareRequest!, progress, cancellation.Token))
        : request.Type=="watch"?new WorkerMessage("result",PlayResult:CampaignWatch.Run(request.WatchRequest!,progress,cancellation.Token))
        : request.Type=="play"?new WorkerMessage("result",PlayResult:CampaignPlayback.Run(request.PlayRequest!,progress,cancellation.Token))
        : new WorkerMessage("result", LaunchResult: WindowsForgeLaunch.Run(request.LaunchRequest!, progress, cancellation.Token)));
    cancellation.Cancel(); await heartbeat; await control;
    try { Send(output); } catch (IOException) { }
    return (output.Result?.State ?? output.PlanResult?.State ?? output.ForgeResult?.State ?? output.LaunchResult?.State ?? output.InputResult?.State ?? output.PreparationResult?.State ?? output.PlayResult?.State) == "Failed" ? 1 : 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

static ForgeResult Prepare(ForgeRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation)
{
    // The core validates the cache and immutable source plan before the factory can start any app.
    return new ForgePreparation().Run(request, () =>
    {
        var launched = WindowsForgeLaunch.Run(new(request.ForgeRoot, request.PackageFullName), progress, cancellation);
        if (launched.State == "Paused") throw new OperationCanceledException(cancellation);
        if (launched.State != "Running") throw new CacheException(launched.Code ?? "FORGE_LAUNCH_FAILED", launched.Message ?? "Forge could not start.");
        return new ForgeFiles(request, cancellation);
    }, progress, cancellation);
}

sealed class CallbackProgress(Action<IndexProgress> callback) : IProgress<IndexProgress>
{
    public void Report(IndexProgress value) => callback(value);
}
