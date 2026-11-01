using System.Text;
using YoutubeDownloadHelper.Core.Runner;

namespace YoutubeDownloadHelper.Core.Tests.Runner;

/// <summary>The progress parser against spike #33's raw captures of real yt-dlp (<c>docs/spikes/samples/</c>).</summary>
public sealed class ProgressTrackerTests
{
    private const string Title = "İzmir ılık 😀 東京 — Me at the zoo";

    [Fact]
    public void A_merge_downloads_two_formats_by_planned_size_then_processes_and_moves()
    {
        var (tracker, snapshots) = Replay("merge");

        Assert.Equal(
            [DownloadPhase.Downloading, DownloadPhase.Processing, DownloadPhase.Moving, DownloadPhase.Done],
            Phases(snapshots));
        Assert.Equal(["Merger", "Metadata", "EmbedThumbnail", "MoveFiles"], Postprocessors(snapshots));

        var downloading = snapshots.Where(s => s.Phase == DownloadPhase.Downloading).ToList();
        AssertRising(downloading);
        Assert.All(downloading, s => Assert.Equal(223779 + 252182, s.TotalBytes));
        Assert.Equal(1.0, downloading[^1].Fraction);

        // When the first (video) format finishes, the bar is at its share of the planned bytes.
        var videoDone = downloading.First(s => s.DownloadedBytes >= 223779);
        Assert.Equal(223779.0 / (223779 + 252182), videoDone.Fraction!.Value, 3);

        Assert.EndsWith(@"\merge\jawed\2005-04-24 - " + Title + ".mkv", tracker.FinalPath, StringComparison.Ordinal);
        Assert.False(tracker.AlreadyPresent);
        Assert.Equal(DownloadPhase.Done, tracker.Snapshot().Phase);
    }

