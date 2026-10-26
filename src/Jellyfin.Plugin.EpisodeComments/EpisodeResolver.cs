using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.EpisodeComments;

public sealed class EpisodeResolver(TraktClient trakt, Func<PluginConfiguration> configuration)
{
    private static readonly string[] Providers = ["trakt", "tmdb", "tvdb", "imdb"];

    public async Task<EpisodeMatch> ResolveAsync(EpisodeIdentity item, CancellationToken ct)
    {
        var config = configuration();
        var mappings = (config.Mappings ?? []).Where(m => m.ItemId == item.ItemId).ToArray();
        if (mappings.Length > 1) throw MappingError("There are duplicate overrides for this episode.");
        if (mappings.Length == 1)
        {
            var map = mappings[0];
            if (map.TraktShowId <= 0 || map.Season < 0 || map.Episode <= 0)
                throw MappingError("The episode override is invalid.");
            return await ByNumbers(map.TraktShowId, map.Season, map.Episode, "override", null, ct).ConfigureAwait(false);
        }
        if (item.EpisodeEnd.HasValue && item.EpisodeEnd != item.Episode)
            throw MappingError("This file contains multiple episodes. Add an explicit episode override in the plugin settings.");

        // Episode IDs remain reliable when a library uses DVD or absolute ordering.
        foreach (var provider in Providers)
        {
            var id = ProviderId(item.EpisodeIds, provider);
            if (id is null) continue;
            var data = await Lookup(provider, id, "episode", ct).ConfigureAwait(false);
            var candidates = data.EnumerateArray().Where(x => x.Text("type") == "episode")
                .Where(x => !ConflictsWithSeries(x.Object("show"), item.SeriesIds)).ToArray();
            if (candidates.Length == 0) continue;
            var distinct = candidates.DistinctBy(x => x.Object("episode").Object("ids").Number("trakt")).ToArray();
            if (distinct.Length != 1) throw MappingError("The episode provider ID matches more than one Trakt episode.");
            var result = distinct[0];
            return MakeMatch(result.Object("show").Object("ids").Number("trakt"), result.Object("episode"), "episode-id");
        }

        if (!config.AllowAiredNumberFallback || (!string.IsNullOrWhiteSpace(item.DisplayOrder) && !item.DisplayOrder.Equals("aired", StringComparison.OrdinalIgnoreCase)))
            throw MappingError("A Trakt-compatible episode provider ID or explicit override is required for this episode order.");
        if (item.Season is null or < 0 || item.Episode is null or <= 0)
            throw MappingError("The episode is missing a valid aired season or episode number.");
        long showId = 0;
        foreach (var provider in Providers)
        {
            var id = ProviderId(item.SeriesIds, provider);
            if (id is null) continue;
            var data = await Lookup(provider, id, "show", ct).ConfigureAwait(false);
            var matches = data.EnumerateArray().Where(x => x.Text("type") == "show")
                .Where(x => !ConflictsWithSeries(x.Object("show"), item.SeriesIds))
                .Select(x => x.Object("show").Object("ids").Number("trakt")).Where(x => x > 0).Distinct().ToArray();
            if (matches.Length > 1) throw MappingError("The series provider ID matches more than one Trakt show.");
            if (matches.Length == 1) { showId = matches[0]; break; }
        }
        if (showId == 0)
            throw new CommentsException(404, "episode_unmatched", "No matching Trakt episode was found. Add provider IDs or an explicit override.");
        return await ByNumbers(showId, item.Season.Value, item.Episode.Value, "aired-numbers", item.PremiereDate, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> Lookup(string provider, string id, string type, CancellationToken ct)
    {
        try
        {
            var response = await trakt.GetAsync($"search/{provider}/{Uri.EscapeDataString(id)}?type={type}&limit=100", ct).ConfigureAwait(false);
            if (response.Data.ValueKind != JsonValueKind.Array) throw InvalidResponse();
            // Refuse a partial result set, which could hide an ambiguous match.
            if (response.PageCount > 1) throw MappingError("The provider ID returned too many matches. Add an explicit override.");
            return response.Data;
        }
        catch (CommentsException ex) when (ex.Code == "trakt_not_found")
        {
            return JsonSerializer.SerializeToElement(Array.Empty<object>());
        }
    }

    private async Task<EpisodeMatch> ByNumbers(long showId, int season, int episode, string method, DateTime? premiereDate, CancellationToken ct)
    {
        var response = await trakt.GetAsync(FormattableString.Invariant($"shows/{showId}/seasons/{season}/episodes/{episode}?extended=full"), ct).ConfigureAwait(false);
        var match = MakeMatch(showId, response.Data, method);
        if (match.Season != season || match.Episode != episode) throw MappingError("Trakt returned different episode numbers.");
        if (premiereDate.HasValue && response.Data.Text("first_aired") is { } aired &&
            DateTimeOffset.TryParse(aired, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) &&
            Math.Abs((date.UtcDateTime.Date - premiereDate.Value.Date).TotalDays) > 1)
            throw MappingError("The episode's air date disagrees with Trakt. Add an episode provider ID or override.");
        return match;
    }

    private static EpisodeMatch MakeMatch(long showId, JsonElement episode, string method)
    {
        var episodeId = episode.Object("ids").Number("trakt");
        var season = episode.Number("season", -1);
        var number = episode.Number("number");
        if (showId <= 0 || episodeId <= 0 || season < 0 || season > int.MaxValue || number <= 0 || number > int.MaxValue)
            throw InvalidResponse();
        return new(showId, episodeId, (int)season, (int)number, method);
    }

    private static bool ConflictsWithSeries(JsonElement show, Dictionary<string, string> ids) =>
        Providers.Any(provider => ProviderId(ids, provider) is { } expected && show.Object("ids").Object(provider) is var actual &&
            actual.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) && !string.Equals(expected, actual.ToString(), StringComparison.OrdinalIgnoreCase));

    private static string? ProviderId(Dictionary<string, string> ids, string provider)
    {
        var value = ids.FirstOrDefault(x => x.Key.Equals(provider, StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (provider == "imdb") return value.StartsWith("tt", StringComparison.Ordinal) && value.Length > 2 && value[2..].All(char.IsAsciiDigit) ? value : null;
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? value : null;
    }

    private static CommentsException MappingError(string text) => new(409, "episode_mapping_required", text);
    internal static CommentsException InvalidResponse() => new(502, "trakt_invalid_response", "Trakt returned an incomplete response.");
}

internal static class JsonExtensions
{
    internal static JsonElement Object(this JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) ? child : default;
    internal static string? Text(this JsonElement value, string name) => value.Object(name) is var field && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    internal static long Number(this JsonElement value, string name, long fallback = 0) => value.Object(name) is var field && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out var number) ? number : fallback;
    internal static bool Flag(this JsonElement value, string name) => value.Object(name).ValueKind == JsonValueKind.True;
}
