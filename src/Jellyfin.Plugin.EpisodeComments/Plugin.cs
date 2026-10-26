using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.EpisodeComments;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;
    public static Plugin? Instance { get; private set; }
    public override string Name => "Episode Comments";
    public override string Description => "Read Trakt episode discussions from any Jellyfin client.";
    public override Guid Id => Guid.Parse("79672a4d-f247-4370-9e41-f508bd891631");
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new() { Name = "episodecomments", EmbeddedResourcePath = "Jellyfin.Plugin.EpisodeComments.Configuration.config.html" }
    ];
}

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string TraktClientId { get; set; } = "";
    public int CacheMinutes { get; set; } = 15;
    public bool AllowAiredNumberFallback { get; set; } = true;
    public EpisodeMapping[] Mappings { get; set; } = [];
}

public sealed class EpisodeMapping
{
    public Guid ItemId { get; set; }
    public long TraktShowId { get; set; }
    public int Season { get; set; }
    public int Episode { get; set; }
}

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<Func<PluginConfiguration>>(_ => () => Plugin.Instance?.Configuration ?? new());
        services.AddSingleton(TimeProvider.System);
        services.AddHttpClient("EpisodeComments.Trakt", client =>
        {
            client.BaseAddress = new Uri("https://api.trakt.tv/");
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-EpisodeComments/0.1.0");
        });
        services.AddSingleton<TraktClient>();
        services.AddSingleton<EpisodeResolver>();
        services.AddSingleton<CommentsService>();
    }
}
