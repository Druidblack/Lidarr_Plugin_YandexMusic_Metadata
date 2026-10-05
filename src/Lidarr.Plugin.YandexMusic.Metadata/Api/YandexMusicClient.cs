using System.Globalization;
using System.Net;
using System.Threading;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Exceptions;

namespace Lidarr.Plugin.YandexMusicMetadata.Api;

internal sealed class YandexMusicClient
{
    private const string BaseUrl = "https://api.music.yandex.net";
    private const int DirectAlbumsPageSize = 100;
    private const int MaxDirectAlbumPages = 1000;
    private const int MaxRateLimitRetries = 4;

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromSeconds(60);

    // Shared across every YandexMusicClient instance (metadata + import lists).
    // This prevents concurrent Lidarr refresh jobs from bypassing the throttle.
    private static readonly object ApiRequestLock = new();
    private static DateTime _lastRequestUtc = DateTime.MinValue;

    private static readonly object ArtistProfileCacheLock = new();
    private static readonly Dictionary<long, ArtistProfileCacheEntry> ArtistProfileCache = new();

    private static readonly object DirectAlbumsCacheLock = new();
    private static readonly Dictionary<long, DirectAlbumsCacheEntry> DirectAlbumsCache = new();

    private static readonly object AlbumCacheLock = new();
    private static readonly Dictionary<long, AlbumCacheEntry> AlbumCache = new();

    private readonly IHttpClient _httpClient;
    private readonly Logger _logger;

    public YandexMusicClient(IHttpClient httpClient, Logger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Returns the complete Yandex artist snapshot used by Lidarr.
    /// Basic artist data comes from /artists/{id}; descriptive data, links and
    /// photos are enriched from /about-artist; the direct discography is loaded
    /// page-by-page from /direct-albums so it is not limited to the album list
    /// embedded in /artists/{id}.
    /// </summary>
    public YandexArtistResult GetArtist(long id)
    {
        // Reuse the same enriched profile that album refreshes use. During a bulk
        // import this avoids fetching /artists/{id} and /about-artist repeatedly
        // for every album belonging to the same artist.
        var profile = GetArtistProfile(id);

        try
        {
            var directAlbums = GetAllDirectAlbums(id);
            if (directAlbums.Count > 0)
                EnrichAudiobookSummaries(directAlbums);

            if (directAlbums.Count > 0 || profile.Albums.Count == 0)
                profile.Albums = directAlbums;
        }
        catch (YandexMusicRateLimitException)
        {
            // Do not persist an incomplete direct-discography snapshot after an
            // exhausted 429 retry sequence. Let Lidarr retry the refresh later.
            throw;
        }
        catch (Exception ex)
        {
            // Keep the old /artists/{id} album list as a compatibility fallback
            // for non-rate-limit failures.
            _logger.Warn(ex,
                "Yandex Music direct-albums lookup failed for artist {0}; using the album list from /artists/{0}",
                id);
        }

        return profile;
    }

    /// <summary>
    /// Fetches artist descriptive metadata without downloading the whole
    /// discography. Used when an album refresh needs metadata for a parent artist.
    /// </summary>
    public YandexArtistResult GetArtistProfile(long id)
    {
        lock (ArtistProfileCacheLock)
        {
            if (ArtistProfileCache.TryGetValue(id, out var cached) &&
                DateTime.UtcNow - cached.CachedAtUtc < CacheLifetime)
            {
                _logger.Trace("Yandex Music artist profile {0}: cache hit", id);
                return cached.Profile;
            }

            ArtistProfileCache.Remove(id);

            var profile = GetLegacyArtist(id);
            var enriched = EnrichArtistAbout(id, profile);

            // Only keep the 15-minute profile cache when the complete
            // /about-artist enrichment succeeded. A transient non-429 failure
            // should be retried on the next refresh instead of caching a reduced
            // profile for the whole cache lifetime.
            if (enriched)
            {
                ArtistProfileCache[id] = new ArtistProfileCacheEntry(DateTime.UtcNow, profile);
                _logger.Trace("Yandex Music artist profile {0}: cached for {1} minutes", id, CacheLifetime.TotalMinutes);
            }

            return profile;
        }
    }

    public YandexPlaylist GetPlaylist(string playlistUuid)
    {
        if (string.IsNullOrWhiteSpace(playlistUuid))
            throw new ArgumentException("Playlist UUID is required.", nameof(playlistUuid));

        var encoded = Uri.EscapeDataString(playlistUuid.Trim());
        var response = Get<YandexPlaylistResponse>($"{BaseUrl}/playlist/{encoded}?richTracks=true");
        if (response?.Result == null)
            throw new InvalidOperationException($"Yandex Music playlist not found: {playlistUuid}");

        _logger.Debug(
            "Yandex Music playlist {0}: title='{1}', tracks={2}, visibility={3}",
            playlistUuid,
            response.Result.Title,
            response.Result.Tracks?.Count ?? 0,
            response.Result.Visibility);

        return response.Result;
    }

    public YandexPlaylist GetUserPlaylist(string owner, string kind)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Playlist owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("Playlist kind is required.", nameof(kind));

        var encodedOwner = Uri.EscapeDataString(owner.Trim());
        var encodedKind = Uri.EscapeDataString(kind.Trim());
        var response = Get<YandexPlaylistResponse>($"{BaseUrl}/users/{encodedOwner}/playlists/{encodedKind}?rich-tracks=true");
        if (response?.Result == null)
            throw new InvalidOperationException($"Yandex Music playlist not found: {owner}:{kind}");

        _logger.Debug(
            "Yandex Music user playlist {0}:{1}: title='{2}', tracks={3}, visibility={4}",
            owner,
            kind,
            response.Result.Title,
            response.Result.Tracks?.Count ?? 0,
            response.Result.Visibility);

        return response.Result;
    }

