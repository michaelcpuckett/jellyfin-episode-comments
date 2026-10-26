using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.EpisodeComments;
using Xunit;

namespace EpisodeComments.Tests;

public sealed class Harness : IDisposable
{
    public PluginConfiguration Config { get; } = new() { TraktClientId = "test-client-id" };
    public FakeClock Clock { get; } = new();
    public List<string> Paths { get; } = [];
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => Json("[]");
    public TraktClient Trakt { get; }
    public EpisodeResolver Resolver { get; }
    public CommentsService Comments { get; }
    public Harness()
    {
        Trakt = new TraktClient(new Factory(new Handler(request =>
        {
            Assert.Equal(Config.TraktClientId, request.Headers.GetValues("trakt-api-key").Single());
            Assert.Equal("2", request.Headers.GetValues("trakt-api-version").Single());
            Paths.Add(request.RequestUri!.PathAndQuery);
            return Respond(request);
        })), () => Config, Clock);
        Resolver = new(Trakt, () => Config);
        Comments = new(Trakt, Resolver, new MediaResolver(Trakt));
    }
    public static EpisodeIdentity Item() => new(Guid.NewGuid(), new() { ["Tvdb"] = "999" }, new() { ["Tmdb"] = "42" }, 1, 1, null, null, null);
    public const string Lookup = """[{"type":"episode","episode":{"season":1,"number":1,"ids":{"trakt":100}},"show":{"ids":{"trakt":10,"tmdb":42}}}]""";
    public const string Episode = """{"season":1,"number":1,"ids":{"trakt":100},"first_aired":"2008-01-20T00:00:00Z"}""";
    public const string Comment = """[{"id":77,"parent_id":0,"comment":"<script>untrusted</script>","spoiler":true,"likes":12,"replies":2,"created_at":"2026-01-01T00:00:00Z","user":{"username":"reader"}}]""";
    public static HttpResponseMessage Json(string body, int pages = 1, int total = 1)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        response.Headers.Add("X-Pagination-Page-Count", pages.ToString());
        response.Headers.Add("X-Pagination-Item-Count", total.ToString());
        return response;
    }
    public void Dispose() => Trakt.Dispose();
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("https://api.trakt.tv/") };
    }
}

