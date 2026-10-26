# Episode Comments for Jellyfin 12

A read-only server plugin that exposes Trakt episode discussions to authenticated Jellyfin clients. The accompanying changes in your `jellyfin-androidtv` checkout add a D-pad friendly Comments dialog to episode details.

The Trakt Client ID stays on the Jellyfin server. Android TV uses its existing Jellyfin session and never contacts Trakt directly. Other clients can use the same versioned HTTP API.

## Install

1. Stop Jellyfin 12.
2. Extract the plugin install ZIP into your Jellyfin **plugins directory**. The resulting layout must be `plugins/EpisodeComments_0.1.0.0/Jellyfin.Plugin.EpisodeComments.dll` and `meta.json` beside it. For Docker this normally means `/config/plugins/EpisodeComments_0.1.0.0/`; use your server's configured data directory on other platforms.
3. Start Jellyfin, then open **Dashboard → Plugins → Episode Comments → Settings**.
4. Enter your Trakt app's **Client ID** and save. No client secret or Trakt user OAuth is needed. Register an app at [Trakt Developer](https://developer.trakt.tv/docs/create-an-app).
5. Install the accompanying **stats** APK on your Android TV, or build your modified checkout. It uses your existing stats variant: release optimizations and resource shrinking, the `.debug` package suffix and debug signing key, and a non-debuggable APK.
6. Open an episode's details and select **Comments** (possibly in More). Select a comment to read it, reveal spoilers, or open replies. Sorting and previous/next page buttons work with a remote.

To remove the plugin, stop Jellyfin, remove only the `EpisodeComments_0.1.0.0` plugin folder and restart Jellyfin. Android TV hides Comments when the capability check fails. It rechecks when details load and immediately before opening the dialog. An already open dialog handles unavailable endpoints as an error with Retry; playback and other details features remain independent.

## Android TV changes

Changes were made in `C:\Users\micha\sites\jellyfin-androidtv` without a commit. The accompanying patch includes the added files and changes to the episode details action list and overflow menu.

- The app checks `/EpisodeComments/v1/Capabilities` using its active Jellyfin SDK client. It requires this plugin's ID, API version 1, and `configured: true`.
- A missing, unconfigured, incompatible, unauthorized or unreachable plugin hides the button. Discovery is asynchronous and times out after 2.5 seconds. It is scoped to the current details view and item; no availability state is carried between servers or users.
- A second check at selection handles removal while the details screen stays open.
- Comments, replies, sort and page state are kept within the dialog. Spoilers are hidden until explicitly revealed, including comments containing Trakt's inline `[spoiler]` tags. Text is rendered as plain text; HTML and Markdown are not executed or rendered.
- This adds a client UI to your custom Android TV app. Stock clients need their own support for this API. No injected Jellyfin web-client panel is included in this version.

## Matching

1. An administrator's explicit per-item mapping takes precedence.
2. Otherwise, a single episode is looked up by its own Trakt, TMDb, TVDb or IMDb ID. Conflicting series IDs and ambiguous results are rejected.
3. If enabled, aired-order series can fall back to their series provider ID plus Jellyfin's season/episode numbers. A known air-date disagreement is rejected. Specials use season 0.
4. Combined episode files require an explicit mapping to **one chosen discussion**. DVD/absolute/other display orders require an episode ID or explicit mapping. The plugin does not guess from a title.

In settings, overrides are a JSON array. Obtain the episode's Jellyfin item ID from its detail URL or API and use the numeric Trakt **show** ID:

```json
[
  {
    "ItemId": "11111111-2222-3333-4444-555555555555",
    "TraktShowId": 1388,
    "Season": 1,
    "Episode": 1
  }
]
```

## Client API

All routes are under your existing Jellyfin base URL, including a configured subpath. Use the normal Jellyfin authorization header. A signed-in user session is required for episode data; userless administrator API keys are rejected. A caller cannot choose a different `userId` to bypass permissions.

| Method | Route | Query |
| --- | --- | --- |
| GET | `/EpisodeComments/v1/Capabilities` | none |
| GET | `/EpisodeComments/v1/Items/{itemId}/Comments` | `sort=likes`, `page=1`, `limit=20` |
| GET | `/EpisodeComments/v1/Items/{itemId}/Comments/{commentId}/Replies` | `page=1`, `limit=20` |

`itemId` is a Jellyfin episode GUID. `commentId` is a numeric Trakt comment ID. Replies are checked against the resolved episode before fetching. Sort values: `likes`, `newest`, `oldest`, `replies`, `highest`, `lowest`. Page range: 1–10000; limit: 1–100. Replies use Trakt's upstream order and do not accept sorting.

