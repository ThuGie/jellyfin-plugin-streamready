using System.Diagnostics;
using System.Reflection;
using Jellyfin.Plugin.StreamReady.Configuration;
using Jellyfin.Plugin.StreamReady.Models;
using Jellyfin.Plugin.StreamReady.Services;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

var schedule = new PluginConfiguration { EncodeWindowEnabled = true, EncodeWindowDays = "5" };
Check(EncodeWindow.IsOpen(schedule, new DateTime(2026, 11, 20, 22, 0, 0)), "Friday night opens on time");
Check(EncodeWindow.IsOpen(schedule, new DateTime(2026, 11, 21, 1, 0, 0)), "Saturday morning belongs to Friday");
Check(!EncodeWindow.IsOpen(schedule, new DateTime(2026, 11, 20, 1, 0, 0)), "Friday morning belongs to Thursday");
Check(!EncodeWindow.IsOpen(schedule, new DateTime(2026, 11, 21, 6, 0, 0)), "The end is exclusive");
schedule.EncodeWindowStart = "bad";
Check(!EncodeWindow.IsOpen(schedule), "Invalid schedules fail closed");

var config = new PluginConfiguration { HardwareAccel = "Software" };
Check(EncodePlanner.Decide(false, true, true, "vp9", "aac", config) == EncodeAction.Full,
    "Video copy eligibility takes precedence over the audio-only decision");

var bitmapSubtitle = new ProbedStream(2, "subtitle", "hdmv_pgs_subtitle", false, false, true, "eng");
var textSubtitle = bitmapSubtitle with { Codec = "subrip" };
Check(EncodePlanner.SelectOutputContainer(config, [textSubtitle]) == "mp4", "Text subtitles can stay in MP4");
try
{
    EncodePlanner.SelectOutputContainer(config, [bitmapSubtitle]);
    throw new Exception("Bitmap subtitles should require an allowed MKV container");
}
catch (InvalidOperationException ex)
{
    Check(ex.Message.Contains("require MKV"), "Explain the conflicting subtitle policy");
}

config.AllowedContainers = "mp4,mkv";
Check(EncodePlanner.SelectOutputContainer(config, [bitmapSubtitle]) == "mkv", "Try MKV for bitmap subtitles");

var scanner = new LibraryScanner(null!, new CompatibilityAnalyzer(), null!, NullLogger<LibraryScanner>.Instance);
var skippedScan = await scanner.ScanAsync(null, CancellationToken.None);
Check(skippedScan.Status == "disabled" && skippedScan.Found is null && scanner.LastScanUtc is null,
    "A disabled scan returns a skipped outcome without claiming zero matches");

var snapshots = Directory.CreateTempSubdirectory("streamready snapshots ");
try
{
    var path = Path.Combine(snapshots.FullName, "jobs.json");
    File.WriteAllText(path, """{"Queue":[],"Candidates":[],"Processed":[],"Replacements":[]}""");
    var store = new JobStore(NullLogger<JobStore>.Instance, path);
    var candidate = new CandidateRecord { Id = "fixture", ItemId = "fixture" };
    store.UpsertCandidates([candidate]);
    var job = store.Enqueue(candidate);
    Check(File.Exists(path + ".legacy"), "Migration preserves original jobs.json");
    Check(store.DequeueNext()?.Id == job.Id, "Claim the queued job");
    Check(!store.Skip(job.Id), "Running jobs cannot be skipped");
    Check(store.Cancel(job.Id), "Running jobs can be cancelled");
    Check(store.GetJob(job.Id)?.Status == JobStatus.Cancelled, "Cancellation is durable");
    File.WriteAllText(path, "invalid");
    var recovered = new JobStore(NullLogger<JobStore>.Instance, path);
    Check(recovered.IsHealthy && recovered.GetJob(job.Id) is not null, "Recover from the previous snapshot");
    File.WriteAllText(path + ".previous", "invalid");
    var blocked = new JobStore(NullLogger<JobStore>.Instance, path);
    Check(!blocked.IsHealthy, "Block operations when both snapshots are invalid");
    try
    {
        blocked.Enqueue(candidate);
        throw new Exception("An invalid store must reject queue mutations");
    }
    catch (InvalidOperationException)
    {
        Check(blocked.GetQueue().Count == 0, "A failed write must not publish in-memory state");
    }
}
finally
{
    snapshots.Delete(recursive: true);
}