public sealed class FakeClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class ServiceTests
{
    [Fact]
    public async Task EpisodeIdWinsOverDvdNumbersAndPaginationIsPreserved()
    {
        using var h = new Harness();
        h.Respond = r => r.RequestUri!.AbsolutePath.StartsWith("/search/") ? Harness.Json(Harness.Lookup) : Harness.Json(Harness.Comment, 5, 99);
        var item = Harness.Item() with { DisplayOrder = "dvd", Season = 4, Episode = 20 };
        var page = await h.Comments.GetAsync(item, "likes", 2, 20, null, default);
        Assert.Equal("episode-id", page.Match.Method);
        Assert.Equal(1, page.Match.Season);
        Assert.Equal(2, page.Page);
        Assert.Equal(5, page.PageCount);
        Assert.Equal(99, page.TotalItems);
        Assert.True(page.Comments.Single().Spoiler);
        Assert.Equal("reader", page.Comments.Single().Author);
        Assert.Equal("<script>untrusted</script>", page.Comments.Single().Text);
        Assert.Equal("/shows/10/seasons/1/episodes/1/comments/likes?page=2&limit=20", h.Paths.Last());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(page));
        Assert.True(json.RootElement.TryGetProperty("comments", out _));
    }

    [Fact]
    public async Task SpecialsUseSeasonZero()
    {
        using var h = new Harness();
        h.Respond = r => r.RequestUri!.AbsolutePath.StartsWith("/search/")
            ? Harness.Json("""[{"type":"show","show":{"ids":{"trakt":10,"tmdb":42}}}]""")
            : Harness.Json(Harness.Episode.Replace("\"season\":1", "\"season\":0"));
        var match = await h.Resolver.ResolveAsync(Harness.Item() with { EpisodeIds = [], Season = 0 }, default);
        Assert.Equal(0, match.Season);
        Assert.Contains("/shows/10/seasons/0/episodes/1?extended=full", h.Paths);
    }

    [Fact]
    public async Task CombinedFileRequiresDeliberateOverride()
    {
        using var h = new Harness();
        var item = Harness.Item() with { EpisodeEnd = 2 };
        var error = await Assert.ThrowsAsync<CommentsException>(() => h.Resolver.ResolveAsync(item, default));
        Assert.Equal(409, error.Status);
        Assert.Empty(h.Paths);
        h.Config.Mappings = [new() { ItemId = item.ItemId, TraktShowId = 10, Season = 1, Episode = 1 }];
        h.Respond = _ => Harness.Json(Harness.Episode);
        Assert.Equal("override", (await h.Resolver.ResolveAsync(item, default)).Method);
    }

    [Theory]
    [InlineData("dvd")]
    [InlineData("absolute")]
    public async Task AlternateOrdersNeverFallBackToLibraryNumbers(string order)
    {
        using var h = new Harness();
        var ex = await Assert.ThrowsAsync<CommentsException>(() => h.Resolver.ResolveAsync(Harness.Item() with { EpisodeIds = [], DisplayOrder = order }, default));
        Assert.Equal("episode_mapping_required", ex.Code);
        Assert.Empty(h.Paths);
    }

    [Fact]
    public async Task AmbiguousEpisodeIdIsRejected()
    {
        using var h = new Harness();
        h.Respond = _ => Harness.Json(Harness.Lookup.Replace("]", "," + Harness.Lookup.TrimStart('[').Replace("100", "101")));
        Assert.Equal(409, (await Assert.ThrowsAsync<CommentsException>(() => h.Resolver.ResolveAsync(Harness.Item(), default))).Status);
    }

    [Fact]
    public async Task WrongSeriesEpisodeIsNotUsed()
    {
        using var h = new Harness();
        h.Config.AllowAiredNumberFallback = false;
        h.Respond = _ => Harness.Json(Harness.Lookup.Replace("42", "43"));
        Assert.Equal(409, (await Assert.ThrowsAsync<CommentsException>(() => h.Resolver.ResolveAsync(Harness.Item(), default))).Status);
    }

    [Fact]
    public async Task DisagreeingAirDateRequiresOverride()
    {
        using var h = new Harness();
        h.Respond = r => r.RequestUri!.AbsolutePath.StartsWith("/search/")
            ? Harness.Json("""[{"type":"show","show":{"ids":{"trakt":10,"tmdb":42}}}]""") : Harness.Json(Harness.Episode);
        var item = Harness.Item() with { EpisodeIds = [], PremiereDate = new DateTime(2020, 1, 1) };
        Assert.Equal(409, (await Assert.ThrowsAsync<CommentsException>(() => h.Resolver.ResolveAsync(item, default))).Status);
    }

    [Theory]
    [InlineData("invalid", 1, 20)]
    [InlineData("likes", 0, 20)]
    [InlineData("likes", 1, 101)]
    public async Task InvalidQueriesDoNotReachTrakt(string sort, int page, int limit)
    {
        using var h = new Harness();
        Assert.Equal(400, (await Assert.ThrowsAsync<CommentsException>(() => h.Comments.GetAsync(Harness.Item(), sort, page, limit, null, default))).Status);
        Assert.Empty(h.Paths);
    }

    [Fact]
    public async Task RepliesMustBelongToRequestedEpisode()
    {
        using var h = new Harness();
        h.Respond = r => r.RequestUri!.AbsolutePath.StartsWith("/search/") ? Harness.Json(Harness.Lookup)
            : Harness.Json("""{"type":"episode","episode":{"ids":{"trakt":101}}}""");
        Assert.Equal("comment_not_on_episode", (await Assert.ThrowsAsync<CommentsException>(() => h.Comments.GetAsync(Harness.Item(), "oldest", 1, 20, 77, default))).Code);
        Assert.DoesNotContain(h.Paths, p => p.Contains("/replies"));
    }

    [Fact]
    public async Task RepliesHaveTheirOwnPagination()
    {
        using var h = new Harness();
        h.Respond = r => r.RequestUri!.AbsolutePath.StartsWith("/search/") ? Harness.Json(Harness.Lookup)
            : r.RequestUri.AbsolutePath.EndsWith("/item") ? Harness.Json("""{"type":"episode","episode":{"ids":{"trakt":100}}}""")
            : Harness.Json(Harness.Comment.Replace("\"parent_id\":0", "\"parent_id\":77"), 3, 50);
        var page = await h.Comments.GetAsync(Harness.Item(), "oldest", 2, 20, 77, default);
        Assert.Equal(77, page.Comments.Single().ParentId);
        Assert.Equal(3, page.PageCount);
        Assert.Equal("/comments/77/replies?page=2&limit=20", h.Paths.Last());
    }

    [Fact]
    public async Task ConcurrentReadsAreCoalescedAndPagesHaveSeparateCacheEntries()
    {
        using var h = new Harness();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => h.Trakt.GetAsync("comments/77/replies?page=1&limit=20", default)));
        Assert.Single(h.Paths);
        await h.Trakt.GetAsync("comments/77/replies?page=2&limit=20", default);
        Assert.Equal(2, h.Paths.Count);
    }

    [Fact]
    public async Task ChangingCredentialsDoesNotReuseOldCache()
    {
        using var h = new Harness();
        await h.Trakt.GetAsync("a", default);
        h.Config.TraktClientId = "different-client-id";
        await h.Trakt.GetAsync("a", default);
        Assert.Equal(2, h.Paths.Count);
    }

    [Fact]
    public async Task MissingProviderIdsReturnUnmatchedWithoutNetworkRequests()
    {
        using var h = new Harness();
        var item = Harness.Item() with { EpisodeIds = [], SeriesIds = [] };
        var ex = await Assert.ThrowsAsync<CommentsException>(() => h.Resolver.ResolveAsync(item, default));
        Assert.Equal("episode_unmatched", ex.Code);
        Assert.Empty(h.Paths);
    }

    [Fact]
    public async Task MissingCredentialsNeverMakeARequest()
    {
        using var h = new Harness();
        h.Config.TraktClientId = "";
        Assert.Equal("not_configured", (await Assert.ThrowsAsync<CommentsException>(() => h.Trakt.GetAsync("search/tvdb/999?type=episode", default))).Code);
        Assert.Empty(h.Paths);
    }

    [Fact]
    public async Task RateLimitCooldownIsSharedAndHonorsRetryAfter()
    {
        using var h = new Harness();
        h.Respond = _ => new(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(90)) } };
        var first = await Assert.ThrowsAsync<CommentsException>(() => h.Trakt.GetAsync("a", default));
        Assert.Equal(90, first.RetryAfter);
        var next = await Assert.ThrowsAsync<CommentsException>(() => h.Trakt.GetAsync("b", default));
        Assert.Equal(90, next.RetryAfter);
        Assert.Single(h.Paths);
        h.Clock.Now = h.Clock.Now.AddSeconds(91);
        h.Respond = _ => Harness.Json("[]");
        await h.Trakt.GetAsync("b", default);
        Assert.Equal(2, h.Paths.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 503)]
    [InlineData(HttpStatusCode.Forbidden, 503)]
    [InlineData(HttpStatusCode.InternalServerError, 502)]
    [InlineData(HttpStatusCode.NotFound, 404)]
    public async Task UpstreamErrorsAreNotCached(HttpStatusCode upstream, int expected)
    {
        using var h = new Harness();
        h.Respond = _ => new(upstream);
        Assert.Equal(expected, (await Assert.ThrowsAsync<CommentsException>(() => h.Trakt.GetAsync("a", default))).Status);
        h.Respond = _ => Harness.Json("[]");
        await h.Trakt.GetAsync("a", default);
        Assert.Equal(2, h.Paths.Count);
    }

    [Fact]
    public async Task MalformedAndOversizedPayloadsAreRejected()
    {
        using var h = new Harness();
        h.Respond = _ => Harness.Json("this is not json");
        Assert.Equal(502, (await Assert.ThrowsAsync<CommentsException>(() => h.Trakt.GetAsync("a", default))).Status);
        h.Respond = _ => Harness.Json(new string('x', 2 * 1024 * 1024 + 1));
        Assert.Equal(502, (await Assert.ThrowsAsync<CommentsException>(() => h.Trakt.GetAsync("b", default))).Status);
    }
}
