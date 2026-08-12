using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PixelReel.Network;

namespace PixelReel.Jellyfin
{
    /// <summary>
    /// Talks to a Jellyfin server over its REST API. Server-side only: the API key
    /// never leaves this class except embedded in a playback URL (see StreamUrl).
    /// </summary>
    public class JellyfinClient
    {
        private readonly HttpClient http;
        private readonly string baseUrl;
        private readonly string apiKey;
        private readonly string userId;

        public JellyfinClient(string baseUrl, string apiKey, string userId, int timeoutSeconds)
        {
            this.baseUrl = (baseUrl ?? "").TrimEnd('/');
            this.apiKey = apiKey ?? "";
            this.userId = userId ?? "";

            http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(timeoutSeconds <= 0 ? 15 : timeoutSeconds)
            };
            // Jellyfin accepts the token as a header, which keeps it out of request logs
            // for everything except the stream URL itself.
            http.DefaultRequestHeaders.Add("X-Emby-Token", this.apiKey);
            http.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(baseUrl) &&
            !string.IsNullOrWhiteSpace(apiKey) &&
            !string.IsNullOrWhiteSpace(userId);

        /// <summary>Server name if reachable, otherwise throws.</summary>
        public async Task<string> PingAsync(CancellationToken ct = default)
        {
            using JsonDocument doc = await GetJsonAsync("/System/Info/Public", ct);
            return doc.RootElement.TryGetProperty("ServerName", out JsonElement n)
                ? n.GetString()
                : "Jellyfin";
        }

        /// <summary>The user's libraries (Movies, Shows, etc).</summary>
        public async Task<List<BrowseEntry>> GetLibrariesAsync(CancellationToken ct = default)
        {
            using JsonDocument doc = await GetJsonAsync($"/Users/{userId}/Views", ct);
            List<BrowseEntry> list = new List<BrowseEntry>();

            if (doc.RootElement.TryGetProperty("Items", out JsonElement items))
            {
                foreach (JsonElement item in items.EnumerateArray())
                {
                    string type = Str(item, "CollectionType");
                    // Only libraries we can actually play video from.
                    if (type != null && type != "movies" && type != "tvshows" && type != "homevideos") continue;

                    list.Add(new BrowseEntry
                    {
                        Id = Str(item, "Id"),
                        Title = Str(item, "Name"),
                        Kind = EntryKind.Library,
                        Detail = type
                    });
                }
            }
            return list;
        }

        /// <summary>Movies and series directly inside a library.</summary>
        public async Task<List<BrowseEntry>> GetLibraryItemsAsync(string parentId, CancellationToken ct = default)
        {
            string path = $"/Users/{userId}/Items" +
                          $"?ParentId={Uri.EscapeDataString(parentId)}" +
                          "&IncludeItemTypes=Movie,Series" +
                          "&Recursive=true" +
                          "&SortBy=SortName&SortOrder=Ascending" +
                          "&Fields=ProductionYear,UserData,RunTimeTicks" +
                          "&Limit=500";
            return await ItemsAsync(path, ct);
        }

        public async Task<List<BrowseEntry>> GetSeasonsAsync(string seriesId, CancellationToken ct = default)
        {
            string path = $"/Shows/{Uri.EscapeDataString(seriesId)}/Seasons?userId={userId}&Fields=UserData";
            return await ItemsAsync(path, ct);
        }

        public async Task<List<BrowseEntry>> GetEpisodesAsync(string seasonId, CancellationToken ct = default)
        {
            string path = $"/Shows/{Uri.EscapeDataString(await SeriesIdOfSeasonAsync(seasonId, ct))}/Episodes" +
                          $"?seasonId={Uri.EscapeDataString(seasonId)}" +
                          $"&userId={userId}" +
                          "&Fields=UserData,RunTimeTicks,Overview";
            return await ItemsAsync(path, ct);
        }

