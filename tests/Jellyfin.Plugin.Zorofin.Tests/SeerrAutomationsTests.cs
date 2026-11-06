using System.Text.Json.Nodes;
using Jellyfin.Plugin.Zorofin.Configuration;
using Jellyfin.Plugin.Zorofin.Requests;
using Xunit;

namespace Jellyfin.Plugin.Zorofin.Tests;

public class SeerrAutomationsTests
{
    private static readonly DateTime Today = new(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc);

    private static IEnumerable<(int, int)> Season(int season, int episodes) => Enumerable.Range(1, episodes).Select(e => (season, e));

    [Fact]
    public void AsksForTheNextSeasonNearTheEndOfTheNewestOne()
    {
        var library = Season(1, 10).Concat(Season(2, 8)).Append((0, 1)).ToList();
        Assert.Null(SeerrAutomations.NextSeason(2, 5, library, 2));   // 3 left
        Assert.Equal(3, SeerrAutomations.NextSeason(2, 6, library, 2));
        Assert.Equal(3, SeerrAutomations.NextSeason(2, 8, library, 2));
        Assert.Equal(3, SeerrAutomations.NextSeason(2, 8, library, 0));
        Assert.Null(SeerrAutomations.NextSeason(2, 7, library, 0));
        // Season 1 is not the newest here, so season 2 is already on the server.
        Assert.Null(SeerrAutomations.NextSeason(1, 10, library, 2));
        // Specials never trigger.
        Assert.Null(SeerrAutomations.NextSeason(0, 1, library, 2));
    }

    private static JsonNode Title(string? mediaInfo = null, string seasons = """[{"seasonNumber":1,"episodeCount":10},{"seasonNumber":2,"episodeCount":8},{"seasonNumber":3,"episodeCount":8,"airDate":"2027-01-10"}]""") =>
        JsonNode.Parse("{\"id\":1,\"seasons\":" + seasons + (mediaInfo is null ? string.Empty : ",\"mediaInfo\":" + mediaInfo) + "}")!;

    [Fact]
    public void OnlyRequestsSeasonsSeerrWouldTake()
    {
        Assert.True(SeerrAutomations.SeasonIsOpen(Title(), 3, Today));
        Assert.False(SeerrAutomations.SeasonIsOpen(Title(), 4, Today), "season Seerr doesn't list");
        Assert.False(SeerrAutomations.SeasonIsOpen(Title(seasons: """[{"seasonNumber":3,"episodeCount":0}]"""), 3, Today), "no episodes yet");
        Assert.False(SeerrAutomations.SeasonIsOpen(Title(seasons: """[{"seasonNumber":3,"episodeCount":8,"airDate":"2028-06-01"}]"""), 3, Today), "too far out");
        Assert.True(SeerrAutomations.SeasonIsOpen(Title(seasons: """[{"seasonNumber":3,"episodeCount":8,"airDate":null}]"""), 3, Today), "announced, no date");

        Assert.True(SeerrAutomations.SeasonIsOpen(Title("""{"status":4,"seasons":[{"seasonNumber":2,"status":5},{"seasonNumber":3,"status":1}],"requests":[]}"""), 3, Today));
        Assert.False(SeerrAutomations.SeasonIsOpen(Title("""{"status":4,"seasons":[{"seasonNumber":3,"status":3}]}"""), 3, Today), "already processing");
        Assert.False(SeerrAutomations.SeasonIsOpen(Title("""{"status":4,"seasons":[],"requests":[{"status":3,"seasons":[{"seasonNumber":3}]}]}"""), 3, Today), "a declined request sticks");
        Assert.False(SeerrAutomations.SeasonIsOpen(JsonNode.Parse("{}"), 3, Today));
        Assert.False(SeerrAutomations.SeasonIsOpen(null, 3, Today));
    }

    [Fact]
    public void ScanWaitsForQuietButNotForever()
    {
        var first = Today;
        var delay = TimeSpan.FromMinutes(2);
        Assert.Equal(delay, SeerrAutomations.ScanDue(first, first, delay));
        Assert.Equal(delay, SeerrAutomations.ScanDue(first, first.AddMinutes(5), delay));
        Assert.Equal(TimeSpan.FromMinutes(1), SeerrAutomations.ScanDue(first, first.AddMinutes(9), delay));
        Assert.Equal(TimeSpan.Zero, SeerrAutomations.ScanDue(first, first.AddMinutes(20), delay));
    }

    [Fact]
    public void BackgroundJobsNeedOnlySeerrsAddressAndKey()
    {
        Assert.Null(SeerrAutomations.Seerr(null));
        Assert.Null(SeerrAutomations.Seerr(new PluginConfiguration()));
        Assert.NotNull(SeerrAutomations.Seerr(new PluginConfiguration { SeerrUrl = "http://seerr:5055", SeerrApiKey = "key", RequestsEnabled = false }));
        var defaults = new PluginConfiguration();
        Assert.False(defaults.AutoSeasonRequestsEnabled);
        Assert.False(defaults.SeerrScanOnLibraryAdd);
        Assert.False(defaults.PlayHistoryEnabled);
        Assert.Equal(2, defaults.AutoSeasonEpisodesLeft);
    }
}
