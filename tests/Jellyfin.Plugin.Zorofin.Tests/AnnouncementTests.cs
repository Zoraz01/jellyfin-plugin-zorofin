using System;
using System.IO;
using Jellyfin.Plugin.Zorofin.Announcements;
using Jellyfin.Plugin.Zorofin.Capabilities;
using Jellyfin.Plugin.Zorofin.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Zorofin.Tests;

public class AnnouncementTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static PluginConfiguration Config() => new()
    {
        AnnouncementEnabled = true,
        AnnouncementType = "maintenance",
        AnnouncementTitle = "  Maintenance tonight ",
        AnnouncementMessage = "The server restarts at 2 AM for about ten minutes.",
        AnnouncementActionLabel = "Details",
        AnnouncementActionHref = "/web/#/home",
    };

    [Fact]
    public void ShowsAValidAnnouncement()
    {
        var a = AnnouncementFeed.Current(Config(), Now)!;
        Assert.True((bool)a["enabled"]!);
        Assert.Equal("maintenance", (string)a["type"]!);
        Assert.Equal("Maintenance tonight", (string)a["title"]!);
        Assert.Equal("/web/#/home", (string)a["action"]!["href"]!);
        Assert.Matches("^a-[0-9a-f]{16}$", (string)a["id"]!);
        Assert.True((bool)a["dismissible"]!);
    }

    [Fact]
    public void FailsClosed()
    {
        Assert.Null(AnnouncementFeed.Current(null, Now));
        Assert.Null(AnnouncementFeed.Current(new PluginConfiguration(), Now));
        var c = Config(); c.AnnouncementTitle = new string('x', 81);
        Assert.Null(AnnouncementFeed.Current(c, Now));
        c = Config(); c.AnnouncementMessage = " ";
        Assert.Null(AnnouncementFeed.Current(c, Now));
        c = Config(); c.AnnouncementStartAt = "not a date";
        Assert.Null(AnnouncementFeed.Current(c, Now));
        c = Config(); c.AnnouncementType = "<b>";
        Assert.Equal("info", (string)AnnouncementFeed.Current(c, Now)!["type"]!);
    }

    [Fact]
    public void RespectsTheSchedule()
    {
        var c = Config(); c.AnnouncementStartAt = "2026-11-05T00:00:00Z";
        Assert.Null(AnnouncementFeed.Current(c, Now));
        c = Config(); c.AnnouncementEndAt = "2026-11-04T11:00:00Z";
        Assert.Null(AnnouncementFeed.Current(c, Now));
        c = Config(); c.AnnouncementStartAt = "2026-11-04T10:00:00Z"; c.AnnouncementEndAt = "2026-11-04T13:00:00Z";
        Assert.NotNull(AnnouncementFeed.Current(c, Now));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("//evil.example")]
    [InlineData("http://plain.example")]
    [InlineData("https://user:pw@evil.example")]
    [InlineData("/\\evil")]
    public void DropsUnsafeLinks(string href)
    {
        var c = Config(); c.AnnouncementActionHref = href;
        Assert.Null(AnnouncementFeed.Current(c, Now)!["action"]);
    }

    [Fact]
    public void EditsAndRevisionsChangeTheId()
    {
        var first = (string)AnnouncementFeed.Current(Config(), Now)!["id"]!;
        Assert.Equal(first, (string)AnnouncementFeed.Current(Config(), Now.AddDays(1))!["id"]!);
        var edited = Config(); edited.AnnouncementMessage += " Sorry!";
        Assert.NotEqual(first, (string)AnnouncementFeed.Current(edited, Now)!["id"]!);
        var again = Config(); again.AnnouncementRevision = 1;
        Assert.NotEqual(first, (string)AnnouncementFeed.Current(again, Now)!["id"]!);
    }

    [Fact]
    public void DismissalsPersistPerAccount()
    {
        var path = Path.Combine(Path.GetTempPath(), "zorofin-dismiss-" + Guid.NewGuid().ToString("N"), "d.json");
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var store = new AnnouncementDismissals(path);
        store.Dismiss(a, "a-1");
        Assert.True(store.IsDismissed(a, "a-1"));
        Assert.False(store.IsDismissed(b, "a-1"));
        var reloaded = new AnnouncementDismissals(path);
        Assert.True(reloaded.IsDismissed(a, "a-1"));
        for (var i = 0; i < 60; i++) reloaded.Dismiss(a, "x-" + i);
        Assert.False(reloaded.IsDismissed(a, "a-1"));
        Assert.True(reloaded.IsDismissed(a, "x-59"));
        File.WriteAllText(path, "{broken");
        Assert.False(new AnnouncementDismissals(path).IsDismissed(a, "x-59"));
    }

    [Fact]
    public void CapabilityListsAnnouncementsWhenOn()
    {
        Assert.False(CapabilityManifest.Build(new PluginConfiguration()).Features.ContainsKey("announcements"));
        Assert.Equal(1, CapabilityManifest.Build(Config()).Features["announcements"].Version);
    }
}
