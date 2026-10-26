using System.Globalization;

namespace Jellyfin.Plugin.EpisodeComments;

public sealed class CommentsService(TraktClient trakt, EpisodeResolver resolver, MediaResolver mediaResolver)
{
    public async Task<CommentPage> GetAsync(EpisodeIdentity item, string sort, int page, int limit, long? parentId, CancellationToken ct)
    {
        Validate(sort, page, limit, parentId);
        var match = await resolver.ResolveAsync(item, ct).ConfigureAwait(false);
        var fetched = await FetchAsync("episode", match.EpisodeId,
            FormattableString.Invariant($"shows/{match.ShowId}/seasons/{match.Season}/episodes/{match.Episode}"), sort, page, limit, parentId, ct).ConfigureAwait(false);
        return new(item.ItemId, match, page, limit, fetched.Response.PageCount, fetched.Response.TotalItems, fetched.Response.FetchedAt, fetched.Comments);
    }

    public async Task<MediaCommentPage> GetMediaAsync(MediaIdentity item, string sort, int page, int limit, long? parentId, CancellationToken ct)
    {
        Validate(sort, page, limit, parentId);
        var match = await mediaResolver.ResolveAsync(item, ct).ConfigureAwait(false);
        var prefix = match.Type == "movie" ? "movies" : "shows";
        var fetched = await FetchAsync(match.Type, match.TraktId, FormattableString.Invariant($"{prefix}/{match.TraktId}"), sort, page, limit, parentId, ct).ConfigureAwait(false);
        return new(item.ItemId, match, page, limit, fetched.Response.PageCount, fetched.Response.TotalItems, fetched.Response.FetchedAt, fetched.Comments);
    }

    private static void Validate(string sort, int page, int limit, long? parentId)
    {
        if (sort is not ("newest" or "oldest" or "likes" or "replies" or "highest" or "lowest"))
            throw new CommentsException(400, "invalid_sort", "Sort must be newest, oldest, likes, replies, highest or lowest.");
        if (page < 1 || page > 10000 || limit < 1 || limit > 100 || parentId <= 0)
            throw new CommentsException(400, "invalid_pagination", "Page must be 1–10000, limit 1–100, and comment IDs positive.");
    }

    private async Task<(TraktResponse Response, CommentDto[] Comments)> FetchAsync(string type, long traktId, string mediaPath, string sort, int page, int limit, long? parentId, CancellationToken ct)
    {
        string path;
        if (parentId.HasValue)
        {
            var attached = await trakt.GetAsync(FormattableString.Invariant($"comments/{parentId}/item"), ct).ConfigureAwait(false);
            if (attached.Data.Text("type") != type || attached.Data.Object(type).Object("ids").Number("trakt") != traktId)
                throw new CommentsException(404, type == "episode" ? "comment_not_on_episode" : "comment_not_on_item", "This comment is not attached to this title.");
            path = FormattableString.Invariant($"comments/{parentId}/replies?page={page}&limit={limit}");
        }
        else path = FormattableString.Invariant($"{mediaPath}/comments/{sort}?page={page}&limit={limit}");
        var response = await trakt.GetAsync(path, ct).ConfigureAwait(false);
        if (response.Data.ValueKind != System.Text.Json.JsonValueKind.Array) throw EpisodeResolver.InvalidResponse();
        var comments = response.Data.EnumerateArray().Select(comment =>
        {
            var id = comment.Number("id");
            if (id <= 0) throw EpisodeResolver.InvalidResponse();
            var user = comment.Object("user");
            var created = DateTimeOffset.TryParse(comment.Text("created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : (DateTimeOffset?)null;
            return new CommentDto(id, comment.Number("parent_id") is > 0 and var parent ? parent : null,
                user.Text("username") ?? user.Text("name") ?? "Trakt user", comment.Text("comment") ?? "",
                comment.Flag("spoiler"), comment.Flag("review"), (int)Math.Clamp(comment.Number("likes"), 0, int.MaxValue),
                (int)Math.Clamp(comment.Number("replies"), 0, int.MaxValue), created, FormattableString.Invariant($"https://trakt.tv/comments/{id}"));
        }).ToArray();
        return (response, comments);
    }
}
