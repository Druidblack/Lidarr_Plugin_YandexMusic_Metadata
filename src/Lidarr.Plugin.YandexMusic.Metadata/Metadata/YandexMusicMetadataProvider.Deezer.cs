using System.Globalization;
using NzbDrone.Core.MediaCover;
using CoreMediaCover = NzbDrone.Core.MediaCover.MediaCover;
using NzbDrone.Core.Music;
using Lidarr.Plugin.YandexMusicMetadata.Api;
using Lidarr.Plugin.YandexMusicMetadata.Utility;

namespace Lidarr.Plugin.YandexMusicMetadata.Metadata;

public sealed partial class YandexMusicMetadataProvider
{
    private Artist GetDeezerArtistInfo(long id)
    {
        var settings = GetDeezerSettings();
        var source = _deezerClient.GetArtist(id);
        var artist = MapDeezerArtist(source, settings, lookup: false);

        _logger.Debug(
            "Deezer artist {0}: albums={1}, returned={2}",
            id,
            source.Albums.Count,
            artist.Albums.Value.Count);

        return artist;
    }

    private Tuple<string, Album, List<ArtistMetadata>> GetDeezerAlbumInfo(string requestedId, long albumId)
    {
        var settings = GetDeezerSettings();

        if (settings.LightweightAlbumRequests)
        {
            var stored = TryGetStoredAlbumInfo(requestedId, "Deezer");
            if (stored != null)
                return stored;
        }

        var source = _deezerClient.GetAlbum(albumId);

        long? parentArtistId = null;
        if (DeezerIdParser.TryAlbumParentArtistId(requestedId, out var encodedParentArtistId))
            parentArtistId = encodedParentArtistId;

        var compactArtists = source.Contributors
            .Concat(source.Artist.Id != 0 ? new[] { source.Artist } : Array.Empty<DeezerArtist>())
            .Where(x => x.Id != 0)
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .ToList();

        var artists = compactArtists
            .Select(a => MapDeezerAlbumArtistMetadata(a, settings))
            .GroupBy(a => a.ForeignArtistId)
            .Select(g => g.First())
            .ToList();

        var dict = artists.ToDictionary(a => a.ForeignArtistId, a => a);
        ArtistMetadata primaryMetadata;

        if (parentArtistId.HasValue)
        {
            var parentForeignId = DeezerIdParser.ArtistForeignId(parentArtistId.Value);
            if (!dict.TryGetValue(parentForeignId, out primaryMetadata!))
            {
                primaryMetadata = MapDeezerArtistMetadata(_deezerClient.GetArtistProfile(parentArtistId.Value), settings);
                dict[parentForeignId] = primaryMetadata;
                artists.Add(primaryMetadata);
            }
        }
        else
        {
            var primaryId = source.Artist.Id != 0
                ? source.Artist.Id
                : compactArtists.FirstOrDefault()?.Id ?? 0;

            if (primaryId == 0)
                throw new InvalidOperationException($"Deezer album {albumId} has no artists");

            var primaryForeignId = DeezerIdParser.ArtistForeignId(primaryId);
            if (!dict.TryGetValue(primaryForeignId, out primaryMetadata!))
            {
                primaryMetadata = MapDeezerArtistMetadata(_deezerClient.GetArtistProfile(primaryId), settings);
                dict[primaryForeignId] = primaryMetadata;
                artists.Add(primaryMetadata);
            }
        }

        var ratingArtistId = parentArtistId ?? source.Artist.Id;
        var ratings = settings.CalculateAlbumRatings
            ? BuildDeezerAlbumRatings(source, ratingArtistId == 0 ? null : ratingArtistId)
            : new Ratings();

        var album = MapDeezerAlbum(source, dict, includeTracks: true, settings, parentArtistId, ratings);
        album.ArtistMetadata = primaryMetadata;
        album.Artist = new Artist { Metadata = primaryMetadata };

        _logger.Debug(
            "Deezer album {0}: requestedId={1}, canonicalId={2}, parent={3}, tracks={4}",
            albumId,
            requestedId,
            album.ForeignAlbumId,
            primaryMetadata.ForeignArtistId,
            album.AlbumReleases.Value.Sum(r => r.Tracks.Value.Count));

        return Tuple.Create(primaryMetadata.ForeignArtistId, album, artists);
    }

