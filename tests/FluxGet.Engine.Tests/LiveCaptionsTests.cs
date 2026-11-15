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