    public YandexAlbum GetAlbum(long id)
    {
        lock (AlbumCacheLock)
        {
            if (AlbumCache.TryGetValue(id, out var cached) &&
                DateTime.UtcNow - cached.CachedAtUtc < CacheLifetime)
            {
                _logger.Trace("Yandex Music album {0}: cache hit", id);
                return cached.Album;
            }

            AlbumCache.Remove(id);

            var response = Get<YandexAlbumResponse>($"{BaseUrl}/albums/{id}/with-tracks");
            if (response?.Result == null || response.Result.Id == 0)
                throw new AlbumNotFoundException(id.ToString());

            AlbumCache[id] = new AlbumCacheEntry(DateTime.UtcNow, response.Result);
            return response.Result;
        }
    }

    public List<YandexArtist> SearchArtists(string query, int page = 0)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<YandexArtist>();

        var encoded = Uri.EscapeDataString(query.Trim());
        var response = Get<YandexSearchResponse>($"{BaseUrl}/search?text={encoded}&type=artist&page={page}");
        var artists = response?.Result?.Artists?.Results ?? new List<YandexArtist>();

        _logger.Debug(
            "Yandex Music artist search '{0}': page={1}, returned={2}, total={3}",
            query,
            page,
            artists.Count,
            response?.Result?.Artists?.Total ?? artists.Count);