var media = Directory.CreateTempSubdirectory("streamready recovery ");
try
{
    var original = Path.Combine(media.FullName, "film.mkv");
    var stage = Path.Combine(media.FullName, "stage.mp4");
    var destination = Path.Combine(media.FullName, "film.mp4");
    await File.WriteAllTextAsync(original, "original contents");
    await File.WriteAllTextAsync(stage, "verified output contents");
    await File.WriteAllTextAsync(destination, "existing unrelated contents");
    var store = new JobStore(NullLogger<JobStore>.Instance, Path.Combine(media.FullName, "jobs.json"));
    var replacement = new ReplacementService(null!, null!, null!, NullLogger<ReplacementService>.Instance,
        store, null!, media.FullName);
    var item = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Name = "Fixture" };
    var candidate = new CandidateRecord { Id = item.Id.ToString("N"), ItemId = item.Id.ToString("N") };
    var job = store.Enqueue(candidate);
    var policy = new PluginConfiguration { ReplacementPolicy = "Replace" };

    try
    {
        await replacement.CommitAsync(item, job.Id, original, stage, policy, () => { }, _ => { }, CancellationToken.None);
        throw new Exception("An unrelated destination must block promotion");
    }
    catch (IOException)
    {
        Check(File.ReadAllText(destination) == "existing unrelated contents" && File.Exists(original),
            "A destination collision leaves both files intact");
    }

    File.Delete(destination);
    try
    {
        await replacement.CommitAsync(item, job.Id, original, stage, policy, () => { },
            _ => throw new IOException("Injected interruption before history publication"), CancellationToken.None);
        throw new Exception("The history fault was not exercised");
    }
    catch (IOException ex) when (ex.Message.StartsWith("Injected", StringComparison.Ordinal))
    {
        Check(replacement.PendingOperation is not null && File.Exists(destination),
            "Interrupted publication leaves verified output and a durable journal");
    }

    await replacement.RecoverAsync(CancellationToken.None);
    Check(replacement.PendingOperation is null && store.GetJob(job.Id)?.Status == JobStatus.Done,
        "Startup publishes missing history for a journaled promotion");
    Check(media.GetFiles("*.streamready-rollback").Any(file => File.ReadAllText(file.FullName) == "original contents"),
        "Startup retains the original media from an interrupted promotion");
}
finally
{
    media.Delete(recursive: true);
}

if (args.Contains("--ffmpeg"))
{
    var work = Directory.CreateTempSubdirectory("streamready fixture ");
    try
    {
        var input = Path.Combine(work.FullName, "two tracks.mkv");
        var output = Path.Combine(work.FullName, "encoded file.mp4");
        var subtitlePath = Path.Combine(work.FullName, "caption.srt");
        await File.WriteAllTextAsync(subtitlePath, "1\n00:00:00,100 --> 00:00:01,000\nStreamReady fixture\n");
        using var generator = new Process();
        generator.StartInfo.FileName = "ffmpeg";
        generator.StartInfo.UseShellExecute = false;
        foreach (var token in new[] { "-v", "error", "-f", "lavfi", "-i", "color=s=96x64:r=10",
            "-f", "lavfi", "-i", "sine=frequency=440", "-f", "lavfi", "-i", "sine=frequency=880",
            "-i", subtitlePath, "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:s",
            "-metadata:s:s:0", "language=eng", "-disposition:s:0", "forced", "-disposition:a:0", "0",
            "-disposition:a:1", "default", "-t", "2", "-c:v", "libx264", "-c:a", "aac", input })
        {
            generator.StartInfo.ArgumentList.Add(token);
        }

        generator.Start();
        await generator.WaitForExitAsync();
        Check(generator.ExitCode == 0, "Synthetic fixture creation");

        var encoder = DispatchProxy.Create<IMediaEncoder, FixtureEncoder>();
        var runner = new FfmpegRunner(encoder, null!, NullLogger<FfmpegRunner>.Instance);
        var source = await runner.ProbeMediaAsync(input, CancellationToken.None);
        Check(source.IsValid && source.Streams.Count(s => s.Type == "audio") == 2, "Probe reads both audio streams");
        config.KeepAllAudioAndSubtitles = false;
        var plan = runner.BuildEncodePlan(input, output, EncodeAction.Remux, config, source: source);
        Check(plan.Args.Contains("-map 0:2"), "The default audio stream is selected");
        await runner.EncodeAsync(input, output, EncodeAction.Remux, config, source.Duration,
            new Progress<EncodeProgressUpdate>(), CancellationToken.None, source: source);
        var encoded = await runner.ProbeMediaAsync(output, CancellationToken.None);
        await runner.VerifyDecodeAsync(output, encoded.Duration, CancellationToken.None);
        Check(encoded.IsValid && encoded.Streams.Count(s => s.Type == "audio") == 1, "Remux preserves selected audio");

        config.KeepAllAudioAndSubtitles = true;
        var fullOutput = Path.Combine(work.FullName, "full encode.mp4");
        await runner.EncodeAsync(input, fullOutput, EncodeAction.Full, config, source.Duration,
            new Progress<EncodeProgressUpdate>(), CancellationToken.None, source: source, videoBudget: 500_000);
        var full = await runner.ProbeMediaAsync(fullOutput, CancellationToken.None);
        Check(full.IsValid && full.Streams.Count(s => s.Type == "audio") == 2, "Full encode preserves both audio tracks");
        Check(full.Streams.Any(s => s.Type == "subtitle" && s.Codec == "mov_text" && s.Language == "eng" && s.Forced),
            "MP4 preserves a text subtitle's language and forced disposition");
    }
    finally
    {
        work.Delete(recursive: true);
    }
}

Console.WriteLine("StreamReady regression checks passed.");

public class FixtureEncoder : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
    {
        "get_EncoderPath" => "ffmpeg",
        "get_ProbePath" => "ffprobe",
        _ => throw new NotSupportedException(targetMethod?.Name)
    };
}