    private List<Artist> SearchDeezerArtistsByName(string title, DeezerMetadataSettings settings)
    {
        return _deezerClient.SearchArtists(title)
            .Select(a => MapDeezerArtistSearchResult(a, settings))
            .ToList();
    }

    private static Artist MapDeezerArtistSearchResult(DeezerArtist source, DeezerMetadataSettings settings)
    {
        var metadata = MapDeezerArtistMetadata(source, settings);
        metadata.Disambiguation = "Deezer";

        return new Artist
        {
            Metadata = metadata,
            CleanName = NzbDrone.Core.Parser.Parser.CleanArtistName(metadata.Name),
            SortName = NzbDrone.Core.Parser.Parser.NormalizeTitle(metadata.Name),
            Albums = new List<Album>()
        };
    }

    private Artist MapDeezerArtist(DeezerArtistResult result, DeezerMetadataSettings settings, bool lookup)
    {
        var metadata = MapDeezerArtistMetadata(result.Artist, settings);
        if (lookup)
            metadata.Disambiguation = "Deezer";

        var artist = new Artist
        {
            Metadata = metadata,
            CleanName = NzbDrone.Core.Parser.Parser.CleanArtistName(metadata.Name),
            SortName = NzbDrone.Core.Parser.Parser.NormalizeTitle(metadata.Name)
        };

        if (lookup)
        {
            artist.Albums = new List<Album>();
            return artist;
        }

        var parentArtistId = result.Artist.Id;
        var maxFans = settings.CalculateAlbumRatings && result.Albums.Count > 0
            ? result.Albums.Max(a => a.Fans ?? 0)
            : 0;

        artist.Albums = result.Albums
            .Where(a => ShouldIncludeDeezerRelease(a, settings))
            .GroupBy(a => a.Id)
            .Select(g => g.First())
            .Select(a => MapDeezerAlbumSummary(a, metadata, settings, parentArtistId, maxFans))
            .ToList();

        return artist;
    }

    private static bool ShouldIncludeDeezerRelease(DeezerAlbum source, DeezerMetadataSettings settings)
    {
        return source.RecordType?.ToLowerInvariant() switch
        {
            "single" => settings.IncludeSingles,
            "ep" => settings.IncludeEPs,
            _ => settings.IncludeAlbums
        };
    }

    private ArtistMetadata MapDeezerAlbumArtistMetadata(DeezerArtist compactArtist, DeezerMetadataSettings settings)
    {
        try
        {
            return MapDeezerArtistMetadata(_deezerClient.GetArtistProfile(compactArtist.Id), settings);
        }
        catch (Exception ex)
        {
            _logger.Warn(ex,
                "Deezer artist profile lookup failed for album artist {0}; using compact album artist metadata",
                compactArtist.Id);
            return MapDeezerArtistMetadata(compactArtist, settings);
        }
    }

    private static ArtistMetadata MapDeezerArtistMetadata(DeezerArtist source, DeezerMetadataSettings settings)
    {
        var links = new List<Links>();
        if (settings.ExternalLinks)
        {
            links.Add(new Links
            {
                Url = !string.IsNullOrWhiteSpace(source.Link)
                    ? source.Link!
                    : $"https://www.deezer.com/artist/{source.Id}",
                Name = "Deezer"
            });
        }

        var imageUrl = source.PictureXl ?? source.PictureBig ?? source.PictureMedium ?? source.Picture;
        var images = settings.ArtistImages && ToDeezerImage(imageUrl, MediaCoverTypes.Poster) is { } image
            ? new List<CoreMediaCover> { image }
            : new List<CoreMediaCover>();

        return new ArtistMetadata
        {
            Name = source.Name,
            Aliases = new List<string>(),
            ForeignArtistId = DeezerIdParser.ArtistForeignId(source.Id),
            OldForeignArtistIds = new List<string>(),
            Genres = new List<string>(),
            Overview = string.Empty,
            Disambiguation = string.Empty,
            Type = "Artist",
            Status = ArtistStatusType.Continuing,
            Ratings = new Ratings(),
            Images = images,
            Links = links
        };
    }

