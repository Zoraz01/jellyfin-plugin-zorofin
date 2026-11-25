using System.Text.Json.Nodes;
using Jellyfin.Plugin.Zorofin.Home;
using Xunit;

namespace Jellyfin.Plugin.Zorofin.Tests;

public class HomeRowsCacheTests
{
    private static HomeSnapshot Snapshot(DateTime at, string team) =>
        new(at, Array.Empty<HomeRowResult>(), new Dictionary<Guid, JsonObject>(), new[] { team });

    [Fact]
    public async Task FreshAppReadRefreshesTeamsAfterTheCooldownAndOrdinaryReadsShareIt()
    {
        var now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        var cache = new HomeRowsCache(() => now);
        var viewer = Guid.NewGuid();
        var team = "Old team";
        var builds = 0;
        Task<HomeSnapshot> Build(DateTime at) { Interlocked.Increment(ref builds); return Task.FromResult(Snapshot(at, team)); }
        var initial = await cache.GetAsync(viewer, false, Build);
        team = "New team";
        now = now.AddSeconds(3);
        Assert.Same(initial, await cache.GetAsync(viewer, true, Build));
        now = now.AddSeconds(3);
        var refreshed = await cache.GetAsync(viewer, true, Build);
        Assert.Equal("New team", Assert.Single(refreshed.Teams));
        Assert.Same(refreshed, await cache.GetAsync(viewer, false, Build));
        Assert.Equal(2, builds);
        now = now.AddSeconds(61);
        Assert.NotSame(refreshed, await cache.GetAsync(viewer, false, Build));
    }

    [Fact]
    public async Task ConcurrentAppAndWebReadsShareOnlyTheirViewersBuild()
    {
        var cache = new HomeRowsCache();
        var viewer = Guid.NewGuid();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builds = 0;
        async Task<HomeSnapshot> Build(DateTime at) { Interlocked.Increment(ref builds); started.TrySetResult(); await release.Task; return Snapshot(at, "Team"); }
        var first = cache.GetAsync(viewer, false, Build);
        await started.Task;
        Assert.Same(first, cache.GetAsync(viewer, true, Build));
        var other = cache.GetAsync(Guid.NewGuid(), false, Build);
        Assert.NotSame(first, other);
        release.SetResult();
        var snapshots = await Task.WhenAll(first, other);
        Assert.NotSame(snapshots[0], snapshots[1]);
        Assert.Equal(2, builds);
    }

    [Fact]
    public async Task FailedBuildIsNotCachedAndOverloadRemainsBounded()
    {
        var cache = new HomeRowsCache();
        var viewer = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(viewer, false, _ => throw new InvalidOperationException("fixture")));
        var recovered = await cache.GetAsync(viewer, false, at => Task.FromResult(Snapshot(at, "Recovered")));
        Assert.Equal("Recovered", Assert.Single(recovered.Teams));
        cache.Forget(viewer);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builds = Enumerable.Range(0, 12).Select(_ => cache.GetAsync(Guid.NewGuid(), false,
            async at => { await release.Task; return Snapshot(at, "Team"); })).ToArray();
        Assert.Throws<HomeBusyException>(() => { _ = cache.GetAsync(Guid.NewGuid(), true, at => Task.FromResult(Snapshot(at, "Extra"))); });
        release.SetResult();
        await Task.WhenAll(builds);
    }
}