Capabilities are authenticated, do not call Trakt, and return no credentials:

```json
{
  "pluginId": "79672a4d-f247-4370-9e41-f508bd891631",
  "apiVersion": 1,
  "configured": true,
  "provider": "Trakt"
}
```

Comment and reply pages share this JSON shape:

```json
{
  "itemId": "11111111-2222-3333-4444-555555555555",
  "match": { "showId": 1388, "episodeId": 73482, "season": 1, "episode": 1, "method": "episode-id" },
  "page": 1,
  "pageSize": 20,
  "pageCount": 3,
  "totalItems": 42,
  "fetchedAt": "2026-10-26T14:00:00Z",
  "comments": [
    {
      "id": 123,
      "parentId": null,
      "author": "reader",
      "text": "Example comment",
      "spoiler": false,
      "review": false,
      "likes": 12,
      "replyCount": 2,
      "createdAt": "2026-10-25T12:00:00Z",
      "url": "https://trakt.tv/comments/123"
    }
  ]
}
```

The IDs and text in this example are illustrative. Clients must treat comment text and author names as untrusted plain text and explicitly hide spoilers. `spoiler` is metadata for clients; the API still returns comment text. `match.method` is `episode-id`, `aired-numbers` or `override`. `fetchedAt` is the upstream page's fetch time, including for cached pages. An empty discussion returns HTTP 200 with an empty array; an unmatched episode returns an error.

Errors use HTTP status plus Problem Details with a machine-readable `code` and human-readable `detail`:

| Status | Meaning |
| --- | --- |
| 400 | Invalid sorting, pagination or comment ID |
| 401 / 403 | Missing authorization or a userless session |
| 404 | Episode unavailable to this user, no match, or comment not attached to the episode |
| 409 | Ambiguous numbering, combined file or a mapping override required |
| 429 | Trakt rate limit; respect the `Retry-After` header |
| 502 / 504 | Upstream failure, invalid response or timeout |
| 503 | Trakt Client ID missing or rejected |

## Caching and privacy

Only requested data is fetched. The plugin does not scan or prefetch the library. Successful upstream responses are cached in server memory for 15 minutes by default (configurable 1–1440 minutes), bounded to approximately 32 MiB. Cache misses are serialized to coalesce identical requests and bound upstream concurrency. Rate-limit cooldown is shared; errors are not cached as successful empty discussions. The cache is cleared on server restart, and changes to the Trakt Client ID partition cached responses.

The plugin sends requested episode/show provider IDs and comment IDs to Trakt with the server operator's Client ID and server IP. It sends no Jellyfin usernames, session tokens, playback history or media paths. Public comments are shared across Jellyfin users; Trakt block lists are not applied because this version has no user OAuth. Trakt usernames, public comments and metadata are retained only in the bounded in-memory cache. Configuration (including Client ID and mapping overrides) is stored in Jellyfin's plugin XML configuration. Remove that configuration as well if you want to delete the plugin settings.

## Build and verify

Requires a .NET 10 SDK and Jellyfin 12.x. The plugin references the 12.0 API and excludes server dependency DLLs from its package.

```powershell
dotnet test tests/EpisodeComments.Tests -c Release
./build.ps1
```

For Android TV, use Java 21 and your Android SDK, then run in your checkout:

```powershell
./gradlew.bat :app:assembleStats "-Pjellyfin.version=0.20.0-stats.13" --console=plain
./gradlew.bat :app:testDebugUnitTest --tests org.jellyfin.androidtv.ui.itemdetail.EpisodeCommentsTests --console=plain
```

Server tests use a fake Trakt HTTP handler and an ASP.NET test host. They cover authentication, per-user item lookups, capability discovery, matching, specials, ambiguity, combined episodes, pagination, replies ownership, caching, rate limits and upstream failures. Android unit tests cover plugin removal/discovery, spoiler detection, API paths and pagination. See `VALIDATION.md` for the checks completed in this workspace and remaining live checks.

## References

- [Jellyfin plugin template](https://github.com/jellyfin/jellyfin-plugin-template)
- [Jellyfin 12 source](https://github.com/jellyfin/jellyfin/tree/v12.3)
- [Trakt API source contracts](https://github.com/trakt/trakt-api)
- [Trakt episode comments](https://docs.trakt.tv/reference/getshowsepisodecomments)
- [Trakt replies](https://docs.trakt.tv/reference/getcommentsreplies)
- [Trakt ID lookup](https://docs.trakt.tv/reference/getsearchlookup)
- [Trakt API use policy](https://developer.trakt.tv/docs/api-use-policy)

Licensed under GPL-3.0-only. Trakt and Jellyfin are their respective owners' names; this is an independent integration.
