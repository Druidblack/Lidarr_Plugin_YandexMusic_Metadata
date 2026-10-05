using System.Globalization;
using System.Net;
using System.Threading;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Exceptions;

namespace Lidarr.Plugin.YandexMusicMetadata.Api;

internal sealed class DeezerClient
{
    private const string BaseUrl = "https://api.deezer.com";
    private const int PageSize = 100;
    private const int MaxPages = 1000;
    private const int MaxRateLimitRetries = 4;

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMilliseconds(125);
    private static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromSeconds(60);

    private static readonly object ApiRequestLock = new();
    private static DateTime _lastRequestUtc = DateTime.MinValue;

    private static readonly object ArtistCacheLock = new();
    private static readonly Dictionary<long, ArtistCacheEntry> ArtistCache = new();

    private static readonly object ArtistAlbumsCacheLock = new();
    private static readonly Dictionary<long, ArtistAlbumsCacheEntry> ArtistAlbumsCache = new();

    private static readonly object AlbumCacheLock = new();
    private static readonly Dictionary<long, AlbumCacheEntry> AlbumCache = new();

    private static readonly object PlaylistCacheLock = new();
    private static readonly Dictionary<long, PlaylistCacheEntry> PlaylistCache = new();

    private readonly IHttpClient _httpClient;
    private readonly Logger _logger;

    public DeezerClient(IHttpClient httpClient, Logger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public DeezerArtistResult GetArtist(long id)
    {
        return new DeezerArtistResult
        {
            Artist = GetArtistProfile(id),
            Albums = GetArtistAlbums(id)
        };
    }

    public DeezerArtist GetArtistProfile(long id)
    {
        lock (ArtistCacheLock)
        {
            if (ArtistCache.TryGetValue(id, out var cached) &&
                DateTime.UtcNow - cached.CachedAtUtc < CacheLifetime)
            {
                _logger.Trace("Deezer artist {0}: cache hit", id);
                return cached.Artist;
            }

            ArtistCache.Remove(id);
            var artist = Get<DeezerArtist>($"{BaseUrl}/artist/{id}");
            if (artist.Id == 0)
                throw new ArtistNotFoundException(id.ToString(CultureInfo.InvariantCulture));

            ArtistCache[id] = new ArtistCacheEntry(DateTime.UtcNow, artist);
            return artist;
        }
    }

    public List<DeezerAlbum> GetArtistAlbums(long id)
    {
        lock (ArtistAlbumsCacheLock)
        {
            if (ArtistAlbumsCache.TryGetValue(id, out var cached) &&
                DateTime.UtcNow - cached.CachedAtUtc < CacheLifetime)
            {
                _logger.Trace("Deezer artist albums {0}: cache hit", id);
                return cached.Albums;
            }

            ArtistAlbumsCache.Remove(id);
            var albums = FetchArtistAlbums(id);
            ArtistAlbumsCache[id] = new ArtistAlbumsCacheEntry(DateTime.UtcNow, albums);
            return albums;
        }
    }

    public DeezerAlbum GetAlbum(long id)
    {
        lock (AlbumCacheLock)
        {
            if (AlbumCache.TryGetValue(id, out var cached) &&
                DateTime.UtcNow - cached.CachedAtUtc < CacheLifetime)
            {
                _logger.Trace("Deezer album {0}: cache hit", id);
                return cached.Album;
            }

            AlbumCache.Remove(id);
            var album = Get<DeezerAlbum>($"{BaseUrl}/album/{id}");
            if (album.Id == 0)
                throw new AlbumNotFoundException(id.ToString(CultureInfo.InvariantCulture));

            // The embedded tracks object is paged. Most albums fit in the first page,
            // but explicitly fetch the complete list when Deezer reports more tracks.
            var embedded = album.Tracks?.Data ?? new List<DeezerTrack>();
            var expected = Math.Max(album.NbTracks ?? 0, album.Tracks?.Total ?? 0);
            if (expected > embedded.Count || !string.IsNullOrWhiteSpace(album.Tracks?.Next))
                album.Tracks = new DeezerPage<DeezerTrack> { Data = FetchAlbumTracks(id), Total = expected };

            AlbumCache[id] = new AlbumCacheEntry(DateTime.UtcNow, album);
            return album;
        }
    }

    public DeezerPlaylist GetPlaylist(long id)
    {
        lock (PlaylistCacheLock)
        {
            if (PlaylistCache.TryGetValue(id, out var cached) &&
                DateTime.UtcNow - cached.CachedAtUtc < CacheLifetime)
            {
                _logger.Trace("Deezer playlist {0}: cache hit", id);
                return cached.Playlist;
            }

            PlaylistCache.Remove(id);
            var playlist = Get<DeezerPlaylist>($"{BaseUrl}/playlist/{id}");
            if (playlist.Id == 0)
                throw new InvalidOperationException($"Deezer playlist not found: {id}");

            var embedded = playlist.Tracks?.Data ?? new List<DeezerTrack>();
            var expected = Math.Max(playlist.NbTracks, playlist.Tracks?.Total ?? 0);
            if (expected > embedded.Count || !string.IsNullOrWhiteSpace(playlist.Tracks?.Next))
                playlist.Tracks = new DeezerPage<DeezerTrack> { Data = FetchPlaylistTracks(id), Total = expected };

            _logger.Debug(
                "Deezer playlist {0}: title='{1}', tracks={2}, public={3}",
                id,
                playlist.Title,
                playlist.Tracks?.Data?.Count ?? 0,
                playlist.Public);

            PlaylistCache[id] = new PlaylistCacheEntry(DateTime.UtcNow, playlist);
            return playlist;
        }
    }

    public string ResolveShareUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Deezer share URL is required.", nameof(url));

        lock (ApiRequestLock)
        {
            WaitForRequestSlot();

            var request = new HttpRequestBuilder(url.Trim())
                .SetHeader("Accept", "text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.8")
                .Build();
            request.AllowAutoRedirect = true;
            request.SuppressHttpError = true;

            _lastRequestUtc = DateTime.UtcNow;
            var response = _httpClient.Get(request);
            if (response.HasHttpError)
                throw new InvalidOperationException($"Deezer share URL HTTP error {(int)response.StatusCode}: {url}");

            var resolved = response.Request.Url.ToString();
            _logger.Debug("Deezer share URL resolved: {0} -> {1}", url, resolved);
            return resolved;
        }
    }

