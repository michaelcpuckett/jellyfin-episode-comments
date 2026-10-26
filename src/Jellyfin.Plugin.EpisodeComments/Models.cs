using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.EpisodeComments;

public sealed record CapabilitiesDto(
    [property: JsonPropertyName("pluginId")] string PluginId,
    [property: JsonPropertyName("apiVersion")] int ApiVersion,
    [property: JsonPropertyName("configured")] bool Configured,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("supportedItemTypes")] IReadOnlyList<string> SupportedItemTypes);

public sealed record MediaIdentity(Guid ItemId, string Type, Dictionary<string, string> ProviderIds);

public sealed record MediaMatch(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("traktId")] long TraktId,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("provider")] string Provider);

public sealed record MediaCommentPage(
    [property: JsonPropertyName("itemId")] Guid ItemId,
    [property: JsonPropertyName("match")] MediaMatch Match,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("pageCount")] int PageCount,
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("fetchedAt")] DateTimeOffset FetchedAt,
    [property: JsonPropertyName("comments")] IReadOnlyList<CommentDto> Comments);

public sealed record EpisodeIdentity(
    Guid ItemId, Dictionary<string, string> EpisodeIds, Dictionary<string, string> SeriesIds,
    int? Season, int? Episode, int? EpisodeEnd, string? DisplayOrder, DateTime? PremiereDate);

public sealed record EpisodeMatch(
    [property: JsonPropertyName("showId")] long ShowId,
    [property: JsonPropertyName("episodeId")] long EpisodeId,
    [property: JsonPropertyName("season")] int Season,
    [property: JsonPropertyName("episode")] int Episode,
    [property: JsonPropertyName("method")] string Method);

public sealed record CommentDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("parentId")] long? ParentId,
    [property: JsonPropertyName("author")] string Author,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("spoiler")] bool Spoiler,
    [property: JsonPropertyName("review")] bool Review,
    [property: JsonPropertyName("likes")] int Likes,
    [property: JsonPropertyName("replyCount")] int ReplyCount,
    [property: JsonPropertyName("createdAt")] DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("url")] string Url);

public sealed record CommentPage(
    [property: JsonPropertyName("itemId")] Guid ItemId,
    [property: JsonPropertyName("match")] EpisodeMatch Match,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("pageCount")] int PageCount,
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("fetchedAt")] DateTimeOffset FetchedAt,
    [property: JsonPropertyName("comments")] IReadOnlyList<CommentDto> Comments);

public sealed class CommentsException(int status, string code, string message, int? retryAfter = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public int? RetryAfter { get; } = retryAfter;
}
