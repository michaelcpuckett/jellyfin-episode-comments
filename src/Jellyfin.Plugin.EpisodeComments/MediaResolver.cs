using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.EpisodeComments;

public sealed class MediaResolver(TraktClient trakt)
{
    private static readonly string[] Providers = ["trakt", "tmdb", "imdb", "tvdb"];

    public async Task<MediaMatch> ResolveAsync(MediaIdentity item, CancellationToken ct)
    {
        if (item.Type is not ("movie" or "show"))
            throw new CommentsException(400, "unsupported_item_type", "Only movies, series and episodes are supported.");
        foreach (var provider in Providers)
        {
            var id = ProviderId(item.ProviderIds, provider);
            if (id is null) continue;
            TraktResponse response;
            try
            {
                response = await trakt.GetAsync($"search/{provider}/{Uri.EscapeDataString(id)}?type={item.Type}&limit=100", ct).ConfigureAwait(false);
            }
            catch (CommentsException ex) when (ex.Code == "trakt_not_found") { continue; }
            if (response.Data.ValueKind != JsonValueKind.Array) throw EpisodeResolver.InvalidResponse();
            if (response.PageCount > 1)
                throw new CommentsException(409, "media_mapping_required", "The provider ID returned too many matches. Check this title's provider IDs.");
            var matches = response.Data.EnumerateArray().Where(x => x.Text("type") == item.Type)
                .Select(x => x.Object(item.Type))
                .Where(x => !Providers.Any(p => ProviderId(item.ProviderIds, p) is { } expected && x.Object("ids").Object(p) is var actual &&
                    actual.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) && !string.Equals(expected, actual.ToString(), StringComparison.OrdinalIgnoreCase)))
                .Select(x => x.Object("ids").Number("trakt")).Where(x => x > 0).Distinct().ToArray();
            if (matches.Length > 1)
                throw new CommentsException(409, "media_mapping_required", "The provider ID matches more than one Trakt title. Check this title's provider IDs.");
            if (matches.Length == 1) return new(item.Type, matches[0], "provider-id", provider);
        }
        throw new CommentsException(404, "media_unmatched", "No matching Trakt title was found. Add or correct the movie or series provider IDs in Jellyfin.");
    }

    private static string? ProviderId(Dictionary<string, string> ids, string provider)
    {
        var value = ids.FirstOrDefault(x => x.Key.Equals(provider, StringComparison.OrdinalIgnoreCase)).Value?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (provider == "imdb") return value.StartsWith("tt", StringComparison.Ordinal) && value.Length > 2 && value[2..].All(char.IsAsciiDigit) ? value : null;
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? value : null;
    }
}
