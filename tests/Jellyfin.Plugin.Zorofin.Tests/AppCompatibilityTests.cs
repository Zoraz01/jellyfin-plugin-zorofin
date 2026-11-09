using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Zorofin.Capabilities;
using Jellyfin.Plugin.Zorofin.Configuration;
using Jellyfin.Plugin.Zorofin.Support;
using Xunit;

namespace Jellyfin.Plugin.Zorofin.Tests;

public class AppCompatibilityTests
{
    private static readonly Dictionary<string, CapabilityFeature> CompanionAlerts = new() { ["notifications"] = new(1, true) };

    [Fact]
    public void StoreAppNamesFollowWhatThePluginCanAnswer()
    {
        Assert.Empty(CapabilityManifest.Build(new PluginConfiguration()).Features);

        var inbox = CapabilityManifest.Build(new PluginConfiguration { SupportInboxEnabled = true }).Features;
        Assert.Equal(new CapabilityFeature(1, true), inbox["support"]);
        Assert.Equal(new CapabilityFeature(1, true), inbox["notifications"]);
        Assert.False(inbox.ContainsKey("notificationSettings"));

        var announcement = CapabilityManifest.Build(new PluginConfiguration { AnnouncementEnabled = true }).Features;
        Assert.Equal(new CapabilityFeature(1, true), announcement["notifications"]);
        Assert.False(announcement.ContainsKey("support"));

        var alerts = CapabilityManifest.Build(new PluginConfiguration(), CompanionAlerts).Features;
        Assert.Equal(new CapabilityFeature(1, true), alerts["notificationSettings"]);
        Assert.Equal(new CapabilityFeature(1, true), alerts["notifications"]);
        Assert.False(alerts.ContainsKey("support"));

        var off = CapabilityManifest.Build(new PluginConfiguration(), new Dictionary<string, CapabilityFeature> { ["notifications"] = new(1, false) }).Features;
        Assert.False(off.ContainsKey("notificationSettings"));
    }

    [Fact]
    public void LegacyListStillWinsForServersWithTheirOwnService()
    {
        var config = new PluginConfiguration { SupportInboxEnabled = true, LegacyAppFeatures = "support,notificationSettings" };
        var features = CapabilityManifest.Build(config, CompanionAlerts).Features;
        Assert.Equal(new CapabilityFeature(1, true), features["support"]);
        Assert.Equal(new CapabilityFeature(1, true), features["notificationSettings"]);
    }