        public async Task<List<BrowseEntry>> GetRecentAsync(CancellationToken ct = default)
        {
            string path = $"/Users/{userId}/Items/Latest" +
                          "?IncludeItemTypes=Movie,Episode" +
                          "&Limit=40" +
                          "&Fields=ProductionYear,UserData,RunTimeTicks";

            using JsonDocument doc = await GetJsonAsync(path, ct);
            List<BrowseEntry> list = new List<BrowseEntry>();

            // /Items/Latest returns a bare array, not an Items wrapper.
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in doc.RootElement.EnumerateArray())
                {
                    BrowseEntry e = ToEntry(item);
                    if (e != null) list.Add(e);
                }
            }
            return list;
        }

        /// <summary>
        /// The next episode after the given one, for autoplay. Null when the season
        /// and series are finished.
        /// </summary>
        public async Task<BrowseEntry> GetNextEpisodeAsync(string episodeId, CancellationToken ct = default)
        {
            using JsonDocument ep = await GetJsonAsync($"/Users/{userId}/Items/{Uri.EscapeDataString(episodeId)}", ct);
            string seriesId = Str(ep.RootElement, "SeriesId");
            if (string.IsNullOrEmpty(seriesId)) return null;

            string path = $"/Shows/{Uri.EscapeDataString(seriesId)}/Episodes" +
                          $"?userId={userId}&Fields=UserData,RunTimeTicks";

            List<BrowseEntry> all = await ItemsAsync(path, ct);
            for (int i = 0; i < all.Count - 1; i++)
            {
                if (all[i].Id == episodeId) return all[i + 1];
            }
            return null;
        }

        public async Task<BrowseEntry> GetItemAsync(string itemId, CancellationToken ct = default)
        {
            using JsonDocument doc = await GetJsonAsync($"/Users/{userId}/Items/{Uri.EscapeDataString(itemId)}", ct);
            return ToEntry(doc.RootElement);
        }

        /// <summary>
        /// URL of an external subtitle track, or null when the item has none.
        ///
        /// Jellyfin keeps many subtitles as separate files rather than muxing them into
        /// the video, so they never reach VLC through the video stream and have to be
        /// attached alongside it.
        /// </summary>
        public async Task<string> GetSubtitleUrlAsync(string itemId, string preferredLanguage,
                                                      CancellationToken ct = default)
        {
            using JsonDocument doc = await GetJsonAsync(
                $"/Users/{userId}/Items/{Uri.EscapeDataString(itemId)}?Fields=MediaSources", ct);

            if (!doc.RootElement.TryGetProperty("MediaSources", out JsonElement sources)) return null;

            foreach (JsonElement source in sources.EnumerateArray())
            {
                string sourceId = Str(source, "Id") ?? itemId;
                if (!source.TryGetProperty("MediaStreams", out JsonElement streams)) continue;

                int fallbackIndex = -1;
                string fallbackCodec = null;

                foreach (JsonElement stream in streams.EnumerateArray())
                {
                    if (Str(stream, "Type") != "Subtitle") continue;

                    bool external = stream.TryGetProperty("IsExternal", out JsonElement ext) &&
                                    ext.ValueKind == JsonValueKind.True;
                    bool deliverable = stream.TryGetProperty("IsTextSubtitleStream", out JsonElement txt) &&
                                       txt.ValueKind == JsonValueKind.True;

                    // Embedded image subs (PGS/VobSub) can't be delivered as text and are
                    // already inside the video stream anyway, so skip them here.
                    if (!external && !deliverable) continue;

                    int? index = Int(stream, "Index");
                    if (!index.HasValue) continue;

                    string codec = Str(stream, "Codec") ?? "srt";
                    string language = Str(stream, "Language");
                    bool isDefault = stream.TryGetProperty("IsDefault", out JsonElement def) &&
                                     def.ValueKind == JsonValueKind.True;

                    bool languageMatches = preferredLanguage != null && language != null &&
                        language.StartsWith(preferredLanguage, StringComparison.OrdinalIgnoreCase);

                    if (languageMatches)
                    {
                        return SubtitleUrl(itemId, sourceId, index.Value, codec);
                    }

                    if (fallbackIndex < 0 || isDefault)
                    {
                        fallbackIndex = index.Value;
                        fallbackCodec = codec;
                    }
                }

                if (fallbackIndex >= 0) return SubtitleUrl(itemId, sourceId, fallbackIndex, fallbackCodec);
            }

            return null;
        }

        private string SubtitleUrl(string itemId, string sourceId, int index, string codec)
        {
            // Jellyfin converts on the fly, so asking for .srt works even when the source
            // is .ass or .sub.
            string ext = string.Equals(codec, "vtt", StringComparison.OrdinalIgnoreCase) ? "vtt" : "srt";
            return $"{baseUrl}/Videos/{Uri.EscapeDataString(itemId)}" +
                   $"/{Uri.EscapeDataString(sourceId)}/Subtitles/{index}/Stream.{ext}" +
                   $"?api_key={Uri.EscapeDataString(apiKey)}";
        }

        /// <summary>
        /// A direct-play URL for the given item.
        ///
        /// The API key is unavoidably part of this URL, because the client's VLC has to
        /// authenticate to fetch the stream. That's a real disclosure: any player who can
        /// open a display can read the token from their own logs. Mitigate it by giving
        /// pixelReel its own Jellyfin user with playback-only permissions rather than an
        /// admin key.
        /// </summary>
        public string StreamUrl(string itemId, long startSeconds)
        {
            string url = $"{baseUrl}/Videos/{Uri.EscapeDataString(itemId)}/stream" +
                         $"?static=true&api_key={Uri.EscapeDataString(apiKey)}";
            if (startSeconds > 0)
            {
                // Ticks are 100ns units.
                url += "&startTimeTicks=" + (startSeconds * 10_000_000L);
            }
            return url;
        }

        // ---------------- internals ----------------

        private async Task<string> SeriesIdOfSeasonAsync(string seasonId, CancellationToken ct)
        {
            using JsonDocument doc = await GetJsonAsync($"/Users/{userId}/Items/{Uri.EscapeDataString(seasonId)}", ct);
            return Str(doc.RootElement, "SeriesId") ?? seasonId;
        }

        private async Task<List<BrowseEntry>> ItemsAsync(string path, CancellationToken ct)
        {
            using JsonDocument doc = await GetJsonAsync(path, ct);
            List<BrowseEntry> list = new List<BrowseEntry>();

            if (doc.RootElement.TryGetProperty("Items", out JsonElement items))
            {
                foreach (JsonElement item in items.EnumerateArray())
                {
                    BrowseEntry e = ToEntry(item);
                    if (e != null) list.Add(e);
                }
            }
            return list;
        }

        private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
        {
            using HttpResponseMessage res = await http.GetAsync(baseUrl + path, ct);
            if (!res.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Jellyfin returned {(int)res.StatusCode} {res.ReasonPhrase} for {path}");
            }
            string body = await res.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(body);
        }

        private static BrowseEntry ToEntry(JsonElement item)
        {
            string type = Str(item, "Type");
            EntryKind kind;
            switch (type)
            {
                case "Movie": kind = EntryKind.Movie; break;
                case "Series": kind = EntryKind.Series; break;
                case "Season": kind = EntryKind.Season; break;
                case "Episode": kind = EntryKind.Episode; break;
                default: return null;
            }

            string detail = null;
            if (kind == EntryKind.Episode)
            {
                int? s = Int(item, "ParentIndexNumber");
                int? e = Int(item, "IndexNumber");
                if (s.HasValue && e.HasValue) detail = $"S{s.Value:00}E{e.Value:00}";
                string series = Str(item, "SeriesName");
                if (series != null) detail = detail == null ? series : detail + "  " + series;
            }
            else
            {
                int? year = Int(item, "ProductionYear");
                if (year.HasValue) detail = year.Value.ToString();
            }

            long resume = 0;
            if (item.TryGetProperty("UserData", out JsonElement ud) &&
                ud.TryGetProperty("PlaybackPositionTicks", out JsonElement pt) &&
                pt.TryGetInt64(out long ticks))
            {
                resume = ticks / 10_000_000L;
            }

            return new BrowseEntry
            {
                Id = Str(item, "Id"),
                Title = Str(item, "Name") ?? "(untitled)",
                Kind = kind,
                Detail = detail,
                ResumeSeconds = resume
            };
        }

        private static string Str(JsonElement el, string prop)
        {
            return el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }

        private static int? Int(JsonElement el, string prop)
        {
            return el.TryGetProperty(prop, out JsonElement v) && v.TryGetInt32(out int i) ? i : (int?)null;
        }
    }
}
