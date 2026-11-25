using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Zorofin.Capabilities;
using Jellyfin.Plugin.Zorofin.Configuration;
using Jellyfin.Plugin.Zorofin.Notifications;
using Xunit;

namespace Jellyfin.Plugin.Zorofin.Tests;

public class NotificationReadsTests
{
    private static readonly Guid Ana = Guid.NewGuid();
    private static readonly Guid Ben = Guid.NewGuid();

    [Fact]
    public void ReadOnOneDeviceIsReadForTheAccountOnly()
    {
        var reads = new NotificationReads(null);
        Assert.False(reads.Get(Ana).Initialized);
        reads.Add(Ana, new[] { 7, 7, 9 }, new[] { "Tabc1234|2026-11-09T20:00:00.000Z" }, initialize: true);

        var ana = reads.Get(Ana);
        Assert.True(ana.Initialized);
        Assert.Equal(new[] { 7, 9 }, ana.Requests);
        Assert.Equal(new[] { "Tabc1234|2026-11-09T20:00:00.000Z" }, ana.Replies);
        Assert.Empty(reads.Get(Ben).Requests);
        Assert.False(reads.Get(Ben).Initialized);
    }

    [Fact]
    public void ALaterReplyOnTheSameTicketIsNew()
    {
        Assert.Equal("Tabc1234|2026-11-09T20:00:00.000Z", NotificationReads.ReplyKey("Tabc1234", "2026-11-09T20:00:00.000Z"));
        Assert.Null(NotificationReads.ReplyKey("not-a-ticket", "2026-11-09T20:00:00.000Z"));
        Assert.Null(NotificationReads.ReplyKey("Tabc1234", "<script>"));
        Assert.Null(NotificationReads.ReplyKey("Tabc1234", string.Empty));
        Assert.False(NotificationReads.IsReplyKey("Tabc1234|"));
        Assert.False(NotificationReads.IsReplyKey("Tabc1234|1|2"));

        var reads = new NotificationReads(null);
        reads.Add(Ana, Array.Empty<int>(), new[] { "Tabc1234|2026-11-09T20:00:00.000Z", "junk" }, false);
        var state = reads.Get(Ana);
        Assert.DoesNotContain("Tabc1234|2026-11-10T08:00:00.000Z", state.Replies);
        Assert.Single(state.Replies);
        Assert.False(state.Initialized);
    }

    [Fact]
    public void KeepsTheNewestAndPersists()
    {
        var path = Path.Combine(Path.GetTempPath(), "zorofin-reads-" + Guid.NewGuid().ToString("N"), "reads.json");
        try
        {
            var reads = new NotificationReads(path);
            reads.Add(Ana, Enumerable.Range(1, NotificationReads.RequestsPerAccount + 5), Array.Empty<string>(), true);
            var state = new NotificationReads(path).Get(Ana);
            Assert.True(state.Initialized);
            Assert.Equal(NotificationReads.RequestsPerAccount, state.Requests.Count);
            Assert.Equal(6, state.Requests[0]);
            Assert.Equal(NotificationReads.RequestsPerAccount + 5, state.Requests[^1]);

            // A returned state is a copy; changing it changes nothing stored.
            state.Requests.Clear();
            Assert.NotEmpty(reads.Get(Ana).Requests);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void DamagedFileStartsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), "zorofin-reads-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{not json");
        try
        {
            Assert.Empty(new NotificationReads(path).Get(Ana).Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ListedOnlyWhenThereAreNoticesToRead()
    {
        Assert.False(CapabilityManifest.Build(new PluginConfiguration()).Features.ContainsKey("notificationReads"));
        var feature = CapabilityManifest.Build(new PluginConfiguration { SupportInboxEnabled = true }).Features["notificationReads"];
        Assert.Equal(1, feature.Version);
        Assert.True(feature.Enabled);
        Assert.True(CapabilityManifest.Build(new PluginConfiguration { LegacyAppFeatures = "requests" }).Features.ContainsKey("notificationReads"));
    }
}