    [Fact]
    public void A_single_format_uses_the_size_of_the_plan()
    {
        var (tracker, snapshots) = Replay("audio-mp3");

        Assert.Equal(["ExtractAudio", "Metadata", "EmbedThumbnail", "MoveFiles"], Postprocessors(snapshots));
        var downloading = snapshots.Where(s => s.Phase == DownloadPhase.Downloading).ToList();
        AssertRising(downloading);
        Assert.Equal(1.0, downloading[^1].Fraction);
        Assert.EndsWith(Title + ".mp3", tracker.FinalPath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hls")]
    [InlineData("hls-progress-delta")]
    public void Fragment_downloads_use_fragment_counts_and_equal_weights(string sample)
    {
        var (_, snapshots) = Replay(sample);

        var downloading = snapshots.Where(s => s.Phase == DownloadPhase.Downloading).ToList();
        AssertRising(downloading);
        Assert.All(downloading, s => Assert.InRange(s.Fraction!.Value, 0, 1));
        Assert.Equal(1.0, downloading[^1].Fraction);

        // Halfway is the switch from the video to the audio format: each weighs the same without planned sizes.
        Assert.Contains(downloading, s => s.Fraction is >= 0.5 and < 0.6);
    }

    [Fact]
    public void A_file_already_in_the_destination_is_reported_as_already_present()
    {
        var (tracker, snapshots) = Replay("already-exists");

        Assert.DoesNotContain(snapshots, s => s.Phase == DownloadPhase.Downloading);
        Assert.DoesNotContain("Merger", Postprocessors(snapshots));
        Assert.True(tracker.AlreadyPresent);
        Assert.NotNull(tracker.FinalPath);
    }

    [Fact]
    public void Raw_lines_are_classified_as_raw()
    {
        var tracker = new ProgressTracker();

        Assert.Equal(OutputLineKind.Raw, tracker.Read("ERROR: Unable to create directory"));
        Assert.Equal(OutputLineKind.Raw, tracker.Read("WARNING: webm doesn't support embedding a thumbnail, mkv will be used"));
        Assert.Equal(OutputLineKind.Raw, tracker.Read("YDH1 {not json"));
        Assert.Equal(OutputLineKind.Raw, tracker.Read("YDH1 [1, 2]"));
        Assert.Equal(OutputLineKind.Raw, tracker.Read("YDH1 {\"k\":\"other\"}"));
        Assert.Equal(OutputLineKind.Raw, tracker.Read("YDH2 {\"k\":\"dl\"}"));
        Assert.Equal(DownloadPhase.Preparing, tracker.Snapshot().Phase);
        Assert.Null(tracker.FinalPath);
    }

    [Fact]
    public void A_live_stream_has_no_fraction()
    {
        var tracker = new ProgressTracker();

        tracker.Read("""YDH1 {"k":"dl","p":{"status": "downloading", "downloaded_bytes": 65536, "speed": 262144.0, "elapsed": 0.25, "fragment_index": 1},"i":{"id": "x", "format_id": "95"}}""");

        var snapshot = tracker.Snapshot();
        Assert.Equal(DownloadPhase.Downloading, snapshot.Phase);
        Assert.Null(snapshot.Fraction);
        Assert.Null(snapshot.TotalBytes);
        Assert.Equal(65536, snapshot.DownloadedBytes);
        Assert.Equal(262144.0, snapshot.BytesPerSecond);
    }

    [Fact]
    public void Missing_and_odd_fields_are_tolerated()
    {
        var tracker = new ProgressTracker();

        Assert.Equal(OutputLineKind.Download, tracker.Read("""YDH1 {"k":"dl","p":{"status": "downloading", "downloaded_bytes": "12", "total_bytes": null},"i":{}}"""));
        Assert.Equal(OutputLineKind.Download, tracker.Read("""YDH1 {"k":"dl","p":{"status": "downloading", "downloaded_bytes": 50.0, "total_bytes": 100, "eta": 3},"i":{"format_id": "18"}}"""));
        Assert.Equal(OutputLineKind.PostProcess, tracker.Read("""YDH1 {"k":"pp","p":{"status": "finished"},"i":{}}"""));
        Assert.Equal(OutputLineKind.Moved, tracker.Read("""YDH1 {"k":"moved","i":{"id": "x"}}"""));

        Assert.Null(tracker.FinalPath);
        Assert.Equal(DownloadPhase.Done, tracker.Snapshot().Phase);
    }

    [Fact]
    public void The_eta_comes_from_the_remaining_bytes_and_the_speed()
    {
        var tracker = new ProgressTracker();
        tracker.Read("""YDH1 {"k":"plan","i":{"id": "x", "format_id": "1+2"},"rf":[{"format_id": "1", "filesize": 1000}, {"format_id": "2", "filesize_approx": 1000}]}""");
        tracker.Read("""YDH1 {"k":"dl","p":{"status": "downloading", "downloaded_bytes": 500, "total_bytes": 1000, "speed": 100, "eta": 5},"i":{"format_id": "1"}}""");

        var snapshot = tracker.Snapshot();

        Assert.Equal(0.25, snapshot.Fraction);
        Assert.Equal(2000, snapshot.TotalBytes);
        Assert.Equal(TimeSpan.FromSeconds(15), snapshot.Eta);
    }

    private static (ProgressTracker Tracker, List<DownloadProgress> Snapshots) Replay(string sample)
    {
        var tracker = new ProgressTracker();
        var snapshots = new List<DownloadProgress>();
        foreach (var line in Lines(sample, "stdout").Concat(Lines(sample, "stderr")).OrderBy(Order))
        {
            if (tracker.Read(line) is not OutputLineKind.Raw and not OutputLineKind.Plan)
            {
                snapshots.Add(tracker.Snapshot());
            }
        }

        return (tracker, snapshots);
    }

    /// <summary>
    /// The captures are per stream, so put them back in time order: plan, downloads, post-processing (stderr), moved.
    /// </summary>
    private static int Order(string line) =>
        line.Contains("\"k\":\"moved\"", StringComparison.Ordinal) ? 3
        : line.Contains("\"k\":\"pp\"", StringComparison.Ordinal) ? 2
        : line.Contains("\"k\":\"dl\"", StringComparison.Ordinal) ? 1
        : 0;

    private static IEnumerable<string> Lines(string sample, string stream)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Runner", "Samples", $"{sample}.{stream}.txt"), Encoding.UTF8);
        return text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0)
            // The spike harness's --replace-in-metadata adds a MetadataParser run that production never has.
            .Where(l => !l.Contains("\"MetadataParser\"", StringComparison.Ordinal));
    }

    private static List<DownloadPhase> Phases(IEnumerable<DownloadProgress> snapshots)
    {
        var phases = new List<DownloadPhase>();
        foreach (var s in snapshots.Where(s => phases.Count == 0 || phases[^1] != s.Phase))
        {
            phases.Add(s.Phase);
        }

        return phases;
    }

    private static List<string> Postprocessors(IEnumerable<DownloadProgress> snapshots) =>
        [.. snapshots.Select(s => s.Postprocessor).OfType<string>().Distinct()];

    private static void AssertRising(IReadOnlyList<DownloadProgress> snapshots)
    {
        Assert.NotEmpty(snapshots);
        for (var i = 1; i < snapshots.Count; i++)
        {
            Assert.True(snapshots[i].Fraction >= snapshots[i - 1].Fraction, $"Fraction fell at {i}: {snapshots[i - 1].Fraction} -> {snapshots[i].Fraction}");
        }
    }
}
