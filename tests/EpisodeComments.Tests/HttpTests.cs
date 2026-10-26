using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Jellyfin.Plugin.EpisodeComments;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace EpisodeComments.Tests;

public sealed class HttpTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid ItemId = Guid.NewGuid();

    private static IHost Server(Harness h, Mock<ILibraryManager> library)
    {
        var builder = new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddSingleton(library.Object);
            services.AddSingleton(h.Comments);
            services.AddSingleton<Func<PluginConfiguration>>(_ => () => h.Config);
            services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuth>("test", _ => { });
            services.AddAuthorization();
            services.AddControllers().AddApplicationPart(typeof(CommentsController).Assembly);
        }).Configure(app =>
        {
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        }));
        return builder.Start();
    }

    [Fact]
    public async Task CapabilitiesIdentifyPluginWithoutCallingTraktOrRevealingClientId()
    {
        using var h = new Harness();
        using var server = Server(h, new Mock<ILibraryManager>(MockBehavior.Strict));
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Session", UserId.ToString());
        var body = await client.GetStringAsync("/EpisodeComments/v1/Capabilities");
        using var json = JsonDocument.Parse(body);
        Assert.Equal("79672a4d-f247-4370-9e41-f508bd891631", json.RootElement.GetProperty("pluginId").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("apiVersion").GetInt32());
        Assert.True(json.RootElement.GetProperty("configured").GetBoolean());
        Assert.DoesNotContain(h.Config.TraktClientId, body);
        h.Config.TraktClientId = "";
        using var unconfigured = JsonDocument.Parse(await client.GetStringAsync("/EpisodeComments/v1/Capabilities"));
        Assert.False(unconfigured.RootElement.GetProperty("configured").GetBoolean());
        Assert.Empty(h.Paths);
    }

    [Fact]
    public async Task AnonymousRequestsAreChallenged()
    {
        using var h = new Harness();
        using var server = Server(h, new Mock<ILibraryManager>(MockBehavior.Strict));
        using var client = server.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/EpisodeComments/v1/Items/{ItemId}/Comments")).StatusCode);
        Assert.Empty(h.Paths);
    }

    [Fact]
    public async Task UserlessApiKeysAreRejected()
    {
        using var h = new Harness();
        using var server = Server(h, new Mock<ILibraryManager>(MockBehavior.Strict));
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Session", "api-key");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/EpisodeComments/v1/Items/{ItemId}/Comments")).StatusCode);
        Assert.Empty(h.Paths);
    }

    [Fact]
    public async Task InaccessibleEpisodesNeverReachTrakt()
    {
        using var h = new Harness();
        var library = new Mock<ILibraryManager>(MockBehavior.Strict);
        library.Setup(x => x.GetItemById<Episode>(ItemId, UserId)).Returns((Episode?)null);
        using var server = Server(h, library);
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Session", UserId.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/EpisodeComments/v1/Items/{ItemId}/Comments")).StatusCode);
        Assert.Empty(h.Paths);
        library.VerifyAll();
    }

    [Fact]
    public async Task AuthenticatedRouteReturnsStableContractAndUsesCurrentUser()
    {
        using var h = new Harness();
        var library = new Mock<ILibraryManager>(MockBehavior.Strict);
        var seriesId = Guid.NewGuid();
        library.Setup(x => x.GetItemById<Episode>(ItemId, UserId)).Returns(new Episode
        {
            Id = ItemId, SeriesId = seriesId, ProviderIds = new() { ["Tvdb"] = "999" }, ParentIndexNumber = 1, IndexNumber = 1
        });
        library.Setup(x => x.GetItemById<Series>(seriesId, UserId)).Returns(new Series { Id = seriesId, ProviderIds = new() { ["Tmdb"] = "42" } });
        h.Respond = r => r.RequestUri!.AbsolutePath.StartsWith("/search/") ? Harness.Json(Harness.Lookup) : Harness.Json(Harness.Comment, 5, 99);
        using var server = Server(h, library);
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Session", UserId.ToString());
        var response = await client.GetAsync($"/EpisodeComments/v1/Items/{ItemId}/Comments?sort=newest&page=2&limit=20&userId={Guid.NewGuid()}");
        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, json.RootElement.GetProperty("page").GetInt32());
        Assert.True(json.RootElement.GetProperty("comments")[0].GetProperty("spoiler").GetBoolean());
        Assert.Equal("/shows/10/seasons/1/episodes/1/comments/newest?page=2&limit=20", h.Paths.Last());
        library.VerifyAll();
    }

    [Fact]
    public async Task ProblemResponseIncludesRetryAfterWithoutExposingCredentials()
    {
        using var h = new Harness();
        var library = new Mock<ILibraryManager>(MockBehavior.Strict);
        library.Setup(x => x.GetItemById<Episode>(ItemId, UserId)).Returns(new Episode { Id = ItemId, ProviderIds = new() { ["Tvdb"] = "999" } });
        library.Setup(x => x.GetItemById<Series>(Guid.Empty, UserId)).Returns((Series?)null);
        h.Respond = _ => new(HttpStatusCode.TooManyRequests);
        using var server = Server(h, library);
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Session", UserId.ToString());
        var response = await client.GetAsync($"/EpisodeComments/v1/Items/{ItemId}/Comments");
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(60), response.Headers.RetryAfter?.Delta);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("trakt_rate_limited", body);
        Assert.DoesNotContain(h.Config.TraktClientId, body);
    }

    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var value = Request.Headers["X-Test-Session"].FirstOrDefault();
            if (value is null) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = Guid.TryParse(value, out var id) ? new[] { new Claim("Jellyfin-UserId", id.ToString()) } : Array.Empty<Claim>();
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), "test")));
        }
    }
}