    [Fact]
    public void InboxStoresRepliesAndReadState()
    {
        var path = Path.Combine(Path.GetTempPath(), "zorofin-inbox-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var now = new DateTimeOffset(2026, 11, 9, 1, 0, 0, TimeSpan.Zero);
            var viewer = Guid.NewGuid();
            var other = Guid.NewGuid();
            var inbox = new SupportInbox(path) { Clock = () => now };
            var id = inbox.Add(viewer, " Alex ", " Playback ", " Stops after a minute ", " Big Test Movie ");
            Assert.True(SupportInbox.IsTicketId(id));
            Assert.Empty(inbox.UnreadReplies(viewer));

            now = now.AddMinutes(5);
            Assert.NotNull(inbox.SetReply(id!, "Fixed, try again."));
            var reply = Assert.Single(inbox.UnreadReplies(viewer));
            Assert.Equal("Fixed, try again.", reply.Reply);
            Assert.Equal("Playback", reply.IssueType);
            Assert.Equal("Alex", reply.UserName);
            Assert.Empty(inbox.UnreadReplies(other));
            Assert.False(inbox.MarkRead(other, id!));

            var reloaded = new SupportInbox(path) { Clock = () => now };
            Assert.True(reloaded.MarkRead(viewer, id!));
            Assert.Empty(reloaded.UnreadReplies(viewer));

            now = now.AddMinutes(1);
            reloaded.SetReply(id!, "One more thing.");
            Assert.Single(reloaded.UnreadReplies(viewer));
            Assert.True(reloaded.Delete(id!));
            Assert.Empty(reloaded.All());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void InboxLimitsEachViewer()
    {
        var now = DateTimeOffset.UtcNow;
        var inbox = new SupportInbox(null) { Clock = () => now };
        var viewer = Guid.NewGuid();
        for (var i = 0; i < SupportInbox.PerViewerPerHour; i++)
        {
            Assert.NotNull(inbox.Add(viewer, "A", "Other", "Message " + i, null));
        }

        Assert.Null(inbox.Add(viewer, "A", "Other", "One too many", null));
        Assert.NotNull(inbox.Add(Guid.NewGuid(), "B", "Other", "Someone else", null));
        now = now.AddHours(1).AddSeconds(1);
        Assert.NotNull(inbox.Add(viewer, "A", "Other", "Next hour", null));
    }

    [Theory]
    [InlineData("Playback", "It stops", "", true)]
    [InlineData("", "It stops", "", false)]
    [InlineData("Playback", "  ", "", false)]
    public void ReportsAreCheckedLikeTheApps(string issue, string text, string media, bool valid)
    {
        Assert.Equal(valid, SupportInbox.IsValidReport(issue, text, media));
        Assert.False(SupportInbox.IsValidReport(new string('x', 65), "a", null));
        Assert.False(SupportInbox.IsValidReport("a", new string('x', 1501), null));
        Assert.False(SupportInbox.IsTicketId("T12"));
        Assert.False(SupportInbox.IsTicketId("TABCDEFGH"));
        Assert.False(SupportInbox.IsTicketId("../etc"));
    }

    [Fact]
    public void AlertSettingsMatchTheAppsShape()
    {
        var view = JsonNode.Parse("""
            {"channels":{"email":true,"ntfy":true},
             "email":{"address":"Alex.Smith@Example.com","enabled":true},
             "pendingEmail":null,
             "ntfy":{"enabled":true,"server":"https://ntfy.example.com","topic":"zf-abc","username":"zf-user"}}
            """);
        var settings = RequestAlertsShape.Settings(view, "abc123", "Alex");
        Assert.Equal("abc123", (string)settings["account"]!["id"]!);
        Assert.Equal("Alex", (string)settings["account"]!["name"]!);
        Assert.True((bool)settings["email"]!["enabled"]!);
        Assert.Equal("al********@example.com", (string)settings["email"]!["maskedAddress"]!);
        Assert.True((bool)settings["push"]!["enabled"]!);
        Assert.Equal("https://ntfy.example.com", (string)settings["push"]!["server"]!);
        Assert.Equal("zf-abc", (string)settings["push"]!["topic"]!);

        var empty = RequestAlertsShape.Settings(JsonNode.Parse("""{"channels":{"email":false,"ntfy":false},"email":null,"ntfy":null}"""), "abc123", "Alex");
        Assert.False((bool)empty["email"]!["enabled"]!);
        Assert.Equal(string.Empty, (string)empty["email"]!["maskedAddress"]!);
        Assert.Equal(string.Empty, (string)empty["push"]!["server"]!);
        Assert.Null(RequestAlertsShape.Credentials(view));

        var created = JsonNode.Parse("""{"ntfy":{"enabled":true,"server":"https://n","topic":"t","username":"u"},"ntfyPassword":"secret"}""");
        var credentials = RequestAlertsShape.Credentials(created)!;
        Assert.Equal("secret", (string)credentials["password"]!);
        Assert.Equal("u", (string)credentials["username"]!);
    }

    [Fact]
    public void EveryAppActionHasACompanionRoute()
    {
        foreach (var action in new[] { "email/save", "email/remove", "email/test", "push/create", "push/reset", "push/remove", "push/test" })
        {
            Assert.True(RequestAlertsShape.Actions.ContainsKey(action), action);
        }

        Assert.Equal("preferences/email", RequestAlertsShape.Actions["email/save"].Path);
        Assert.Equal("a@example.com", (string)RequestAlertsShape.Actions["email/save"].Body("a@example.com")!["address"]!);
        Assert.Null(RequestAlertsShape.Actions["email/remove"].Body(string.Empty));
        Assert.Equal("a***@example.com", RequestAlertsShape.Mask("a@example.com"));
        Assert.Equal(string.Empty, RequestAlertsShape.Mask("nope"));
    }
}