    private static Album MapDeezerAlbumSummary(
        DeezerAlbum source,
        ArtistMetadata primaryArtist,
        DeezerMetadataSettings settings,
        long parentArtistId,
        int maxFans)
    {
        var dict = new Dictionary<string, ArtistMetadata>
        {
            [primaryArtist.ForeignArtistId] = primaryArtist
        };

        var ratings = settings.CalculateAlbumRatings
            ? BuildDeezerAlbumRatings(source.Fans ?? 0, maxFans)
            : new Ratings();

        var album = MapDeezerAlbum(source, dict, includeTracks: false, settings, parentArtistId, ratings);
        album.ArtistMetadata = primaryArtist;
        return album;
    }

    private static Album MapDeezerAlbum(
        DeezerAlbum source,
        Dictionary<string, ArtistMetadata> artists,
        bool includeTracks,
        DeezerMetadataSettings settings,
        long? parentArtistId,
        Ratings ratings)
    {
        var releaseDate = GetDeezerReleaseDate(source, settings);
        var canonicalAlbumId = DeezerIdParser.AlbumForeignId(source.Id, parentArtistId);
        var oldAlbumIds = parentArtistId.HasValue
            ? new List<string> { DeezerIdParser.AlbumForeignId(source.Id) }
            : new List<string>();

        var coverUrl = source.CoverXl ?? source.CoverBig ?? source.CoverMedium ?? source.Cover;
        var images = settings.AlbumCovers && ToDeezerImage(coverUrl, MediaCoverTypes.Cover) is { } image
            ? new List<CoreMediaCover> { image }
            : new List<CoreMediaCover>();

        var genres = settings.Genres
            ? source.Genres?.Data?.Select(g => g.Name)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>()
            : new List<string>();

        var album = new Album
        {
            ForeignAlbumId = canonicalAlbumId,
            OldForeignAlbumIds = oldAlbumIds,
            Title = source.Title,
            Overview = string.Empty,
            Disambiguation = string.Empty,
            ReleaseDate = releaseDate,
            Images = images,
            AlbumType = MapDeezerAlbumType(source.RecordType),
            SecondaryTypes = new List<SecondaryAlbumType> { SecondaryAlbumType.Studio },
            Ratings = ratings,
            Links = settings.ExternalLinks
                ? new List<Links>
                {
                    new()
                    {
                        Url = !string.IsNullOrWhiteSpace(source.Link)
                            ? source.Link!
                            : $"https://www.deezer.com/album/{source.Id}",
                        Name = "Deezer"
                    }
                }
                : new List<Links>(),
            Genres = genres,
            CleanTitle = NzbDrone.Core.Parser.Parser.CleanArtistName(source.Title),
            AnyReleaseOk = true
        };

        var release = MapDeezerRelease(source, artists, includeTracks, settings, parentArtistId);
        release.Monitored = true;
        album.AlbumReleases = new List<AlbumRelease> { release };
        return album;
    }

