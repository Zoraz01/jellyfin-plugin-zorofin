using System.Text.Json.Nodes;
using Jellyfin.Plugin.Zorofin.Stats;
using Xunit;

namespace Jellyfin.Plugin.Zorofin.Tests;

public class PlayHistoryTests
{
    private static readonly DateTime Now = new(2026, 11, 4, 18, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Ana = Guid.NewGuid();
    private static readonly Guid Ben = Guid.NewGuid();

    private static PlayEvent Play(Guid user, string type, string name, DateTime started, int seconds, Guid? series = null, string? seriesName = null, string client = "Zorofin", string method = "DirectPlay", string? importKey = null) => new()
    {
        StartedUtc = started,
        Seconds = seconds,
        UserId = user,
        UserName = user == Ana ? "ana" : "ben",
        ItemId = Guid.NewGuid(),
        ItemType = type,
        ItemName = name,
        SeriesId = series,
        SeriesName = seriesName,
        Client = client,
        Device = "Living room",
        PlayMethod = method,
        ImportKey = importKey,
    };

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "zorofin-history-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void TrackerLeavesOutPausedTimeAndCapsSilence()
    {
        var tracker = new PlaybackTracker();
        var t0 = Now;
        tracker.Start("a", t0);
        tracker.Progress("a", t0.AddMinutes(4), paused: false);
        tracker.Progress("a", t0.AddMinutes(8), paused: true);    // 8 min played, now paused
        tracker.Progress("a", t0.AddMinutes(38), paused: false);  // 30 min paused, not counted
        tracker.Progress("a", t0.AddMinutes(39), paused: false, playMethod: "Transcode", transcode: "audio");
        var stopped = tracker.Stop("a", t0.AddMinutes(90));       // 51 min of silence counts as 5
        Assert.NotNull(stopped);
        Assert.Equal((8 + 1 + 5) * 60, stopped.Value.Seconds);
        Assert.Equal(t0, stopped.Value.StartedUtc);
        Assert.Equal("Transcode", stopped.Value.PlayMethod);
        Assert.Equal("audio", stopped.Value.Transcode);
        Assert.Null(tracker.Stop("a", t0.AddMinutes(91)));
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void TrackerPicksUpPlaybackAlreadyRunning()
    {
        var tracker = new PlaybackTracker();
        tracker.Progress("late", Now, paused: false);
        Assert.Equal(120, tracker.Stop("late", Now.AddMinutes(2))!.Value.Seconds);
    }

    [Fact]
    public void StoreDropsMisTapsAndKeepsOrder()
    {
        var store = new PlayHistoryStore(null);
        Assert.False(store.Add(Play(Ana, "Movie", "Tap", Now, 5)));
        Assert.True(store.Add(Play(Ana, "Movie", "Later", Now.AddHours(1), 600)));
        Assert.True(store.Add(Play(Ben, "Movie", "Earlier", Now, 600)));
        var all = store.Between(DateTime.MinValue, DateTime.MaxValue);
        Assert.Equal(new[] { "Earlier", "Later" }, all.Select(p => p.ItemName));
        Assert.Single(store.Between(Now, Now.AddMinutes(30)));
    }

    [Fact]
    public void StorePersistsAcrossRestartsAndSurvivesADamagedLine()
    {
        var dir = TempDir();
        try
        {
            var store = new PlayHistoryStore(dir);
            store.Add(Play(Ana, "Movie", "October", new DateTime(2026, 10, 31, 23, 0, 0, DateTimeKind.Utc), 600));
            store.Add(Play(Ana, "Movie", "November", Now, 600));
            Assert.True(File.Exists(Path.Combine(dir, "2026-10.jsonl")));
            File.AppendAllText(Path.Combine(dir, "2026-11.jsonl"), "{\"id\":\"cut off");
            var reloaded = new PlayHistoryStore(dir);
            Assert.Equal(new[] { "October", "November" }, reloaded.Between(DateTime.MinValue, DateTime.MaxValue).Select(p => p.ItemName));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ImportSkipsRowsAlreadyImported()
    {
        var dir = TempDir();
        try
        {
            var store = new PlayHistoryStore(dir);
            var batch = new[] { Play(Ana, "Movie", "A", Now.AddDays(-40), 600, importKey: "pr:1"), Play(Ben, "Movie", "B", Now.AddDays(-2), 600, importKey: "pr:2") };
            Assert.Equal((2, 0, 0), store.Import(batch));
            Assert.Equal((0, 2, 0), store.Import(batch));
            Assert.Equal((0, 1, 0), store.Import(new[] { Play(Ana, "Movie", "No key", Now, 600) }));
            // Once the plugin records on its own, imported plays from then on would be counted twice.
            store.Add(Play(Ben, "Movie", "Own", Now.AddDays(-1), 600));
            Assert.Equal((1, 0, 1), store.Import(new[] { Play(Ana, "Movie", "Before", Now.AddDays(-3), 600, importKey: "pr:3"), Play(Ana, "Movie", "After", Now, 600, importKey: "pr:4") }));
            Assert.Equal((0, 2, 0), new PlayHistoryStore(dir).Import(batch));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void PruneAndDeleteRewriteFiles()
    {
        var dir = TempDir();
        try
        {
            var store = new PlayHistoryStore(dir);
            store.Add(Play(Ana, "Movie", "Old", Now.AddDays(-400), 600));
            store.Add(Play(Ana, "Movie", "Recent", Now.AddDays(-1), 600));
            store.Add(Play(Ben, "Movie", "Ben's", Now, 600));
            Assert.Equal(0, store.Prune(0, Now));
            Assert.Equal(1, store.Prune(365, Now));
            Assert.Equal(1, store.Delete(Ben));
            var reloaded = new PlayHistoryStore(dir);
            Assert.Equal(new[] { "Recent" }, reloaded.Between(DateTime.MinValue, DateTime.MaxValue).Select(p => p.ItemName));
            Assert.Equal(1, reloaded.Delete(null));
            Assert.Equal(0, new PlayHistoryStore(dir).Count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SummaryBucketsInTheViewersTimeZone()
    {
        var show = Guid.NewGuid();
        // 02:00 UTC on Nov 4 is 21:00 on Nov 3 in New York (UTC-5).
        var plays = new[]
        {
            Play(Ana, "Episode", "Pilot", new DateTime(2026, 11, 4, 2, 0, 0, DateTimeKind.Utc), 1800, show, "The Show"),
            Play(Ana, "Episode", "Second", new DateTime(2026, 11, 4, 3, 0, 0, DateTimeKind.Utc), 1200, show, "The Show", method: "Transcode"),
            Play(Ben, "Movie", "Film", new DateTime(2026, 11, 4, 15, 0, 0, DateTimeKind.Utc), 6000, client: "Jellyfin Web"),
        };
        var from = new DateTime(2026, 11, 2, 5, 0, 0, DateTimeKind.Utc);
        var summary = ActivityReports.Summary(plays, from, Now, -300, new ActivityFilter());

        Assert.Equal(3, (int)summary["totals"]!["plays"]!);
        Assert.Equal(9000, (long)summary["totals"]!["seconds"]!);
        Assert.Equal(2, (int)summary["totals"]!["users"]!);
        Assert.Equal(2, (int)summary["totals"]!["titles"]!);
        Assert.Equal(1, (int)summary["totals"]!["transcoded"]!);

        var days = summary["days"]!.AsArray();
        Assert.Equal(new[] { "2026-11-02", "2026-11-03", "2026-11-04" }, days.Select(d => (string)d!["date"]!));
        Assert.Equal(3000, (long)days[1]!["seconds"]!);
        Assert.Equal(6000, (long)days[2]!["seconds"]!);
        Assert.Equal(1800, (long)summary["hours"]![21]!["seconds"]!);
        Assert.Equal(1800, summary["heatmap"]![2]![21]!.GetValue<long>()); // Tuesday 21:00
        Assert.Equal(1200, summary["heatmap"]![2]![22]!.GetValue<long>());

        var shows = summary["shows"]!.AsArray();
        Assert.Single(shows);
        Assert.Equal("The Show", (string)shows[0]!["name"]!);
        Assert.Equal(2, (int)shows[0]!["plays"]!);
        Assert.Equal("ben", (string)summary["users"]![0]!["name"]!);
        Assert.Equal("Jellyfin Web", (string)summary["clients"]![0]!["name"]!);
    }

    [Fact]
    public void FiltersAndLogPaging()
    {
        var plays = Enumerable.Range(0, 30).Select(i => Play(i % 2 == 0 ? Ana : Ben, "Movie", "Film " + i, Now.AddMinutes(-i), 600)).ToList();
        var log = ActivityReports.Log(plays, new ActivityFilter(UserId: Ana), 10, 10);
        Assert.Equal(15, (int)log["total"]!);
        Assert.Equal(5, log["items"]!.AsArray().Count);
        Assert.Equal("Film 20", (string)log["items"]![0]!["itemName"]!);
        Assert.Equal(1, (int)ActivityReports.Log(plays, new ActivityFilter(Search: "film 29"), 0, 10)["total"]!);
        Assert.Equal(0, (int)ActivityReports.Log(plays, new ActivityFilter(ItemType: "Episode"), 0, 10)["total"]!);
    }

    [Fact]
    public void CsvEscapesCellsAndFormulas()
    {
        var csv = ActivityReports.Csv(new[] { Play(Ana, "Movie", "=HYPERLINK(\"x\")", Now, 90), Play(Ben, "Movie", "Tom, Jerry", Now, 600) }, new ActivityFilter());
        var lines = csv.TrimEnd('\n').Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Contains("\"Tom, Jerry\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", csv, StringComparison.Ordinal);
        Assert.Contains(",1.5,", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyRowsMatchWhatRewindReads()
    {
        // An imported play gives back Playback Reporting's rowid and time text exactly as Rewind saw them.
        var play = Play(Ana, "Episode", "Pilot", Now, 1800, importKey: "playbackReporting:7|2026-11-04 13:00:00.1234567");
        var row = ActivityReports.LegacyRow(play);
        Assert.Equal(7, (long)row["SourceRowId"]!);
        Assert.Equal("2026-11-04 13:00:00.1234567", (string)row["DateCreated"]!);
        Assert.Equal(Ana.ToString("N"), (string)row["UserId"]!);
        Assert.Equal(1800, (int)row["PlayDuration"]!);
        // A play the plugin recorded itself has its own id and a UTC time.
        var own = ActivityReports.LegacyRow(play with { ImportKey = null });
        Assert.Equal(play.Id, (string)own["SourceRowId"]!);
        Assert.Equal("2026-11-04T18:00:00Z", (string)own["DateCreated"]!);
    }

    [Fact]
    public void ParsesPlaybackReportingTimesAndMethods()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        Assert.True(ActivityController.TryLocal("2026-11-04 13:00:00.1234567", newYork, out var utc));
        Assert.Equal(new DateTime(2026, 11, 4, 18, 0, 0, DateTimeKind.Utc), utc.AddTicks(-1234567));
        Assert.True(ActivityController.TryLocal("2026-07-04 13:00:00", newYork, out utc));
        Assert.Equal(17, utc.Hour);
        Assert.True(ActivityController.TryLocal("2026-07-04T13:00:00Z", newYork, out utc));
        Assert.Equal(13, utc.Hour);
        Assert.True(ActivityController.TryLocal("2026-07-04T13:00:00-07:00", newYork, out utc));
        Assert.Equal(20, utc.Hour);
        Assert.True(ActivityController.TryLocal("2026-03-08 02:30:00", newYork, out utc)); // skipped by DST
        Assert.False(ActivityController.TryLocal("yesterday", newYork, out _));

        Assert.Equal(("DirectPlay", null), ActivityController.Method("DirectPlay"));
        Assert.Equal(("Transcode", "video"), ActivityController.Method("Transcode (v:h264 a:direct)"));
        Assert.Equal(("Transcode", "video, audio"), ActivityController.Method("Transcode (v:hevc a:aac)"));
        Assert.Equal((string.Empty, null), ActivityController.Method(null));
    }
}