        return artists
            .Where(x => x.Id != 0 && !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Id)
            .Select(g => g.First())
            .ToList();
    }

    public (int LikesCount, int MaxLikesCount) GetAlbumLikesContext(long artistId, long albumId)
    {
        var albums = GetAllDirectAlbums(artistId);
        var maxLikes = albums.Count == 0 ? 0 : albums.Max(x => x.LikesCount);
        var likes = albums.FirstOrDefault(x => x.Id == albumId)?.LikesCount ?? 0;
        return (likes, maxLikes);
    }

    private YandexArtistResult GetLegacyArtist(long id)
    {
        var response = Get<YandexArtistResponse>($"{BaseUrl}/artists/{id}");
        if (response?.Result?.Artist == null || response.Result.Artist.Id == 0)
            throw new ArtistNotFoundException(id.ToString());
        return response.Result;
    }

    private bool EnrichArtistAbout(long id, YandexArtistResult target)
    {
        try
        {
            var about = Get<YandexArtistAbout>($"{BaseUrl}/artists/{id}/about-artist");
            if (about == null)
                return false;

            if (about.Artist.Id != 0)
            {
                if (string.IsNullOrWhiteSpace(target.Artist.Name) && !string.IsNullOrWhiteSpace(about.Artist.Name))
                    target.Artist.Name = about.Artist.Name;

                if (about.Artist.Cover != null)
                    target.Artist.Cover = about.Artist.Cover;
            }

            target.Artist.Description = about.Description;
            target.Artist.ArtistType = about.ArtistType;

            // about-artist uses { title, subtitle, url }; normalize these links to
            // the link model already consumed by the metadata mapper.
            if (about.Links.Count > 0)
            {
                target.Artist.Links = about.Links
                    .Where(x => !string.IsNullOrWhiteSpace(x.Url))
                    .Select(x => new YandexLink
                    {
                        Title = x.Title,
                        Subtitle = x.Subtitle,
                        Url = x.Url
                    })
                    .ToList();
            }

            // Prefer the dedicated photo set order returned by /about-artist,
            // but retain any covers supplied by /artists/{id}. Deduplicate by
            // template URI while keeping the about-artist list first.
            target.AllCovers = about.Covers
                .Concat(target.AllCovers)
                .Where(x => !string.IsNullOrWhiteSpace(x.Uri))
                .GroupBy(x => x.Uri!, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (target.AllCovers.Count == 0 && target.Artist.Cover != null)
                target.AllCovers.Add(target.Artist.Cover);

            _logger.Debug(
                "Yandex Music about-artist {0}: description={1}, links={2}, covers={3}, listeners={4}",
                id,
                !string.IsNullOrWhiteSpace(about.Description),
                about.Links.Count,
                about.Covers.Count,
                about.Stats.LastMonthListeners);
            return true;
        }
        catch (YandexMusicRateLimitException)
        {
            // A compact/basic profile must not replace an already persisted rich
            // profile merely because Yandex is rate-limiting us.
            throw;
        }
        catch (Exception ex)
        {
            // Artist lookup remains usable for non-rate-limit failures, but the
            // reduced result is intentionally not cached.
            _logger.Warn(ex,
                "Yandex Music about-artist lookup failed for artist {0}; using basic /artists/{0} metadata",
                id);
            return false;
        }
    }

    private void EnrichAudiobookSummaries(IEnumerable<YandexAlbum> albums)
    {
        foreach (var album in albums.Where(IsAudiobookSummaryMissingTrackCount))
        {
            try
            {
                var details = GetAlbum(album.Id);
                var trackCount = GetDetailedTrackCount(details);
                if (trackCount > 0)
                    album.TrackCount = trackCount;

                // Keep useful audiobook-only fields on the summary as well. This
                // lets Lidarr render the album list correctly without persisting
                // the full chapter list in the artist/discography response.
                if (album.DurationSec <= 0)
                    album.DurationSec = details.DurationSec;
                if (string.IsNullOrWhiteSpace(album.Description))
                    album.Description = details.Description;
                if (string.IsNullOrWhiteSpace(album.ShortDescription))
                    album.ShortDescription = details.ShortDescription;
                album.Pager ??= details.Pager;

                _logger.Debug(
                    "Yandex Music audiobook summary {0}: enriched trackCount={1}, durationSec={2}",
                    album.Id,
                    album.TrackCount,
                    album.DurationSec);
            }
            catch (YandexMusicRateLimitException)
            {
                // Do not let Lidarr persist a known-bad 0/0 summary when Yandex
                // rate-limits the detail request. A later artist refresh can retry.
                throw;
            }
            catch (Exception ex)
            {
                // The detailed count is an enhancement for audiobook summaries.
                // A single unavailable book must not prevent the whole artist from
                // refreshing; leave that one summary untouched and retry later.
                _logger.Warn(ex,
                    "Yandex Music audiobook summary enrichment failed for album {0}; keeping compact metadata",
                    album.Id);
            }
        }
    }

    private static bool IsAudiobookSummaryMissingTrackCount(YandexAlbum album) =>
        album.TrackCount <= 0 &&
        string.Equals(album.Type, "audiobook", StringComparison.OrdinalIgnoreCase);

    private static int GetDetailedTrackCount(YandexAlbum album)
    {
        if (album.TrackCount > 0)
            return album.TrackCount;

        var volumeCount = album.Volumes?.Sum(volume => volume?.Count ?? 0) ?? 0;
        if (volumeCount > 0)
            return volumeCount;

        return album.Pager?.Total ?? 0;
    }

    private List<YandexAlbum> GetAllDirectAlbums(long id)
    {
        lock (DirectAlbumsCacheLock)
        {
            if (DirectAlbumsCache.TryGetValue(id, out var cached) &&
                DateTime.UtcNow - cached.CachedAtUtc < CacheLifetime)
            {
                _logger.Trace("Yandex Music direct-albums {0}: cache hit", id);
                return cached.Albums;
            }

            DirectAlbumsCache.Remove(id);

            // Keep the fetch inside this lock so simultaneous refresh jobs for the
            // same artist cannot all miss the cache and download the same complete
            // discography at once.
            var albums = FetchAllDirectAlbums(id);
            DirectAlbumsCache[id] = new DirectAlbumsCacheEntry(DateTime.UtcNow, albums);
            return albums;
        }
    }

    private List<YandexAlbum> FetchAllDirectAlbums(long id)
    {
        var albums = new List<YandexAlbum>();
        var rawFetched = 0;
        var page = 0;
        var total = int.MaxValue;

        while (rawFetched < total && page < MaxDirectAlbumPages)
        {
            var url = $"{BaseUrl}/artists/{id}/direct-albums?page={page}&page-size={DirectAlbumsPageSize}";
            var response = Get<YandexDirectAlbumsResponse>(url);
            var result = response?.Result;
            if (result == null)
                break;

            total = Math.Max(0, result.Pager.Total);
            var pageAlbums = result.Albums ?? new List<YandexAlbum>();
            if (pageAlbums.Count == 0)
                break;

            albums.AddRange(pageAlbums);
            rawFetched += pageAlbums.Count;

            _logger.Debug(
                "Yandex Music direct-albums {0}: page={1}, received={2}, fetched={3}/{4}, apiPerPage={5}",
                id,
                result.Pager.Page,
                pageAlbums.Count,
                rawFetched,
                total,
                result.Pager.PerPage);

            page++;
        }

        if (page >= MaxDirectAlbumPages && rawFetched < total)
        {
            _logger.Warn(
                "Yandex Music direct-albums pagination stopped at safety limit for artist {0}: fetched {1} of {2}",
                id,
                rawFetched,
                total);
        }

        var deduplicated = albums
            .Where(x => x.Id != 0)
            .GroupBy(x => x.Id)
            .Select(g => g.First())
            .ToList();

        _logger.Debug(
            "Yandex Music direct-albums {0}: completed with {1} unique albums ({2} raw, reported total {3})",
            id,
            deduplicated.Count,
            rawFetched,
            total == int.MaxValue ? 0 : total);

        return deduplicated;
    }

    private sealed record ArtistProfileCacheEntry(DateTime CachedAtUtc, YandexArtistResult Profile);
    private sealed record DirectAlbumsCacheEntry(DateTime CachedAtUtc, List<YandexAlbum> Albums);
    private sealed record AlbumCacheEntry(DateTime CachedAtUtc, YandexAlbum Album);

    private T Get<T>(string url) where T : class, new()
    {
        // All Yandex API traffic from every plugin component shares one lock.
        // This deliberately serializes metadata/import-list requests and enforces
        // a conservative 500 ms gap between them, preventing refresh storms from
        // producing a burst of parallel requests.
        lock (ApiRequestLock)
        {
            for (var retry = 0; ; retry++)
            {
                WaitForRequestSlot();

                // Do not set User-Agent here. Lidarr's ManagedHttpDispatcher
                // supplies the approved host User-Agent itself.
                var request = new HttpRequestBuilder(url)
                    .SetHeader("Accept", "application/json")
                    .Build();
                request.AllowAutoRedirect = true;
                request.SuppressHttpError = true;

                _lastRequestUtc = DateTime.UtcNow;
                var response = _httpClient.Get<T>(request);

                if (!response.HasHttpError)
                    return response.Resource;

                if (response.StatusCode == (HttpStatusCode)429)
                {
                    if (retry < MaxRateLimitRetries)
                    {
                        var delay = GetRetryDelay(response, retry);
                        _logger.Warn(
                            "Yandex Music rate limit for {0}; retry {1}/{2} in {3:0.0}s",
                            url,
                            retry + 1,
                            MaxRateLimitRetries,
                            delay.TotalSeconds);
                        Thread.Sleep(delay);
                        continue;
                    }

                    _logger.Warn(
                        "Yandex Music request failed after {0} rate-limit retries: {1}",
                        MaxRateLimitRetries,
                        url);
                    throw new YandexMusicRateLimitException(url, MaxRateLimitRetries + 1);
                }

                _logger.Warn("Yandex Music request failed: {0} -> HTTP {1}", url, response.StatusCode);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new InvalidOperationException($"Yandex Music object not found: {url}");

                throw new InvalidOperationException($"Yandex Music HTTP error {(int)response.StatusCode}: {url}");
            }
        }
    }

    private static void WaitForRequestSlot()
    {
        var elapsed = DateTime.UtcNow - _lastRequestUtc;
        var remaining = MinimumRequestInterval - elapsed;
        if (remaining > TimeSpan.Zero)
            Thread.Sleep(remaining);
    }

    private static TimeSpan GetRetryDelay<T>(HttpResponse<T> response, int retry)
        where T : new()
    {
        var fallback = TimeSpan.FromSeconds(Math.Pow(2, retry)); // 1, 2, 4, 8 seconds
        var retryAfter = response.Headers.GetSingleValue("Retry-After");
        if (string.IsNullOrWhiteSpace(retryAfter))
            return fallback;

        TimeSpan parsed;
        if (int.TryParse(retryAfter, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            parsed = TimeSpan.FromSeconds(Math.Max(0, seconds));
        }
        else if (DateTimeOffset.TryParse(
                     retryAfter,
                     CultureInfo.InvariantCulture,
                     DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                     out var retryAt))
        {
            parsed = retryAt - DateTimeOffset.UtcNow;
            if (parsed < TimeSpan.Zero)
                parsed = TimeSpan.Zero;
        }
        else
        {
            return fallback;
        }

        if (parsed < fallback)
            parsed = fallback;
        if (parsed > MaximumRetryAfter)
            parsed = MaximumRetryAfter;

        return parsed;
    }
}