    public List<DeezerArtist> SearchArtists(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<DeezerArtist>();

        var encoded = Uri.EscapeDataString(query.Trim());
        var page = Get<DeezerPage<DeezerArtist>>($"{BaseUrl}/search/artist?q={encoded}&limit=25&index=0");
        var results = page.Data
            .Where(x => x.Id != 0 && !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .ToList();

        _logger.Debug("Deezer artist search '{0}': returned={1}, total={2}", query, results.Count, page.Total);
        return results;
    }

    public (int Fans, int MaxFans) GetAlbumFansContext(long artistId, long albumId)
    {
        var albums = GetArtistAlbums(artistId);
        var max = albums.Count == 0 ? 0 : albums.Max(x => x.Fans ?? 0);
        var current = albums.FirstOrDefault(x => x.Id == albumId)?.Fans ?? 0;
        return (current, max);
    }

    private List<DeezerAlbum> FetchArtistAlbums(long id)
    {
        var albums = new List<DeezerAlbum>();
        var index = 0;
        var page = 0;
        var total = int.MaxValue;

        while (index < total && page < MaxPages)
        {
            var response = Get<DeezerPage<DeezerAlbum>>($"{BaseUrl}/artist/{id}/albums?limit={PageSize}&index={index}");
            total = Math.Max(0, response.Total);
            var pageAlbums = response.Data ?? new List<DeezerAlbum>();
            if (pageAlbums.Count == 0)
                break;

            albums.AddRange(pageAlbums);
            index += pageAlbums.Count;
            page++;

            _logger.Debug(
                "Deezer artist albums {0}: page={1}, received={2}, fetched={3}/{4}",
                id,
                page,
                pageAlbums.Count,
                index,
                total);

            if (string.IsNullOrWhiteSpace(response.Next) && index >= total)
                break;
        }

        if (page >= MaxPages && index < total)
            _logger.Warn("Deezer artist albums pagination stopped at safety limit for artist {0}: fetched {1} of {2}", id, index, total);

        return albums
            .Where(x => x.Id != 0)
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .ToList();
    }

    private List<DeezerTrack> FetchPlaylistTracks(long id)
    {
        var tracks = new List<DeezerTrack>();
        var index = 0;
        var page = 0;
        var total = int.MaxValue;

        while (index < total && page < MaxPages)
        {
            var response = Get<DeezerPage<DeezerTrack>>($"{BaseUrl}/playlist/{id}/tracks?limit={PageSize}&index={index}");
            total = Math.Max(0, response.Total);
            var pageTracks = response.Data ?? new List<DeezerTrack>();
            if (pageTracks.Count == 0)
                break;

            tracks.AddRange(pageTracks);
            index += pageTracks.Count;
            page++;

            _logger.Debug(
                "Deezer playlist tracks {0}: page={1}, received={2}, fetched={3}/{4}",
                id,
                page,
                pageTracks.Count,
                index,
                total);

            if (string.IsNullOrWhiteSpace(response.Next) && index >= total)
                break;
        }

        if (page >= MaxPages && index < total)
            _logger.Warn("Deezer playlist pagination stopped at safety limit for playlist {0}: fetched {1} of {2}", id, index, total);

        // Keep playlist order, but remove duplicate API rows by track id. Local or
        // unavailable tracks can have id=0; keep those rows so Test() can still
        // distinguish an accessible playlist from an empty one.
        var seen = new HashSet<long>();
        return tracks
            .Where(x => x.Id == 0 || seen.Add(x.Id))
            .ToList();
    }

    private List<DeezerTrack> FetchAlbumTracks(long id)
    {
        var tracks = new List<DeezerTrack>();
        var index = 0;
        var page = 0;
        var total = int.MaxValue;

        while (index < total && page < MaxPages)
        {
            var response = Get<DeezerPage<DeezerTrack>>($"{BaseUrl}/album/{id}/tracks?limit={PageSize}&index={index}");
            total = Math.Max(0, response.Total);
            var pageTracks = response.Data ?? new List<DeezerTrack>();
            if (pageTracks.Count == 0)
                break;

            tracks.AddRange(pageTracks);
            index += pageTracks.Count;
            page++;

            if (string.IsNullOrWhiteSpace(response.Next) && index >= total)
                break;
        }

        return tracks
            .Where(x => x.Id != 0)
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .ToList();
    }

    private T Get<T>(string url) where T : class, new()
    {
        lock (ApiRequestLock)
        {
            for (var retry = 0; ; retry++)
            {
                WaitForRequestSlot();

                var request = new HttpRequestBuilder(url)
                    .SetHeader("Accept", "application/json")
                    .Build();
                request.AllowAutoRedirect = true;
                request.SuppressHttpError = true;

                _lastRequestUtc = DateTime.UtcNow;
                var response = _httpClient.Get<T>(request);

                if (!response.HasHttpError)
                {
                    var resource = response.Resource;
                    if (resource is IDeezerErrorContainer errorContainer && errorContainer.Error is { Code: not 0 } error)
                        throw new InvalidOperationException($"Deezer API error {error.Code} ({error.Type}): {error.Message}");

                    return resource;
                }

                if (response.StatusCode == (HttpStatusCode)429 && retry < MaxRateLimitRetries)
                {
                    var delay = GetRetryDelay(response, retry);
                    _logger.Warn(
                        "Deezer rate limit for {0}; retry {1}/{2} in {3:0.0}s",
                        url,
                        retry + 1,
                        MaxRateLimitRetries,
                        delay.TotalSeconds);
                    Thread.Sleep(delay);
                    continue;
                }

                _logger.Warn("Deezer request failed: {0} -> HTTP {1}", url, response.StatusCode);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new InvalidOperationException($"Deezer object not found: {url}");

                throw new InvalidOperationException($"Deezer HTTP error {(int)response.StatusCode}: {url}");
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

    private static TimeSpan GetRetryDelay<T>(HttpResponse<T> response, int retry) where T : new()
    {
        var fallback = TimeSpan.FromSeconds(Math.Pow(2, retry));
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

        if (parsed < fallback) parsed = fallback;
        if (parsed > MaximumRetryAfter) parsed = MaximumRetryAfter;
        return parsed;
    }

    private sealed record ArtistCacheEntry(DateTime CachedAtUtc, DeezerArtist Artist);
    private sealed record ArtistAlbumsCacheEntry(DateTime CachedAtUtc, List<DeezerAlbum> Albums);
    private sealed record AlbumCacheEntry(DateTime CachedAtUtc, DeezerAlbum Album);
    private sealed record PlaylistCacheEntry(DateTime CachedAtUtc, DeezerPlaylist Playlist);
}
