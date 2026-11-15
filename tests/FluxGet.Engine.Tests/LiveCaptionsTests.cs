using FluxGet.Engine.Media;

namespace FluxGet.Engine.Tests;

public sealed class LiveCaptionsTests
{
    [Fact]
    public async Task Captions_appear_for_the_part_being_played()
    {
        var samples = Path.Combine(Path.GetTempPath(), "fluxget-player-samples", "speech-test.mp4");
        var dataDir = Environment.GetEnvironmentVariable("FLUXGET_TEST_MODELS_DATA");
        var tools = ToolsDirectory();
        if (!File.Exists(samples) || dataDir is null || !File.Exists(Path.Combine(dataDir, "models", "ggml-base.bin")) || tools is null) return;

        var locator = new ToolLocator(tools);
        var transcriber = new Transcriber(new EngineOptions { DataDirectory = dataDir }, locator);
        using var live = new LiveCaptions(transcriber, locator, Path.Combine(Path.GetTempPath(), "fluxget-tests", "live"), samples, null, "base", "auto", false, 0, 15_000);
        var until = Environment.TickCount64 + 60_000;
        while (live.Count == 0 && live.Error is null && live.Status != "Done" && Environment.TickCount64 < until)
        {
            live.Update(1000, 15_000);
            await Task.Delay(200);
        }
        Assert.Null(live.Error);
        Assert.True(live.Count > 0, $"status {live.Status}");
        Assert.NotNull(live.TextAt(live.Captions()[0].StartMs + 10));
    }

    private static string? ToolsDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "bin");
            if (File.Exists(Path.Combine(candidate, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"))) return candidate;
        }
        return null;
    }
}

public sealed class WhisperBenchmark
{
    [Fact]
    public async Task Transcribe_three_minutes_of_speech()
    {
        var mode = Environment.GetEnvironmentVariable("FLUXGET_BENCH_WHISPER");
        var dataDir = Environment.GetEnvironmentVariable("FLUXGET_TEST_MODELS_DATA");
        var wav = Path.Combine(Path.GetTempPath(), "fluxget-player-samples", "speech-3min.wav");
        if (mode is null || dataDir is null || !File.Exists(wav)) return;
        Transcriber.UseGpu(mode == "gpu");
        var model = Environment.GetEnvironmentVariable("FLUXGET_BENCH_MODEL") ?? "ggml-base.bin";
        var threads = int.TryParse(Environment.GetEnvironmentVariable("FLUXGET_BENCH_THREADS"), out var t) ? t : Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var factory = Whisper.net.WhisperFactory.FromPath(Path.Combine(dataDir, "models", model), new Whisper.net.WhisperFactoryOptions { UseGpu = mode == "gpu", GpuDevice = int.TryParse(Environment.GetEnvironmentVariable("FLUXGET_BENCH_DEVICE"), out var dev) ? dev : 0 });
        var load = sw.ElapsedMilliseconds;
        await using var processor = factory.CreateBuilder().WithLanguage("en").WithThreads(threads).Build();
        await using var audio = File.OpenRead(wav);
        var lines = 0;
        await foreach (var _ in processor.ProcessAsync(audio)) lines++;
        Assert.Fail($"BENCH {mode} dev {Environment.GetEnvironmentVariable("FLUXGET_BENCH_DEVICE")} {model} threads {threads} runtime {Transcriber.LoadedRuntime}: load {load} ms, 180 s audio in {sw.ElapsedMilliseconds - load} ms, {lines} lines");
    }
}
