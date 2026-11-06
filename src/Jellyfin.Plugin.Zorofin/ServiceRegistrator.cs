using System.IO;
using Jellyfin.Plugin.Zorofin.Announcements;
using Jellyfin.Plugin.Zorofin.Companion;
using Jellyfin.Plugin.Zorofin.Home;
using Jellyfin.Plugin.Zorofin.Requests;
using Jellyfin.Plugin.Zorofin.Stats;
using Jellyfin.Plugin.Zorofin.Web;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Zorofin;

/// <summary>Registers the plugin's services with Jellyfin.</summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(UpstreamClient.HttpClientName, client => client.Timeout = System.TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler { AllowAutoRedirect = false });
        serviceCollection.AddHttpClient(CompanionClient.HttpClientName, client => client.Timeout = System.TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler { AllowAutoRedirect = false });
        serviceCollection.AddSingleton<UpstreamClient>();
        serviceCollection.AddSingleton<CompanionClient>();
        serviceCollection.AddSingleton<SeerrUserDirectory>();
        serviceCollection.AddSingleton<RequestsService>();
        serviceCollection.AddSingleton(provider => new PlayStatsStore(
            Path.Combine(provider.GetRequiredService<IApplicationPaths>().DataPath, "zorofin", "play-stats.json")));
        serviceCollection.AddSingleton(provider => new PlayHistoryStore(
            Path.Combine(provider.GetRequiredService<IApplicationPaths>().DataPath, "zorofin", "play-history")));
        serviceCollection.AddSingleton<HomeRowsService>();
        serviceCollection.AddHostedService<PlayStatsRecorder>();
        serviceCollection.AddHostedService<SeerrAutomations>();
        serviceCollection.AddSingleton(provider => new AnnouncementDismissals(
            Path.Combine(provider.GetRequiredService<IApplicationPaths>().DataPath, "zorofin", "announcement-dismissals.json")));
        serviceCollection.AddTransient<IStartupFilter, WebUiStartupFilter>();
    }
}
