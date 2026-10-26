using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.EpisodeComments;
using Xunit;

namespace EpisodeComments.Tests;

public sealed class MediaTests
{
    [Theory]
    [InlineData("movie", "movies")]
    [InlineData("show", "shows")]
    public async Task ExactProviderIdsResolveMoviesAndSeriesToSeparateDiscussions(string type, string path)
    {
        using var h = new Harness();
        h.Respond = r => r.RequestUri!.AbsolutePath.StartsWith("/search/")
            ? Harness.Json("""[{"type":"KIND","KIND":{"ids":{"trakt":123,"tmdb":42,"imdb":"tt12345"}}}]""".Replace("KIND", type))
            : Harness.Json(Harness.Comment, 5, 99);
        var page = await h.Comments.GetMediaAsync(new(Guid.NewGuid(), type, new() { ["Tmdb"] = "42", ["Imdb"] = "tt12345" }), "newest", 2, 20, null, default);
        Assert.Equal(type, page.Match.Type);
        Assert.Equal(123, page.Match.TraktId);
        Assert.Equal(99, page.TotalItems);
        Assert.True(page.Comments.Single().Spoiler);
        Assert.Equal($"/{path}/123/comments/newest?page=2&limit=20", h.Paths.Last());
        Assert.Contains($"?type={type}&limit=100", h.Paths.First());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(page));
        Assert.True(json.RootElement.TryGetProperty("comments", out _));
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("show")]
    public async Task RepliesMustMatchBothMediaTypeAndId(string type)
    {
        using var h = new Harness();
        var other = type == "movie" ? "show" : "movie";
        h.Respond = r => r.RequestUri!.AbsolutePath.StartsWith("/search/")
            ? Harness.Json("""[{"type":"KIND","KIND":{"ids":{"trakt":123,"tmdb":42}}}]""".Replace("KIND", type))
            : Harness.Json("""{"type":"KIND","KIND":{"ids":{"trakt":123}}}""".Replace("KIND", other));
        var error = await Assert.ThrowsAsync<CommentsException>(() => h.Comments.GetMediaAsync(new(Guid.NewGuid(), type, new() { ["Tmdb"] = "42" }), "likes", 1, 20, 77, default));
        Assert.Equal("comment_not_on_item", error.Code);
        Assert.DoesNotContain(h.Paths, p => p.Contains("/replies"));
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("show")]
    public async Task MatchingRepliesArePaginated(string type)
    {
        using var h = new Harness();
        h.Respond = r => r.RequestUri!.AbsolutePath.StartsWith("/search/")
            ? Harness.Json("""[{"type":"KIND","KIND":{"ids":{"trakt":123,"tmdb":42}}}]""".Replace("KIND", type))
            : r.RequestUri.AbsolutePath.EndsWith("/item") ? Harness.Json("""{"type":"KIND","KIND":{"ids":{"trakt":123}}}""".Replace("KIND", type))
            : Harness.Json(Harness.Comment, 4, 61);
        var page = await h.Comments.GetMediaAsync(new(Guid.NewGuid(), type, new() { ["Tmdb"] = "42" }), "likes", 3, 20, 77, default);
        Assert.Equal(4, page.PageCount);
        Assert.Equal("/comments/77/replies?page=3&limit=20", h.Paths.Last());
    }

    [Fact]
    public async Task AmbiguousMovieIdsAreRejected()
    {
        using var h = new Harness();
        h.Respond = _ => Harness.Json("""[{"type":"movie","movie":{"ids":{"trakt":1,"tmdb":42}}},{"type":"movie","movie":{"ids":{"trakt":2,"tmdb":42}}}]""");
        Assert.Equal(409, (await Assert.ThrowsAsync<CommentsException>(() => h.Comments.GetMediaAsync(new(Guid.NewGuid(), "movie", new() { ["Tmdb"] = "42" }), "likes", 1, 20, null, default))).Status);
    }

    [Fact]
    public async Task ConflictingProviderIdsCannotPickTheWrongMovie()
    {
        using var h = new Harness();
        h.Respond = _ => Harness.Json("""[{"type":"movie","movie":{"ids":{"trakt":1,"tmdb":42,"imdb":"tt99999"}}}]""");
        Assert.Equal("media_unmatched", (await Assert.ThrowsAsync<CommentsException>(() => h.Comments.GetMediaAsync(new(Guid.NewGuid(), "movie", new() { ["Tmdb"] = "42", ["Imdb"] = "tt12345" }), "likes", 1, 20, null, default))).Code);
    }

    [Fact]
    public async Task MissingMediaIdsNeverTriggerTitleSearches()
    {
        using var h = new Harness();
        Assert.Equal(404, (await Assert.ThrowsAsync<CommentsException>(() => h.Comments.GetMediaAsync(new(Guid.NewGuid(), "show", []), "likes", 1, 20, null, default))).Status);
        Assert.Empty(h.Paths);
    }

    [Fact]
    public async Task Lookup404FallsBackToTheNextExactProvider()
    {
        using var h = new Harness();
        h.Respond = r => r.RequestUri!.AbsolutePath.StartsWith("/search/tmdb/") ? new(HttpStatusCode.NotFound)
            : r.RequestUri.AbsolutePath.StartsWith("/search/imdb/") ? Harness.Json("""[{"type":"movie","movie":{"ids":{"trakt":123,"tmdb":42,"imdb":"tt12345"}}}]""")
            : Harness.Json(Harness.Comment);
        var page = await h.Comments.GetMediaAsync(new(Guid.NewGuid(), "movie", new() { ["Tmdb"] = "42", ["Imdb"] = "tt12345" }), "likes", 1, 20, null, default);
        Assert.Equal("imdb", page.Match.Provider);
    }
}
