using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Zorofin.Support;
using Xunit;

namespace Jellyfin.Plugin.Zorofin.Tests;

public class PlaybackReportsTests
{
    [Fact]
    public void KnownKindsOnly()
    {
        Assert.True(PlaybackReports.IsKind("start_failed"));
        Assert.True(PlaybackReports.IsKind("quality_lowered"));
        Assert.False(PlaybackReports.IsKind("Start_Failed"));
        Assert.False(PlaybackReports.IsKind("anything"));
        Assert.False(PlaybackReports.IsKind(null));
    }

    [Fact]
    public void StoresTrimsAndReloadsNewestFirst()
    {
        var path = Path.Combine(Path.GetTempPath(), "zorofin-playback-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var now = new DateTimeOffset(2026, 11, 24, 7, 0, 0, TimeSpan.Zero);
            var viewer = Guid.NewGuid();
            var reports = new PlaybackReports(path) { Clock = () => now };
            Assert.True(reports.Add(viewer, " Alex ", new PlaybackReport
            {
                Kind = "start_failed",
                Title = " Big Test Movie ",
                Platform = "Android TV",
                AppVersion = "1.5.0",
                Reason = new string('x', 900),
                StartMs = -5,
                PositionSeconds = int.MaxValue,
                // Whatever an app claims, the viewer and time are the server's.
                UserId = Guid.NewGuid(),
                UserName = "Someone else",
                At = now.AddYears(-3),
            }));
            now = now.AddMinutes(1);
            Assert.True(reports.Add(viewer, "Alex", new PlaybackReport { Kind = "stalled", Title = "Later" }));

            var reloaded = new PlaybackReports(path).All();
            Assert.Equal(new[] { "Later", "Big Test Movie" }, reloaded.Select(r => r.Title));
            var first = reloaded[1];
            Assert.Equal(viewer, first.UserId);
            Assert.Equal("Alex", first.UserName);
            Assert.Equal(now.AddMinutes(-1), first.At);
            Assert.Equal(300, first.Reason.Length);
            Assert.Equal(0, first.StartMs);
            Assert.Equal(864_000, first.PositionSeconds);
            Assert.Null(reloaded[0].StartMs);

            reports.Clear();
            Assert.Empty(new PlaybackReports(path).All());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LimitsEachViewerPerHourAndKeepsTheNewest()
    {
        var now = new DateTimeOffset(2026, 11, 24, 7, 0, 0, TimeSpan.Zero);
        var reports = new PlaybackReports(null) { Clock = () => now };
        var viewer = Guid.NewGuid();
        for (var i = 0; i < PlaybackReports.PerViewerPerHour; i++)
        {
            Assert.True(reports.Add(viewer, "Alex", new PlaybackReport { Kind = "stalled" }));
        }

        Assert.False(reports.Add(viewer, "Alex", new PlaybackReport { Kind = "stalled" }));
        Assert.True(reports.Add(Guid.NewGuid(), "Sam", new PlaybackReport { Kind = "stalled" }));
        now = now.AddHours(1).AddSeconds(1);
        Assert.True(reports.Add(viewer, "Alex", new PlaybackReport { Kind = "stalled" }));

        for (var i = 0; i < PlaybackReports.MaxReports + 5; i++)
        {
            reports.Add(Guid.NewGuid(), "Viewer", new PlaybackReport { Kind = "stopped", Title = i.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        var all = reports.All();
        Assert.Equal(PlaybackReports.MaxReports, all.Count);
        Assert.Equal((PlaybackReports.MaxReports + 4).ToString(System.Globalization.CultureInfo.InvariantCulture), all[0].Title);
    }

    [Fact]
    public void DamagedFileStartsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), "zorofin-playback-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{not json");
            Assert.Empty(new PlaybackReports(path).All());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
