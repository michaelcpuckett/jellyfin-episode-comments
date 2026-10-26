using System.Security.Claims;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.EpisodeComments;

[ApiController]
[Authorize]
[Route("EpisodeComments/v1")]
[Produces("application/json")]
public sealed class CommentsController(ILibraryManager library, CommentsService comments, Func<PluginConfiguration> configuration) : ControllerBase
{
    [HttpGet("Capabilities")]
    [ProducesResponseType(typeof(CapabilitiesDto), 200)]
    public ActionResult<CapabilitiesDto> GetCapabilities() => Ok(new CapabilitiesDto(
        "79672a4d-f247-4370-9e41-f508bd891631", 1, !string.IsNullOrWhiteSpace(configuration().TraktClientId), "Trakt"));

    [HttpGet("Items/{itemId:guid}/Comments")]
    [ProducesResponseType(typeof(CommentPage), 200)]
    public Task<ActionResult<CommentPage>> GetComments(Guid itemId, [FromQuery] string sort = "likes", [FromQuery] int page = 1, [FromQuery] int limit = 20, CancellationToken cancellationToken = default) =>
        GetPage(itemId, sort, page, limit, null, cancellationToken);

    [HttpGet("Items/{itemId:guid}/Comments/{commentId:long}/Replies")]
    [ProducesResponseType(typeof(CommentPage), 200)]
    public Task<ActionResult<CommentPage>> GetReplies(Guid itemId, long commentId, [FromQuery] int page = 1, [FromQuery] int limit = 20, CancellationToken cancellationToken = default) =>
        GetPage(itemId, "oldest", page, limit, commentId, cancellationToken);

    private async Task<ActionResult<CommentPage>> GetPage(Guid itemId, string sort, int page, int limit, long? parent, CancellationToken ct)
    {
        // Do not accept a caller-supplied user ID or an administrator API key without user context.
        // This is Jellyfin's authenticated user claim, populated by its authentication handler.
        if (!Guid.TryParse(User.FindFirstValue("Jellyfin-UserId"), out var userId) || userId == Guid.Empty)
            return StatusCode(403, Error(403, "user_session_required", "Use an authenticated Jellyfin user session."));
        var episode = library.GetItemById<Episode>(itemId, userId);
        if (episode is null) return NotFound(Error(404, "episode_not_found", "The episode was not found or is unavailable to this user."));
        var series = library.GetItemById<Series>(episode.SeriesId, userId);
        var identity = new EpisodeIdentity(itemId, new(episode.ProviderIds), series is null ? [] : new(series.ProviderIds),
            episode.ParentIndexNumber, episode.IndexNumber, episode.IndexNumberEnd, series?.DisplayOrder, episode.PremiereDate);
        try { return Ok(await comments.GetAsync(identity, sort, page, limit, parent, ct).ConfigureAwait(false)); }
        catch (CommentsException ex)
        {
            if (ex.RetryAfter.HasValue) Response.Headers.RetryAfter = ex.RetryAfter.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return StatusCode(ex.Status, Error(ex.Status, ex.Code, ex.Message));
        }
    }

    private static ProblemDetails Error(int status, string code, string detail) => new()
    {
        Status = status, Title = "Episode Comments", Detail = detail, Extensions = { ["code"] = code }
    };
}
