using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.MediaCover;
using CoreMediaCover = NzbDrone.Core.MediaCover.MediaCover;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.SkyHook;
using NzbDrone.Core.Music;
using Lidarr.Plugin.YandexMusicMetadata.Api;
using Lidarr.Plugin.YandexMusicMetadata.Utility;

namespace Lidarr.Plugin.YandexMusicMetadata.Metadata;

public sealed partial class YandexMusicMetadataProvider :
    IProvideArtistInfo,
    ISearchForNewArtist,
    IProvideAlbumInfo,
    ISearchForNewAlbum,
    ISearchForNewEntity
{
    private readonly YandexMusicClient _client;
    private readonly DeezerClient _deezerClient;
    private readonly SkyHookProxy _lidarrDefault;
    private readonly IMetadataFactory _metadataFactory;
    private readonly IAlbumService _albumService;
    private readonly IArtistService _artistService;
    private readonly IImportListExclusionService _importListExclusionService;
    private readonly Logger _logger;

    public YandexMusicMetadataProvider(
        IHttpClient httpClient,
        IMetadataFactory metadataFactory,
        IAlbumService albumService,
        IArtistService artistService,
        IImportListExclusionService importListExclusionService,
        SkyHookProxy lidarrDefault,
        Logger logger)
    {
        _metadataFactory = metadataFactory;
        _albumService = albumService;
        _artistService = artistService;
        _importListExclusionService = importListExclusionService;
        _lidarrDefault = lidarrDefault;
        _logger = logger;
        _client = new YandexMusicClient(httpClient, logger);
        _deezerClient = new DeezerClient(httpClient, logger);
    }

    public Artist GetArtistInfo(string lidarrId, int metadataProfileId)
    {
        if (DeezerIdParser.TryArtistId(lidarrId, out var deezerId))
            return GetDeezerArtistInfo(deezerId);

        if (YandexIdParser.TryPodcastArtistId(lidarrId, out var podcastAlbumId))
            return GetPodcastArtistInfo(podcastAlbumId);

        if (!YandexIdParser.TryArtistId(lidarrId, out var id))
            return _lidarrDefault.GetArtistInfo(lidarrId, metadataProfileId);

        var settings = GetSettings();
        var source = _client.GetArtist(id);
        var artist = MapArtist(source, settings);

        _logger.Debug(
            "Yandex Music artist {0}: direct={1}, also={2}, returned={3}, includeAlso={4}",
            id,
            source.Albums.Count,
            source.AlsoAlbums.Count,
            artist.Albums.Value.Count,
            settings.IncludeAlsoAlbums);

        return artist;
    }

    public HashSet<string> GetChangedArtists(DateTime startTime) => _lidarrDefault.GetChangedArtists(startTime);
    public HashSet<string> GetChangedAlbums(DateTime startTime) => _lidarrDefault.GetChangedAlbums(startTime);

    public Tuple<string, Album, List<ArtistMetadata>> GetAlbumInfo(string id)
    {
        if (DeezerIdParser.TryAlbumId(id, out var deezerAlbumId))
            return GetDeezerAlbumInfo(id, deezerAlbumId);

        if (!YandexIdParser.TryAlbumId(id, out var albumId))
            return _lidarrDefault.GetAlbumInfo(id);

        var settings = GetSettings();

        if (settings.LightweightAlbumRequests)
        {
            var stored = TryGetStoredAlbumInfo(id);
            if (stored != null)
                return stored;
        }

        var source = _client.GetAlbum(albumId);

        // Yandex podcasts are album-shaped objects with an empty artists[] array.
        // Lidarr, however, requires every album and every track to have an artist.
        // Model the podcast itself as a synthetic Lidarr artist and keep the Yandex
        // album as that artist's single release. This mirrors how podcast apps
        // conceptually group episodes and avoids falling through to SkyHook.
        if (IsPodcast(source))
            return GetPodcastAlbumInfo(source, settings);

        long? parentArtistId = null;
        if (YandexIdParser.TryAlbumParentArtistId(id, out var encodedParentArtistId))
            parentArtistId = encodedParentArtistId;

        // Album responses contain only a compact artist object. If those compact
        // objects are returned in tuple.Item3, Lidarr's RefreshAlbumService will
        // UpsertMany() them and overwrite richer artist metadata already saved by
        // GetArtistInfo() (overview, social links and full image set). Resolve each
        // album-level artist through /about-artist before returning metadata to Lidarr.
        // Audiobook credits are less regular than music credits:
        //  * several narrators can be listed directly in album.artists;
        //  * extra narrators can be hidden inside artist.decomposed;
        //  * chapter artists can differ from album.artists entirely.
        // Flatten the album-level contributors first. Track-only contributors are
        // added to the same dictionary by MapRelease below.
        var albumContributors = ExpandArtistContributors(source.Artists)
            .Where(a => a.Id > 0)
            .GroupBy(a => a.Id)
            .Select(g => g.First())
            .ToList();

        var artists = albumContributors
            .Select(a => MapAlbumArtistMetadata(a, settings))
            .GroupBy(a => a.ForeignArtistId)
            .Select(g => g.First())
            .ToList();

        var dict = artists.ToDictionary(a => a.ForeignArtistId, a => a);
        ArtistMetadata primaryMetadata;

        if (parentArtistId.HasValue)
        {
            var parentForeignId = YandexIdParser.ArtistForeignId(parentArtistId.Value);
            if (!dict.TryGetValue(parentForeignId, out primaryMetadata!))
            {
                // Participation releases can omit the selected Lidarr parent from
                // album.artists. Load its full profile explicitly and keep it as parent.
                var parent = _client.GetArtistProfile(parentArtistId.Value);
                primaryMetadata = MapArtistMetadata(parent.Artist, settings, parent.AllCovers);
                dict[parentForeignId] = primaryMetadata;
            }
        }
        else
        {
            // For audiobooks, the top-level album credits can contain a composite
            // attribution while every actual chapter is read by one stable narrator.
            // Prefer that sole chapter narrator as Lidarr's parent artist. This makes
            // books such as "Охота на лис" attach to the real reader rather than to
            // a wrapper credit. Multi-reader productions keep Yandex's first
            // album-level contributor as their stable parent.
            var audiobookPrimary = IsAudiobook(source)
                ? GetSoleTrackArtist(source)
                : null;

            if (audiobookPrimary != null)
            {
                var primaryForeignId = YandexIdParser.ArtistForeignId(audiobookPrimary.Id);
                if (!dict.TryGetValue(primaryForeignId, out primaryMetadata!))
                {
                    primaryMetadata = MapAlbumArtistMetadata(audiobookPrimary, settings);
                    dict[primaryForeignId] = primaryMetadata;
                }
            }
            else
            {
                if (artists.Count == 0)
                {
                    // A malformed audiobook can still have no album-level artist but
                    // valid chapter artists. Use the first real chapter contributor
                    // before giving up.
                    var trackArtist = GetFirstTrackArtist(source);
                    if (trackArtist == null)
                        throw new InvalidOperationException($"Yandex album {albumId} has no artists");

                    primaryMetadata = MapAlbumArtistMetadata(trackArtist, settings);
                    dict[primaryMetadata.ForeignArtistId] = primaryMetadata;
                }
                else
                {
                    primaryMetadata = artists[0];
                }
            }
        }

        // Direct audiobook lookups start with a bare yandex:album:<id>. By this
        // point we have already resolved the real parent narrator. Canonicalize the
        // album to the same parent-aware id used when it is discovered through the
        // narrator page; otherwise Lidarr creates a second, trackless bare album.
        if (!parentArtistId.HasValue && IsAudiobook(source))
        {
            var inferredParentArtistId = TryParseYandexArtistId(primaryMetadata.ForeignArtistId);
            if (inferredParentArtistId.HasValue)
            {
                parentArtistId = inferredParentArtistId.Value;
                _logger.Debug(
                    "Yandex Music audiobook {0}: inferred parent artist {1} for direct album lookup",
                    albumId,
                    parentArtistId.Value);
            }
        }

        var ratingArtistId = parentArtistId ?? TryParseYandexArtistId(primaryMetadata.ForeignArtistId);
        var ratings = settings.CalculateAlbumRatings
            ? BuildAlbumRatings(source, ratingArtistId)
            : new Ratings();

        var album = MapAlbum(source, dict, includeTracks: true, settings, parentArtistId, ratings);
        album.ArtistMetadata = primaryMetadata;
        album.Artist = new Artist { Metadata = primaryMetadata };

        _logger.Debug(
            "Yandex Music album {0}: requestedId={1}, canonicalId={2}, parent={3}, tracks={4}",
            albumId,
            id,
            album.ForeignAlbumId,
            primaryMetadata.ForeignArtistId,
            album.AlbumReleases.Value.Sum(r => r.Tracks.Value.Count));

        // MapRelease can discover artists that are present only on chapters.
        // Return the complete dictionary rather than the original album-level list,
        // otherwise Lidarr receives Track.ArtistMetadata references for artists it
        // was never asked to upsert.
        var returnedArtists = dict.Values
            .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ForeignArtistId))
            .DistinctBy(a => a.ForeignArtistId)
            .ToList();

        return Tuple.Create(primaryMetadata.ForeignArtistId, album, returnedArtists);
    }

    public List<Artist> SearchForNewArtist(string title)
    {
        var yandexEnabled = IsYandexSourceEnabled();
        var deezerEnabled = IsDeezerSourceEnabled();

        if (deezerEnabled && DeezerIdParser.TryArtistId(title, out var deezerId))
        {
            try
            {
                return new List<Artist> { MapDeezerArtist(_deezerClient.GetArtist(deezerId), GetDeezerSettings(), lookup: true) };
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Deezer artist lookup failed for {0}; falling back to Lidarr Default", title);
                return _lidarrDefault.SearchForNewArtist(title);
            }
        }

        if (yandexEnabled && YandexIdParser.TryPodcastArtistId(title, out var podcastAlbumId))
        {
            try
            {
                return new List<Artist> { GetPodcastArtistInfo(podcastAlbumId, lookup: true) };
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Yandex Music podcast lookup failed for {0}", title);
                return new List<Artist>();
            }
        }

        if (yandexEnabled && YandexIdParser.TryArtistId(title, out var id))
        {
            try
            {
                var source = _client.GetArtist(id);
                var artist = MapArtist(source, GetSettings());
                artist.Metadata.Value.Images = BuildArtistLookupImages(source.Artist, GetSettings(), source.AllCovers);
                return new List<Artist> { artist };
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Yandex Music artist lookup failed for {0}; falling back to Lidarr Default", title);
                return _lidarrDefault.SearchForNewArtist(title);
            }
        }

        // Preserve the historic Yandex meaning of a bare number whenever the
        // Yandex source is enabled. If only Deezer is enabled, a bare number can
        // conveniently be used as a Deezer artist ID.
        if (!yandexEnabled && deezerEnabled && long.TryParse(title?.Trim(), out var bareDeezerId))
        {
            try
            {
                return new List<Artist> { MapDeezerArtist(_deezerClient.GetArtist(bareDeezerId), GetDeezerSettings(), lookup: true) };
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Deezer numeric artist lookup failed for {0}; falling back to Lidarr Default", title);
                return _lidarrDefault.SearchForNewArtist(title);
            }
        }

        if (string.IsNullOrWhiteSpace(title) || IsNativeArtistIdentifierQuery(title))
            return _lidarrDefault.SearchForNewArtist(title);

        // Do not reinterpret an explicit reference for a disabled external source
        // as a text query for a different source.
        if ((!deezerEnabled && DeezerIdParser.IsArtistReference(title)) ||
            (!yandexEnabled && IsExplicitYandexArtistReference(title)))
            return _lidarrDefault.SearchForNewArtist(title);

        var nativeResults = _lidarrDefault.SearchForNewArtist(title);
        var externalResults = new List<Artist>();

        if (yandexEnabled && GetSettings().SearchArtistsByName)
        {
            try
            {
                externalResults.AddRange(SearchYandexArtistsByName(title, GetSettings()));
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Yandex Music artist name search failed for {0}; keeping other search results", title);
            }
        }

        if (deezerEnabled && GetDeezerSettings().SearchArtistsByName)
        {
            try
            {
                externalResults.AddRange(SearchDeezerArtistsByName(title, GetDeezerSettings()));
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Deezer artist name search failed for {0}; keeping other search results", title);
            }
        }

        return MergeArtistSearchResults(externalResults, nativeResults);
    }

    public List<Album> SearchForNewAlbum(string title, string artist)
    {
        var yandexEnabled = IsYandexSourceEnabled();
        var deezerEnabled = IsDeezerSourceEnabled();

        if (deezerEnabled && DeezerIdParser.TryAlbumId(title, out var deezerId))
        {
            try
            {
                var data = GetAlbumInfo(DeezerIdParser.AlbumForeignId(deezerId));
                return new List<Album> { data.Item2 };
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Deezer album lookup failed for {0}; falling back to Lidarr Default", title);
                return _lidarrDefault.SearchForNewAlbum(title, artist);
            }
        }

        if (yandexEnabled && YandexIdParser.TryAlbumId(title, out var id))
        {
            try
            {
                var data = GetAlbumInfo(YandexIdParser.AlbumForeignId(id));
                return new List<Album> { data.Item2 };
            }
            catch (Exception ex)
            {
                // An explicit Yandex album URL/ID is not meaningful to SkyHook.
                // Returning an empty result avoids a second, unrelated exception
                // from LidarrAPI when the external item cannot be mapped.
                _logger.Warn(ex, "Yandex Music album lookup failed for {0}", title);
                return new List<Album>();
            }
        }

        if (!yandexEnabled && deezerEnabled && long.TryParse(title?.Trim(), out var bareDeezerId))
        {
            try
            {
                var data = GetAlbumInfo(DeezerIdParser.AlbumForeignId(bareDeezerId));
                return new List<Album> { data.Item2 };
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Deezer numeric album lookup failed for {0}; falling back to Lidarr Default", title);
            }
        }

        return _lidarrDefault.SearchForNewAlbum(title, artist);
    }

    public List<Album> SearchForNewAlbumByRecordingIds(List<string> recordingIds) =>
        _lidarrDefault.SearchForNewAlbumByRecordingIds(recordingIds);

    public List<object> SearchForNewEntity(string title)
    {
        var yandexEnabled = IsYandexSourceEnabled();
        var deezerEnabled = IsDeezerSourceEnabled();

        if (yandexEnabled && IsExplicitYandexAlbumReference(title))
        {
            try
            {
                if (!YandexIdParser.TryAlbumId(title, out var yandexAlbumId))
                    return new List<object>();

                var source = _client.GetAlbum(yandexAlbumId);
                if (IsPodcast(source))
                    return new List<object> { MapPodcastArtist(source, GetSettings(), lookup: true) };

                var data = GetAlbumInfo(YandexIdParser.AlbumForeignId(yandexAlbumId));
                return new List<object> { data.Item2 };
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Yandex Music entity lookup failed for {0}", title);
                return new List<object>();
            }
        }

        if (deezerEnabled && DeezerIdParser.IsAlbumReference(title))
            return SearchForNewAlbum(title, string.Empty).Cast<object>().ToList();

        if ((deezerEnabled && DeezerIdParser.IsArtistReference(title)) ||
            (yandexEnabled && (YandexIdParser.TryPodcastArtistId(title, out _) || YandexIdParser.TryArtistId(title, out _))) ||
            (!yandexEnabled && deezerEnabled && long.TryParse(title?.Trim(), out _)))
        {
            return SearchForNewArtist(title).Cast<object>().ToList();
        }

        if (string.IsNullOrWhiteSpace(title) || IsNativeArtistIdentifierQuery(title))
            return _lidarrDefault.SearchForNewEntity(title);

        if ((!deezerEnabled && (DeezerIdParser.IsArtistReference(title) || DeezerIdParser.IsAlbumReference(title))) ||
            (!yandexEnabled && (IsExplicitYandexArtistReference(title) || IsExplicitYandexAlbumReference(title))))
            return _lidarrDefault.SearchForNewEntity(title);

        var nativeEntities = _lidarrDefault.SearchForNewEntity(title);
        var externalArtists = new List<object>();

        if (yandexEnabled && GetSettings().SearchArtistsByName)
        {
            try
            {
                externalArtists.AddRange(SearchYandexArtistsByName(title, GetSettings()).Cast<object>());
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Yandex Music entity name search failed for {0}; keeping other search results", title);
            }
        }

        if (deezerEnabled && GetDeezerSettings().SearchArtistsByName)
        {
            try
            {
                externalArtists.AddRange(SearchDeezerArtistsByName(title, GetDeezerSettings()).Cast<object>());
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Deezer entity name search failed for {0}; keeping other search results", title);
            }
        }

        return externalArtists.Concat(nativeEntities).ToList();
    }

    private static bool IsExplicitYandexArtistReference(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var value = title.Trim();
        return value.Contains("/artist/", StringComparison.OrdinalIgnoreCase) && value.Contains("yandex.", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("yandex:artist:", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("yandex:podcast:", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("ym:artist:", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("ym:", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("yandex:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExplicitYandexAlbumReference(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var value = title.Trim();
        return value.Contains("/album/", StringComparison.OrdinalIgnoreCase) && value.Contains("yandex.", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("yandex:album:", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("ym:album:", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("ym-album:", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("yandex-album:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNativeArtistIdentifierQuery(string title)
    {
        var value = title.Trim();
        if (value.StartsWith("lidarr:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("mbid:", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("musicbrainz.org/artist/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Guid.TryParse(value, out _);
    }

    private List<Artist> SearchYandexArtistsByName(string title, YandexMusicMetadataSettings settings)
    {
        var results = _client.SearchArtists(title);
        return results
            .Select(a => MapArtistSearchResult(a, settings))
            .ToList();
    }

    private static Artist MapArtistSearchResult(YandexArtist source, YandexMusicMetadataSettings settings)
    {
        var metadata = MapArtistMetadata(source, settings);
        metadata.Disambiguation = "Yandex Music";

        // Do not reuse the persistent artwork URL for lookup results. For artists
        // that are not in Lidarr yet, ArtistLookupController passes Images through
        // MediaCoverProxy. A plain remote URL is the most reliable source for that
        // proxy and for the RemotePoster field used by the search UI.
        metadata.Images = BuildArtistLookupImages(source, settings);

        return new Artist
        {
            Metadata = metadata,
            CleanName = NzbDrone.Core.Parser.Parser.CleanArtistName(metadata.Name),
            SortName = NzbDrone.Core.Parser.Parser.NormalizeTitle(metadata.Name),
            Albums = new List<Album>()
        };
    }

    private static List<Artist> MergeArtistSearchResults(
        IEnumerable<Artist> yandexResults,
        IEnumerable<Artist> nativeResults)
    {
        var combined = new List<Artist>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var artist in yandexResults.Concat(nativeResults))
        {
            var foreignId = artist.Metadata?.Value?.ForeignArtistId;
            if (!string.IsNullOrWhiteSpace(foreignId) && !seenIds.Add(foreignId))
                continue;

            combined.Add(artist);
        }

        return combined;
    }

    private bool IsYandexSourceEnabled()
    {
        var definition = GetDefinition();
        return definition?.Enable ?? true;
    }

    private bool IsDeezerSourceEnabled()
    {
        var definition = GetDeezerDefinition();
        return definition?.Enable ?? false;
    }

    private YandexMusicMetadataSettings GetSettings()
    {
        return GetDefinition()?.Settings as YandexMusicMetadataSettings ?? new YandexMusicMetadataSettings();
    }

    private NzbDrone.Core.Extras.Metadata.MetadataDefinition? GetDefinition()
    {
        return _metadataFactory.All()
            .FirstOrDefault(d => string.Equals(d.Implementation, nameof(YandexMusicMetadataConsumer), StringComparison.Ordinal));
    }

    private DeezerMetadataSettings GetDeezerSettings()
    {
        return GetDeezerDefinition()?.Settings as DeezerMetadataSettings ?? new DeezerMetadataSettings();
    }

    private NzbDrone.Core.Extras.Metadata.MetadataDefinition? GetDeezerDefinition()
    {
        return _metadataFactory.All()
            .FirstOrDefault(d => string.Equals(d.Implementation, nameof(DeezerMetadataConsumer), StringComparison.Ordinal));
    }

    private Tuple<string, Album, List<ArtistMetadata>>? TryGetStoredAlbumInfo(string requestedId, string sourceName = "Yandex Music")
    {
        var existing = _albumService.FindById(requestedId);
        if (existing == null || existing.LastInfoSync == DateTime.MinValue)
            return null;

        // A directly added album can already have LastInfoSync set while its child
        // graph has not been persisted yet. Reusing that half-created object makes
        // RefreshAlbumService see AlbumReleases.Count == 0 and report the album as
        // removed from metadata. Never use lightweight mode for an empty graph.
        var existingReleases = existing.AlbumReleases.Value;
        if (existingReleases == null || existingReleases.Count == 0)
        {
            _logger.Debug(
                "{0} lightweight requests: stored album {1} has no releases; forcing full remote refresh",
                sourceName,
                requestedId);
            return null;
        }

        // Audiobooks are useful to Lidarr only after the chapter graph has been
        // materialized. A canonical audiobook with a release but zero tracks is the
        // same partial-add state, so force /with-tracks instead of freezing 0 tracks.
        if (string.Equals(sourceName, "Yandex Music", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.Disambiguation, "Audiobook", StringComparison.OrdinalIgnoreCase) &&
            existingReleases.Sum(r => r.Tracks.Value?.Count ?? 0) == 0)
        {
            _logger.Debug(
                "Yandex Music lightweight requests: stored audiobook {0} has no tracks; forcing full /with-tracks refresh",
                requestedId);
            return null;
        }

        // v0.2.9 changed Yandex release/track ids to a collision-safe schema.
        // Do not let lightweight mode freeze a pre-v0.2.9 graph in the database:
        // force one full /with-tracks refresh so Lidarr can migrate the previous
        // parent-aware ids through OldForeign*Ids. Deezer keeps its own id scheme.
        if (string.Equals(sourceName, "Yandex Music", StringComparison.OrdinalIgnoreCase) &&
            NeedsYandexChildIdMigration(existing, requestedId))
        {
            _logger.Debug(
                "Yandex Music lightweight requests: stored album {0} uses legacy release/track ids; forcing one full refresh for v0.2.9 id migration",
                requestedId);
            return null;
        }

        // RefreshArtistService always asks IProvideAlbumInfo for every local album,
        // even when direct-albums already confirms that the album still exists.
        // In lightweight mode reuse Lidarr's already-synchronised album graph so
        // no /albums/{id}/with-tracks request is needed. New albums are inserted
        // with LastInfoSync == DateTime.MinValue, so they deliberately fall
        // through to the normal full Yandex request above.
        var primaryArtist = existing.ArtistMetadata.Value;
        var releases = existing.AlbumReleases.Value;
        var metadata = new List<ArtistMetadata> { primaryArtist };

        foreach (var release in releases)
        {
            foreach (var track in release.Tracks.Value)
            {
                var trackArtist = track.ArtistMetadata?.Value;
                if (trackArtist != null)
                    metadata.Add(trackArtist);
            }
        }

        metadata = metadata
            .Where(x => x != null && !string.IsNullOrWhiteSpace(x.ForeignArtistId))
            .DistinctBy(x => x.ForeignArtistId)
            .ToList();

        // Lidarr's album-list statistics are calculated from the Tracks table, but
        // only count tracks as available when Album.ReleaseDate is not null and is
        // not in the future. Yandex audiobooks can have a complete local track graph
        // while omitting year/releaseDate entirely. Repair that stored graph before
        // returning it so lightweight mode does not preserve a permanent 0/0 status.
        NormalizeStoredAudiobookForLidarrStatistics(existing);

        _logger.Debug(
            "{0} lightweight requests: reusing stored album {1} ({2} releases, {3} tracks); skipping remote album request",
            sourceName,
            requestedId,
            releases.Count,
            releases.Sum(r => r.Tracks.Value.Count));

        return Tuple.Create(primaryArtist.ForeignArtistId, existing, metadata);
    }


    private static bool NeedsYandexChildIdMigration(Album existing, string requestedId)
    {
        if (!YandexIdParser.TryAlbumId(requestedId, out var albumId))
            return false;

        long? parentArtistId = null;
        if (YandexIdParser.TryAlbumParentArtistId(requestedId, out var parentId))
            parentArtistId = parentId;

        // v0.2.9 could save an audiobook added by direct album URL as the bare
        // yandex:album:<id>, even though its ArtistMetadata already identified the
        // narrator. Do not reuse that graph in lightweight mode: force a full
        // refresh so GetAlbumInfo can canonicalize it to
        // yandex:album:<id>:artist:<narratorId>.
        var storedPrimaryArtist = existing.ArtistMetadata.Value;
        if (!parentArtistId.HasValue &&
            string.Equals(existing.Disambiguation, "Audiobook", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.ForeignAlbumId, YandexIdParser.AlbumForeignId(albumId), StringComparison.OrdinalIgnoreCase) &&
            storedPrimaryArtist != null &&
            TryParseYandexArtistId(storedPrimaryArtist.ForeignArtistId).HasValue)
        {
            return true;
        }

        foreach (var release in existing.AlbumReleases.Value)
        {
            if (!YandexIdParser.IsCurrentReleaseForeignId(release.ForeignReleaseId, albumId, parentArtistId))
                return true;

            foreach (var track in release.Tracks.Value)
            {
                if (!YandexIdParser.IsCurrentTrackForeignId(track.ForeignTrackId, albumId, parentArtistId))
                    return true;
            }
        }

        return false;
    }

    private Artist GetPodcastArtistInfo(long albumId, bool lookup = false)
    {
        var source = _client.GetAlbum(albumId);
        if (!IsPodcast(source))
            throw new InvalidOperationException($"Yandex album {albumId} is not a podcast");

        return MapPodcastArtist(source, GetSettings(), lookup);
    }

    private Tuple<string, Album, List<ArtistMetadata>> GetPodcastAlbumInfo(
        YandexAlbum source,
        YandexMusicMetadataSettings settings)
    {
        var primaryMetadata = MapPodcastArtistMetadata(source, settings);
        var artists = new List<ArtistMetadata> { primaryMetadata };
        var dict = new Dictionary<string, ArtistMetadata>
        {
            [primaryMetadata.ForeignArtistId] = primaryMetadata
        };

        var album = MapAlbum(source, dict, includeTracks: true, settings, parentArtistId: null, new Ratings());
        album.ArtistMetadata = primaryMetadata;
        album.Artist = new Artist { Metadata = primaryMetadata };

        _logger.Debug(
            "Yandex Music podcast {0}: syntheticArtist={1}, volumes={2}, tracks={3}",
            source.Id,
            primaryMetadata.ForeignArtistId,
            source.Volumes.Count,
            album.AlbumReleases.Value.Sum(r => r.Tracks.Value.Count));

        return Tuple.Create(primaryMetadata.ForeignArtistId, album, artists);
    }

    private static Artist MapPodcastArtist(
        YandexAlbum source,
        YandexMusicMetadataSettings settings,
        bool lookup)
    {
        var metadata = MapPodcastArtistMetadata(source, settings, lookup);
        var dict = new Dictionary<string, ArtistMetadata>
        {
            [metadata.ForeignArtistId] = metadata
        };

        var album = MapAlbum(source, dict, includeTracks: false, settings, parentArtistId: null, new Ratings());
        album.ArtistMetadata = metadata;

        return new Artist
        {
            Metadata = metadata,
            CleanName = NzbDrone.Core.Parser.Parser.CleanArtistName(metadata.Name),
            SortName = NzbDrone.Core.Parser.Parser.NormalizeTitle(metadata.Name),
            Albums = new List<Album> { album }
        };
    }

    private static ArtistMetadata MapPodcastArtistMetadata(
        YandexAlbum source,
        YandexMusicMetadataSettings settings,
        bool lookup = false)
    {
        var links = settings.ExternalLinks
            ? new List<Links>
            {
                new() { Url = $"https://music.yandex.ru/album/{source.Id}", Name = "Yandex Music" }
            }
            : new List<Links>();

        // A podcast has no Yandex artist object at all. Reuse the podcast cover as
        // the synthetic artist portrait so Lidarr's Add New and artist pages have
        // usable artwork immediately. The same extension-safe URL works both for
        // lookup and persistent artwork through the plugin media-cover proxy.
        var images = settings.ArtistImages &&
                     ToImage(source.CoverUri ?? source.OgImage, MediaCoverTypes.Poster, settings.ArtistImagesOriginalQuality) is { } image
            ? new List<CoreMediaCover> { image }
            : new List<CoreMediaCover>();

        return new ArtistMetadata
        {
            Name = source.Title,
            Aliases = new List<string>(),
            ForeignArtistId = YandexIdParser.PodcastArtistForeignId(source.Id),
            OldForeignArtistIds = new List<string>(),
            Genres = settings.Genres && !string.IsNullOrWhiteSpace(source.Genre)
                ? new List<string> { source.Genre! }
                : new List<string>(),
            Overview = BuildAlbumOverview(source),
            Disambiguation = "Yandex Music Podcast",
            Type = "Podcast",
            Status = ArtistStatusType.Continuing,
            Ratings = new Ratings(),
            Images = images,
            Links = links
        };
    }

    private Artist MapArtist(YandexArtistResult result, YandexMusicMetadataSettings settings)
    {
        var metadata = MapArtistMetadata(result.Artist, settings, result.AllCovers);
        var artist = new Artist
        {
            Metadata = metadata,
            CleanName = NzbDrone.Core.Parser.Parser.CleanArtistName(metadata.Name),
            SortName = NzbDrone.Core.Parser.Parser.NormalizeTitle(metadata.Name)
        };

        // result.albums and result.alsoAlbums already express Yandex Music's
        // relationship between this artist and the release. Do not require the
        // target artist to also be present in album.artists: for compilations and
        // participation releases Yandex often puts only "сборник" or another main
        // artist there.
        IEnumerable<YandexAlbum> releases = result.Albums;
        if (settings.IncludeAlsoAlbums)
            releases = releases.Concat(result.AlsoAlbums);

        var parentArtistId = result.Artist.Id;
        var maxDirectLikes = settings.CalculateAlbumRatings && result.Albums.Count > 0
            ? result.Albums.Max(a => a.LikesCount)
            : 0;

        // Load the current DB/exclusion state once per artist refresh. Calling
        // GetAllAlbums()/All() for every release is unnecessarily expensive for
        // narrators with hundreds of books.
        var existingAlbums = _albumService.GetAllAlbums();
        var localArtistMetadataId = _artistService.FindById(metadata.ForeignArtistId)?.ArtistMetadataId;
        var exclusionIds = _importListExclusionService.All()
            .Select(x => x.ForeignId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        artist.Albums = releases
            .Where(a => ShouldIncludeRelease(a, settings))
            .GroupBy(a => a.Id)
            .Select(g => g.First())
            .Where(a => !ShouldExcludeDuplicateAudiobook(a, metadata, parentArtistId, localArtistMetadataId, existingAlbums, exclusionIds))
            .Select(a => MapAlbumSummary(a, metadata, settings, parentArtistId, maxDirectLikes))
            .ToList();

        return artist;
    }

    private bool ShouldExcludeDuplicateAudiobook(
        YandexAlbum source,
        ArtistMetadata currentArtist,
        long currentParentArtistId,
        int? currentArtistMetadataId,
        IReadOnlyCollection<Album> existingAlbums,
        HashSet<string> exclusionIds)
    {
        if (!IsAudiobook(source))
            return false;

        var currentForeignAlbumId = YandexIdParser.AlbumForeignId(source.Id, currentParentArtistId);
        var existingCopies = existingAlbums
            .Where(a =>
                YandexIdParser.TryAlbumId(a.ForeignAlbumId, out var existingAlbumId) &&
                existingAlbumId == source.Id)
            .ToList();

        if (existingCopies.Count == 0)
            return exclusionIds.Contains(currentForeignAlbumId);

        // If this exact narrator copy is already in Lidarr, it is the canonical
        // copy for this artist refresh and must remain in the remote graph. This
        // also prevents an already-added album from being hidden merely because
        // a stale exclusion exists.
        if (existingCopies.Any(a => IsAlbumOwnedByArtist(a, currentParentArtistId, currentArtistMetadataId)))
            return false;

        // The same raw Yandex audiobook ID already exists under another narrator.
        // Keep the first stored copy globally, hide this narrator-specific copy
        // from RefreshArtistService, and persist the exact canonical ID in Lidarr's
        // Import List Exclusions so an import list cannot add it later either.
        EnsureAudiobookImportListExclusion(
            currentForeignAlbumId,
            currentArtist.Name,
            source.Title,
            exclusionIds);

        var existingIds = string.Join(", ", existingCopies.Select(a => a.ForeignAlbumId));
        _logger.Info(
            "Yandex Music audiobook {0} [{1}] already exists under another narrator ({2}); excluding {3}",
            source.Id,
            source.Title,
            existingIds,
            currentForeignAlbumId);

        return true;
    }

    private void EnsureAudiobookImportListExclusion(
        string foreignAlbumId,
        string artistName,
        string albumTitle,
        HashSet<string> exclusionIds)
    {
        if (exclusionIds.Contains(foreignAlbumId))
            return;

        try
        {
            _importListExclusionService.Add(new ImportListExclusion
            {
                ForeignId = foreignAlbumId,
                Name = $"{artistName} - {albumTitle}"
            });
        }
        catch (Exception ex)
        {
            // A bulk refresh can process related narrators close together. If
            // another refresh inserted the same exclusion between our snapshot
            // and Add(), treat the unique-key race as success. Re-throw genuine
            // persistence failures so Lidarr can retry the metadata refresh.
            if (_importListExclusionService.FindByForeignId(foreignAlbumId) == null)
                throw;

            _logger.Debug(ex,
                "Yandex Music audiobook exclusion {0} was inserted concurrently",
                foreignAlbumId);
        }

        exclusionIds.Add(foreignAlbumId);
    }

    private static bool IsAlbumOwnedByArtist(Album album, long artistId, int? currentArtistMetadataId)
    {
        if (YandexIdParser.TryAlbumParentArtistId(album.ForeignAlbumId, out var encodedArtistId))
            return encodedArtistId == artistId;

        // Compatibility with pre-parent-aware bare album IDs. GetAllAlbums() does
        // not eager-load ArtistMetadata, so never touch Album.ArtistMetadata.Value
        // here (that causes an N+1 database query storm on large libraries).
        // ArtistMetadataId is an eager scalar column and is sufficient to identify
        // whether the legacy bare album already belongs to the current narrator.
        return currentArtistMetadataId.HasValue &&
               album.ArtistMetadataId == currentArtistMetadataId.Value;
    }

    private static bool ShouldIncludeRelease(YandexAlbum source, YandexMusicMetadataSettings settings)
    {
        // Podcasts are represented as their own synthetic Lidarr artists. If a
        // Yandex artist endpoint ever also exposes a podcast, do not duplicate it
        // under the host/narrator's normal music discography.
        if (IsPodcast(source))
            return false;

        return source.Type?.ToLowerInvariant() switch
        {
            "single" => settings.IncludeSingles,
            "ep" => settings.IncludeEPs,
            _ => settings.IncludeAlbums
        };
    }

    private ArtistMetadata MapAlbumArtistMetadata(
        YandexArtist compactArtist,
        YandexMusicMetadataSettings settings)
    {
        try
        {
            var profile = _client.GetArtistProfile(compactArtist.Id);
            return MapArtistMetadata(profile.Artist, settings, profile.AllCovers);
        }
        catch (YandexMusicRateLimitException ex)
        {
            // Never let a 429 overwrite an already persisted rich artist profile
            // with compact album-level metadata. Failing this album refresh is
            // safer; Lidarr can retry it later after the Yandex limit clears.
            _logger.Warn(ex,
                "Yandex Music rate limit persisted for album artist {0}; preserving existing artist metadata",
                compactArtist.Id);
            throw;
        }
        catch (Exception ex)
        {
            // For non-rate-limit failures the compact metadata still keeps the
            // album usable, and a later artist refresh can enrich it.
            _logger.Warn(ex,
                "Yandex Music artist profile lookup failed for album artist {0}; using compact album artist metadata",
                compactArtist.Id);
            return MapArtistMetadata(compactArtist, settings);
        }
    }

    private static ArtistMetadata MapArtistMetadata(
        YandexArtist source,
        YandexMusicMetadataSettings settings,
        IEnumerable<YandexCover>? covers = null)
    {
        var images = BuildArtistImages(source, settings, covers);

        var links = new List<Links>();
        if (settings.ExternalLinks)
        {
            links.AddRange(source.Links
                .Select(x => new
                {
                    Url = !string.IsNullOrWhiteSpace(x.Href) ? x.Href : x.Url,
                    Name = x.SocialNetwork ?? x.Title ?? x.Subtitle ?? x.Type ?? "link"
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Url))
                .Select(x => new Links { Url = x.Url!, Name = x.Name }));
            links.Add(new Links { Url = $"https://music.yandex.ru/artist/{source.Id}", Name = "Yandex Music" });
        }

        return new ArtistMetadata
        {
            Name = source.Name,
            Aliases = source.DbAliases ?? new List<string>(),
            ForeignArtistId = YandexIdParser.ArtistForeignId(source.Id),
            OldForeignArtistIds = new List<string>(),
            Genres = settings.Genres ? source.Genres ?? new List<string>() : new List<string>(),
            Overview = source.Description ?? string.Empty,
            Disambiguation = string.Empty,
            Type = source.ArtistType ?? string.Empty,
            Status = ArtistStatusType.Continuing,
            Ratings = new Ratings(),
            Images = images,
            Links = links
        };
    }

    private static Album MapAlbumSummary(
        YandexAlbum source,
        ArtistMetadata primaryArtist,
        YandexMusicMetadataSettings settings,
        long parentArtistId,
        int maxDirectLikes)
    {
        var dict = new Dictionary<string, ArtistMetadata> { [primaryArtist.ForeignArtistId] = primaryArtist };
        foreach (var a in source.Artists)
        {
            var mapped = MapArtistMetadata(a, settings);
            dict.TryAdd(mapped.ForeignArtistId, mapped);
        }

        var ratings = settings.CalculateAlbumRatings
            ? BuildAlbumRatings(source.LikesCount, maxDirectLikes)
            : new Ratings();
        var album = MapAlbum(source, dict, includeTracks: false, settings, parentArtistId, ratings);
        album.ArtistMetadata = primaryArtist;
        return album;
    }

    private static Album MapAlbum(
        YandexAlbum source,
        Dictionary<string, ArtistMetadata> artists,
        bool includeTracks,
        YandexMusicMetadataSettings settings,
        long? parentArtistId,
        Ratings ratings)
    {
        var releaseDate = GetReleaseDate(source, settings);
        var canonicalAlbumId = YandexIdParser.AlbumForeignId(source.Id, parentArtistId);
        var oldAlbumIds = parentArtistId.HasValue
            ? new List<string> { YandexIdParser.AlbumForeignId(source.Id) }
            : new List<string>();

        var album = new Album
        {
            ForeignAlbumId = canonicalAlbumId,
            OldForeignAlbumIds = oldAlbumIds,
            Title = source.Title,
            Overview = BuildAlbumOverview(source),
            Disambiguation = IsPodcast(source) ? "Podcast" : IsAudiobook(source) ? "Audiobook" : string.Empty,
            ReleaseDate = releaseDate,
            Images = settings.AlbumCovers && ToImage(source.CoverUri ?? source.OgImage, MediaCoverTypes.Cover, settings.AlbumCoversOriginalQuality) is { } img
                ? new List<CoreMediaCover> { img }
                : new List<CoreMediaCover>(),
            AlbumType = MapAlbumType(source.Type),
            SecondaryTypes = IsAudiobook(source) || IsPodcast(source)
                ? new List<SecondaryAlbumType>()
                : new List<SecondaryAlbumType> { SecondaryAlbumType.Studio },
            Ratings = ratings,
            Links = settings.ExternalLinks
                ? new List<Links> { new() { Url = $"https://music.yandex.ru/album/{source.Id}", Name = "Yandex Music" } }
                : new List<Links>(),
            Genres = settings.Genres && !string.IsNullOrWhiteSpace(source.Genre)
                ? new List<string> { source.Genre! }
                : new List<string>(),
            CleanTitle = NzbDrone.Core.Parser.Parser.CleanArtistName(source.Title),
            AnyReleaseOk = true
        };

        var release = MapRelease(source, artists, includeTracks, settings, parentArtistId);
        release.Monitored = true;
        album.AlbumReleases = new List<AlbumRelease> { release };
        return album;
    }

    private static AlbumRelease MapRelease(
        YandexAlbum source,
        Dictionary<string, ArtistMetadata> artists,
        bool includeTracks,
        YandexMusicMetadataSettings settings,
        long? parentArtistId)
    {
        var tracks = new List<Track>();
        var media = new List<Medium>();
        if (includeTracks)
        {
            for (var volumeIndex = 0; volumeIndex < source.Volumes.Count; volumeIndex++)
            {
                var volume = source.Volumes[volumeIndex];
                for (var i = 0; i < volume.Count; i++)
                {
                    var sourceTrack = volume[i];
                    var trackContributors = ExpandArtistContributors(sourceTrack.Artists)
                        .Where(a => a.Id > 0)
                        .GroupBy(a => a.Id)
                        .Select(g => g.First())
                        .ToList();
                    var trackArtist = trackContributors.FirstOrDefault();

                    // Preserve every contributor in the metadata dictionary even
                    // though Lidarr's Track model can reference only one artist.
                    // This is important for audiobook ensembles and decomposed
                    // credits: RefreshAlbumService must know every ArtistMetadata
                    // object referenced by the returned graph.
                    foreach (var contributor in trackContributors)
                    {
                        var contributorKey = YandexIdParser.ArtistForeignId(contributor.Id);
                        if (!artists.ContainsKey(contributorKey))
                            artists[contributorKey] = MapArtistMetadata(contributor, settings);
                    }

                    ArtistMetadata artistMetadata;
                    if (trackArtist != null)
                    {
                        var key = YandexIdParser.ArtistForeignId(trackArtist.Id);
                        artistMetadata = artists[key];
                    }
                    else
                    {
                        // Podcast episodes intentionally have artists:[]. Their
                        // synthetic podcast artist is the only entry in this map.
                        artistMetadata = artists.Values.First();
                    }

                    var position = GetTrackPosition(source, sourceTrack);
                    var mediumNumber = position?.Volume > 0 ? position.Volume : volumeIndex + 1;
                    var trackNumber = position?.Index > 0 ? position.Index : i + 1;

                    tracks.Add(new Track
                    {
                        ArtistMetadata = artistMetadata,
                        Title = sourceTrack.Title,
                        ForeignTrackId = YandexIdParser.TrackForeignId(sourceTrack.Id, source.Id, parentArtistId),
                        OldForeignTrackIds = new List<string>
                        {
                            YandexIdParser.LegacyTrackForeignId(sourceTrack.Id, parentArtistId)
                        },
                        ForeignRecordingId = YandexIdParser.RecordingForeignId(sourceTrack.Id, source.Id, parentArtistId),
                        OldForeignRecordingIds = new List<string>
                        {
                            YandexIdParser.LegacyRecordingForeignId(sourceTrack.Id, parentArtistId)
                        },
                        TrackNumber = trackNumber.ToString(),
                        AbsoluteTrackNumber = 0,
                        Duration = sourceTrack.DurationMs,
                        MediumNumber = mediumNumber
                    });
                }
            }

            // Podcast /with-tracks responses use sortOrder=desc: newest seasons and
            // episodes come first. trackPosition carries the authoritative season
            // and episode numbers, so order by those values before handing the graph
            // to Lidarr. Normal albums without trackPosition keep their usual order.
            tracks = tracks
                .OrderBy(t => t.MediumNumber)
                .ThenBy(t => int.TryParse(t.TrackNumber, out var n) ? n : int.MaxValue)
                .ToList();

            for (var i = 0; i < tracks.Count; i++)
                tracks[i].AbsoluteTrackNumber = i + 1;

            media = tracks
                .Select(t => t.MediumNumber)
                .Distinct()
                .OrderBy(n => n)
                .Select(n => new Medium { Name = "Digital Media", Number = n, Format = "Digital Media" })
                .ToList();
        }

        return new AlbumRelease
        {
            ForeignReleaseId = YandexIdParser.ReleaseForeignId(source.Id, parentArtistId),
            OldForeignReleaseIds = new List<string>
            {
                YandexIdParser.LegacyReleaseForeignId(source.Id, parentArtistId)
            },
            Title = source.Title,
            Status = "Official",
            Label = settings.Labels
                ? source.Labels.Select(l => l.Name).Where(x => !string.IsNullOrWhiteSpace(x)).ToList()
                : new List<string>(),
            Disambiguation = IsPodcast(source) ? "Podcast" : IsAudiobook(source) ? "Audiobook" : string.Empty,
            Country = new List<string>(),
            ReleaseDate = GetReleaseDate(source, settings),
            Tracks = tracks,
            TrackCount = includeTracks && tracks.Count > 0 ? tracks.Count : GetEffectiveTrackCount(source),
            Media = media,
            Duration = GetEffectiveDurationMs(source, tracks)
        };
    }

    private Ratings BuildAlbumRatings(YandexAlbum source, long? artistId)
    {
        if (!artistId.HasValue)
            return new Ratings();

        try
        {
            var context = _client.GetAlbumLikesContext(artistId.Value, source.Id);
            var likesCount = source.LikesCount > 0 ? source.LikesCount : context.LikesCount;
            return BuildAlbumRatings(likesCount, context.MaxLikesCount);
        }
        catch (Exception ex)
        {
            _logger.Warn(ex,
                "Yandex Music album rating calculation failed for album {0}, artist {1}",
                source.Id,
                artistId.Value);
            return new Ratings();
        }
    }

    private static Ratings BuildAlbumRatings(int likesCount, int maxLikesCount)
    {
        if (likesCount <= 0 || maxLikesCount <= 0)
            return new Ratings();

        // Lidarr's Ratings.Value is decimal. Keep the calculation in decimal
        // from the start so the provider compiles against the plugins branch
        // and no precision is lost through double -> decimal conversion.
        var lidarrValue = Math.Clamp(
            Math.Round((decimal)likesCount / maxLikesCount * 10m, 1, MidpointRounding.AwayFromZero),
            0m,
            10m);

        return new Ratings
        {
            Votes = likesCount,
            Value = lidarrValue
        };
    }

    private static readonly DateTime UnknownAudiobookReleaseDate = new(1970, 1, 1);

    private static DateTime? GetReleaseDate(YandexAlbum source, YandexMusicMetadataSettings settings)
    {
        if (settings.PreferExactReleaseDate && source.ReleaseDate.HasValue)
            return source.ReleaseDate;

        if (source.Year > 0)
            return new DateTime(source.Year, 1, 1);

        if (source.ReleaseDate.HasValue)
            return source.ReleaseDate;

        // Podcasts can omit album-level releaseDate/year even though every episode
        // carries pubDate. Use the earliest known episode date as the podcast start
        // date; this is materially better than a synthetic sentinel and also keeps
        // Lidarr's album-list statistics from treating the podcast as unreleased.
        if (IsPodcast(source))
        {
            var firstEpisodeDate = source.Volumes
                .SelectMany(volume => volume)
                .Where(track => track.PubDate.HasValue)
                .Select(track => track.PubDate!.Value.Date)
                .OrderBy(date => date)
                .FirstOrDefault();

            if (firstEpisodeDate != default)
                return firstEpisodeDate;
        }

        // Yandex sometimes omits every date field for audiobooks. Lidarr's list
        // statistics SQL treats a null Album.ReleaseDate as "not released" and
        // therefore reports 0/0 even though the album detail page has all tracks.
        // Once the detailed album data proves that the audiobook has real parts,
        // use a deterministic past sentinel so Lidarr can count those tracks.
        // A real Yandex date/year always wins above and is never replaced.
        return IsAudiobook(source) && GetEffectiveTrackCount(source) > 0
            ? UnknownAudiobookReleaseDate
            : null;
    }

    private static void NormalizeStoredAudiobookForLidarrStatistics(Album album)
    {
        if (album.ReleaseDate.HasValue ||
            !string.Equals(album.Disambiguation, "Audiobook", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var releases = album.AlbumReleases.Value;
        var hasTracks = false;

        foreach (var release in releases)
        {
            var tracks = release.Tracks.Value;
            var effectiveTrackCount = release.TrackCount > 0 ? release.TrackCount : tracks.Count;
            if (effectiveTrackCount <= 0)
                continue;

            hasTracks = true;
            if (release.TrackCount <= 0)
                release.TrackCount = effectiveTrackCount;
            if (!release.ReleaseDate.HasValue)
                release.ReleaseDate = UnknownAudiobookReleaseDate;
        }

        if (hasTracks)
            album.ReleaseDate = UnknownAudiobookReleaseDate;
    }

    private static List<YandexArtist> ExpandArtistContributors(IEnumerable<YandexArtist>? sourceArtists)
    {
        var result = new List<YandexArtist>();
        if (sourceArtists == null)
            return result;

        var seen = new HashSet<long>();

        void AddArtist(YandexArtist? artist)
        {
            if (artist == null || artist.Id <= 0)
                return;

            if (seen.Add(artist.Id))
                result.Add(artist);

            if (artist.Decomposed == null)
                return;

            foreach (var token in artist.Decomposed)
            {
                if (token is not JObject obj)
                    continue;

                try
                {
                    AddArtist(obj.ToObject<YandexArtist>());
                }
                catch
                {
                    // "decomposed" is a mixed array that also contains separators.
                    // Ignore malformed/non-artist objects rather than failing an
                    // otherwise valid audiobook response.
                }
            }
        }

        foreach (var artist in sourceArtists)
            AddArtist(artist);

        return result;
    }

    private static YandexArtist? GetSoleTrackArtist(YandexAlbum source)
    {
        var artists = source.Volumes
            .SelectMany(volume => volume ?? new List<YandexTrack>())
            .SelectMany(track => ExpandArtistContributors(track.Artists))
            .Where(artist => artist.Id > 0)
            .GroupBy(artist => artist.Id)
            .Select(group => group.First())
            .Take(2)
            .ToList();

        return artists.Count == 1 ? artists[0] : null;
    }

    private static YandexArtist? GetFirstTrackArtist(YandexAlbum source)
    {
        return source.Volumes
            .SelectMany(volume => volume ?? new List<YandexTrack>())
            .SelectMany(track => ExpandArtistContributors(track.Artists))
            .FirstOrDefault(artist => artist.Id > 0);
    }

    private static long? TryParseYandexArtistId(string? foreignArtistId)
    {
        return !string.IsNullOrWhiteSpace(foreignArtistId) &&
               YandexIdParser.TryArtistId(foreignArtistId, out var artistId)
            ? artistId
            : null;
    }

    private static bool IsAudiobook(YandexAlbum source) =>
        string.Equals(source.Type, "audiobook", StringComparison.OrdinalIgnoreCase);

    private static bool IsPodcast(YandexAlbum source) =>
        string.Equals(source.Type, "podcast", StringComparison.OrdinalIgnoreCase);

    private static YandexTrackPosition? GetTrackPosition(YandexAlbum album, YandexTrack track)
    {
        return track.Albums
            .FirstOrDefault(x => x.Id == album.Id)
            ?.TrackPosition;
    }

    private static int GetEffectiveTrackCount(YandexAlbum source)
    {
        if (source.TrackCount > 0)
            return source.TrackCount;

        var volumeTrackCount = source.Volumes?.Sum(volume => volume?.Count ?? 0) ?? 0;
        if (volumeTrackCount > 0)
            return volumeTrackCount;

        return source.Pager?.Total ?? 0;
    }

    private static int GetEffectiveDurationMs(YandexAlbum source, IReadOnlyCollection<Track> tracks)
    {
        if (tracks.Count > 0)
        {
            var trackDuration = tracks.Sum(track => (long)track.Duration);
            return (int)Math.Min(trackDuration, int.MaxValue);
        }

        if (source.DurationSec <= 0)
            return 0;

        return (int)Math.Min((long)source.DurationSec * 1000L, int.MaxValue);
    }

    private static string BuildAlbumOverview(YandexAlbum source)
    {
        var description = !string.IsNullOrWhiteSpace(source.Description)
            ? source.Description
            : source.ShortDescription;

        if (string.IsNullOrWhiteSpace(description))
            return string.Empty;

        // Yandex audiobook/podcast descriptions are HTML fragments with encoded entities.
        // Lidarr expects plain text in Album.Overview.
        var value = System.Text.RegularExpressions.Regex.Replace(
            description,
            @"<br\s*/?>",
            "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        value = System.Text.RegularExpressions.Regex.Replace(
            value,
            @"</p\s*>",
            "\n\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        value = System.Text.RegularExpressions.Regex.Replace(value, @"<[^>]+>", string.Empty);
        value = System.Net.WebUtility.HtmlDecode(value);
        value = value.Replace("\r\n", "\n").Replace('\r', '\n');
        value = System.Text.RegularExpressions.Regex.Replace(value, @"[ \t]+\n", "\n");
        value = System.Text.RegularExpressions.Regex.Replace(value, @"\n{3,}", "\n\n");

        return value.Trim();
    }

    private static string MapAlbumType(string? type) => type?.ToLowerInvariant() switch
    {
        "single" => "Single",
        "ep" => "EP",
        _ => "Album"
    };

    private static List<CoreMediaCover> BuildArtistLookupImages(
        YandexArtist source,
        YandexMusicMetadataSettings settings,
        IEnumerable<YandexCover>? covers = null)
    {
        if (!settings.ArtistImages)
            return new List<CoreMediaCover>();

        var availableUris = GetArtistImageUris(source, covers);
        if (availableUris.Count == 0)
            return new List<CoreMediaCover>();

        var selectedUri = settings.RandomArtistImage && availableUris.Count > 1
            ? availableUris[Random.Shared.Next(availableUris.Count)]
            : availableUris[0];

        // Use the same extension-preserving JPEG URL as persistent artwork.
        // YandexMusicMediaCoverProxy keeps the original remote URL in Lidarr's
        // proxy cache, but exposes a .jpg public proxy path required by
        // MediaCoverProxyMapper during artist lookup.
        return ToImage(selectedUri, MediaCoverTypes.Poster, settings.ArtistImagesOriginalQuality) is { } image
            ? new List<CoreMediaCover> { image }
            : new List<CoreMediaCover>();
    }

    private static List<string> GetArtistImageUris(
        YandexArtist source,
        IEnumerable<YandexCover>? covers = null)
    {
        var uris = (covers ?? Array.Empty<YandexCover>())
            .Select(c => c.Uri)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToList();

        if (!string.IsNullOrWhiteSpace(source.Cover?.Uri))
            uris.Add(source.Cover.Uri!);

        if (!string.IsNullOrWhiteSpace(source.OgImage))
            uris.Add(source.OgImage!);

        return uris
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? BuildYandexImageUrl(string? uri, bool originalQuality)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        var url = uri.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? uri : "https://" + uri;
        return url.Replace("%%", originalQuality ? "orig" : "1000x1000");
    }

    private static List<CoreMediaCover> BuildArtistImages(
        YandexArtist source,
        YandexMusicMetadataSettings settings,
        IEnumerable<YandexCover>? covers = null)
    {
        if (!settings.ArtistImages)
            return new List<CoreMediaCover>();

        var availableUris = GetArtistImageUris(source, covers);

        if (availableUris.Count == 0)
            return new List<CoreMediaCover>();

        var selectedUri = settings.RandomArtistImage && availableUris.Count > 1
            ? availableUris[Random.Shared.Next(availableUris.Count)]
            : availableUris[0];

        return ToImage(selectedUri, MediaCoverTypes.Poster, settings.ArtistImagesOriginalQuality) is { } image
            ? new List<CoreMediaCover> { image }
            : new List<CoreMediaCover>();
    }

    private static CoreMediaCover? ToImage(string? uri, MediaCoverTypes type, bool originalQuality = false)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;

        var remoteUrl = uri.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? uri : "https://" + uri;
        remoteUrl = remoteUrl.Replace("%%", originalQuality ? "orig" : "1000x1000");

        // Persist the file-extension hint inside Url itself. Lidarr serializes and
        // later reconstructs MediaCover objects; Extension has a private setter and
        // may be lost during that round-trip. Kodi/Emby then writes "folder" with no
        // extension. Keeping a harmless query parameter that ends in image.jpg means
        // Path.GetExtension(Url) can always reconstruct ".jpg" after deserialization.
        // webp=false requests the JPEG variant from Yandex.
        remoteUrl += remoteUrl.Contains('?', StringComparison.Ordinal)
            ? "&webp=false&filename=image.jpg"
            : "?webp=false&filename=image.jpg";

        // Also seed Extension immediately for the in-memory object used during the
        // current refresh. On later reloads it can be derived again from remoteUrl.
        var cover = new CoreMediaCover(type, "image.jpg")
        {
            RemoteUrl = remoteUrl
        };
        cover.Url = remoteUrl;
        return cover;
    }
}