    private static AlbumRelease MapDeezerRelease(
        DeezerAlbum source,
        Dictionary<string, ArtistMetadata> artists,
        bool includeTracks,
        DeezerMetadataSettings settings,
        long? parentArtistId)
    {
        var tracks = new List<Track>();
        var media = new List<Medium>();

        if (includeTracks)
        {
            var sourceTracks = source.Tracks?.Data ?? new List<DeezerTrack>();
            var diskNumbers = sourceTracks
                .Select(x => x.DiskNumber > 0 ? x.DiskNumber : 1)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            foreach (var disk in diskNumbers)
                media.Add(new Medium { Name = "Digital Media", Number = disk, Format = "Digital Media" });

            var absoluteNumber = 0;
            foreach (var sourceTrack in sourceTracks
                         .OrderBy(x => x.DiskNumber > 0 ? x.DiskNumber : 1)
                         .ThenBy(x => x.TrackPosition > 0 ? x.TrackPosition : int.MaxValue)
                         .ThenBy(x => x.Id))
            {
                var mediumNumber = sourceTrack.DiskNumber > 0 ? sourceTrack.DiskNumber : 1;
                var trackNumber = sourceTrack.TrackPosition > 0 ? sourceTrack.TrackPosition : absoluteNumber + 1;
                var trackArtist = sourceTrack.Artist.Id != 0
                    ? sourceTrack.Artist
                    : sourceTrack.Contributors.FirstOrDefault(x => x.Id != 0);

                ArtistMetadata artistMetadata;
                if (trackArtist != null && trackArtist.Id != 0)
                {
                    var key = DeezerIdParser.ArtistForeignId(trackArtist.Id);
                    if (!artists.TryGetValue(key, out artistMetadata!))
                    {
                        artistMetadata = MapDeezerArtistMetadata(trackArtist, settings);
                        artists[key] = artistMetadata;
                    }
                }
                else
                {
                    artistMetadata = artists.Values.First();
                }

                absoluteNumber++;
                tracks.Add(new Track
                {
                    ArtistMetadata = artistMetadata,
                    Title = sourceTrack.Title,
                    ForeignTrackId = DeezerIdParser.TrackForeignId(sourceTrack.Id, parentArtistId),
                    OldForeignTrackIds = parentArtistId.HasValue
                        ? new List<string> { DeezerIdParser.TrackForeignId(sourceTrack.Id) }
                        : new List<string>(),
                    ForeignRecordingId = DeezerIdParser.RecordingForeignId(sourceTrack.Id, parentArtistId),
                    OldForeignRecordingIds = parentArtistId.HasValue
                        ? new List<string> { DeezerIdParser.RecordingForeignId(sourceTrack.Id) }
                        : new List<string>(),
                    TrackNumber = trackNumber.ToString(CultureInfo.InvariantCulture),
                    AbsoluteTrackNumber = absoluteNumber,
                    Duration = Math.Max(0, sourceTrack.Duration) * 1000,
                    MediumNumber = mediumNumber
                });
            }
        }

        return new AlbumRelease
        {
            ForeignReleaseId = DeezerIdParser.ReleaseForeignId(source.Id, parentArtistId),
            OldForeignReleaseIds = parentArtistId.HasValue
                ? new List<string> { DeezerIdParser.ReleaseForeignId(source.Id) }
                : new List<string>(),
            Title = source.Title,
            Status = "Official",
            Label = settings.Labels && !string.IsNullOrWhiteSpace(source.Label)
                ? new List<string> { source.Label! }
                : new List<string>(),
            Disambiguation = string.Empty,
            Country = new List<string>(),
            ReleaseDate = GetDeezerReleaseDate(source, settings),
            Tracks = tracks,
            TrackCount = includeTracks ? tracks.Count : Math.Max(0, source.NbTracks ?? 0),
            Media = media,
            Duration = includeTracks ? tracks.Sum(x => x.Duration) : Math.Max(0, source.Duration ?? 0) * 1000
        };
    }

    private Ratings BuildDeezerAlbumRatings(DeezerAlbum source, long? artistId)
    {
        if (!artistId.HasValue)
            return new Ratings();

        try
        {
            var context = _deezerClient.GetAlbumFansContext(artistId.Value, source.Id);
            var fans = (source.Fans ?? 0) > 0 ? source.Fans!.Value : context.Fans;
            return BuildDeezerAlbumRatings(fans, context.MaxFans);
        }
        catch (Exception ex)
        {
            _logger.Warn(ex,
                "Deezer album rating calculation failed for album {0}, artist {1}",
                source.Id,
                artistId.Value);
            return new Ratings();
        }
    }

    private static Ratings BuildDeezerAlbumRatings(int fans, int maxFans)
    {
        if (fans <= 0 || maxFans <= 0)
            return new Ratings();

        var value = Math.Clamp(
            Math.Round((decimal)fans / maxFans * 10m, 1, MidpointRounding.AwayFromZero),
            0m,
            10m);

        return new Ratings
        {
            Votes = fans,
            Value = value
        };
    }

    private static DateTime? GetDeezerReleaseDate(DeezerAlbum source, DeezerMetadataSettings settings)
    {
        if (string.IsNullOrWhiteSpace(source.ReleaseDate))
            return null;

        if (!DateTime.TryParseExact(
                source.ReleaseDate,
                new[] { "yyyy-MM-dd", "yyyy-MM", "yyyy" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed) &&
            !DateTime.TryParse(source.ReleaseDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
        {
            return null;
        }

        return settings.PreferExactReleaseDate
            ? parsed
            : new DateTime(parsed.Year, 1, 1);
    }

    private static string MapDeezerAlbumType(string? type) => type?.ToLowerInvariant() switch
    {
        "single" => "Single",
        "ep" => "EP",
        _ => "Album"
    };

    private static CoreMediaCover? ToDeezerImage(string? url, MediaCoverTypes type)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var cover = new CoreMediaCover(type, "image.jpg")
        {
            RemoteUrl = url
        };
        cover.Url = url;
        return cover;
    }
}
