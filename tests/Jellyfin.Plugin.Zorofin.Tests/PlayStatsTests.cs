using Jellyfin.Plugin.Zorofin.Stats;
using Xunit;

namespace Jellyfin.Plugin.Zorofin.Tests;

public class PlayStatsTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void IgnoresShortAndAbandonedPlays()
    {
        var store = new PlayStatsStore(null);
        var title = Guid.NewGuid();
        Assert.False(store.Record(title, TimeSpan.FromSeconds(90), Now));
        Assert.False(store.Record(title, TimeSpan.FromHours(7), Now));
        Assert.False(store.Record(Guid.Empty, TimeSpan.FromMinutes(30), Now));
        Assert.Empty(store.Top(7, Now));
    }

    [Fact]
    public void RanksByWatchTimeWithinWindow()
    {
        var store = new PlayStatsStore(null);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        store.Record(a, TimeSpan.FromMinutes(30), Now);
        store.Record(b, TimeSpan.FromMinutes(20), Now);
        store.Record(b, TimeSpan.FromMinutes(20), Now.AddDays(-1));
        store.Record(a, TimeSpan.FromHours(5), Now.AddDays(-8));
        var top = store.Top(7, Now);
        Assert.Equal(new[] { b, a }, top.Select(t => t.TitleId));
        Assert.Equal(2, top[0].Plays);
    }

    [Fact]
    public void PersistsAndPrunes()
    {
        var path = Path.Combine(Path.GetTempPath(), "zorofin-stats-" + Guid.NewGuid().ToString("N"), "play-stats.json");
        var title = Guid.NewGuid();
        var store = new PlayStatsStore(path);
        store.Record(title, TimeSpan.FromMinutes(10), Now);
        store.Record(Guid.NewGuid(), TimeSpan.FromMinutes(10), Now.AddDays(-40));
        store.Flush(Now);

        var reloaded = new PlayStatsStore(path);
        var only = Assert.Single(reloaded.Top(30, Now));
        Assert.Equal(title, only.TitleId);
        Assert.DoesNotContain("user", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void DamagedFileStartsFresh()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zorofin-stats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "play-stats.json");
        File.WriteAllText(path, "{not json");
        Assert.Empty(new PlayStatsStore(path).Top(7, Now));
        Directory.Delete(dir, true);
    }
}
