using Newtonsoft.Json.Linq;

namespace Lidarr.Plugin.YandexMusicMetadata.Api;




public sealed class YandexGenresResponse
{
    public List<YandexGenre> Result { get; set; } = new();
}

public sealed class YandexGenre
{
    public string Id { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? FullTitle { get; set; }
    public string? UrlPart { get; set; }
    public Dictionary<string, YandexGenreTitle> Titles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<YandexGenre> SubGenres { get; set; } = new();
}

public sealed class YandexGenreTitle
{
    public string? Title { get; set; }
    public string? FullTitle { get; set; }
}

public sealed class YandexPlaylistResponse { public YandexPlaylist Result { get; set; } = new(); }

public sealed class YandexPlaylist
{
    public string PlaylistUuid { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int TrackCount { get; set; }
    public string? Visibility { get; set; }
    public List<YandexPlaylistTrackItem> Tracks { get; set; } = new();
}

public sealed class YandexPlaylistTrackItem
{
    public string? Id { get; set; }
    public YandexTrack? Track { get; set; }
}

public sealed class YandexSearchResponse { public YandexSearchResult Result { get; set; } = new(); }

public sealed class YandexSearchResult
{
    public YandexSearchArtistBlock Artists { get; set; } = new();
    public int Page { get; set; }
    public int PerPage { get; set; }
}

public sealed class YandexSearchArtistBlock
{
    public string? Type { get; set; }
    public int Total { get; set; }
    public int PerPage { get; set; }
    public int Order { get; set; }
    public List<YandexArtist> Results { get; set; } = new();
}

public sealed class YandexArtistResponse { public YandexArtistResult Result { get; set; } = new(); }

public sealed class YandexArtistResult
{
    public YandexArtist Artist { get; set; } = new();
    public List<YandexAlbum> Albums { get; set; } = new();
    public List<YandexAlbum> AlsoAlbums { get; set; } = new();
    public List<YandexCover> AllCovers { get; set; } = new();
}

public sealed class YandexArtistAbout
{
    public YandexArtist Artist { get; set; } = new();
    public string? Description { get; set; }
    public string? ArtistType { get; set; }
    public List<YandexAboutLink> Links { get; set; } = new();
    public List<YandexCover> Covers { get; set; } = new();
    public YandexArtistStats Stats { get; set; } = new();
}

public sealed class YandexArtistStats
{
    public long LastMonthListeners { get; set; }
    public long LastMonthListenersDelta { get; set; }
}

public sealed class YandexAboutLink
{
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public string? Url { get; set; }
    public string? ImgUrl { get; set; }
}

public sealed class YandexDirectAlbumsResponse { public YandexDirectAlbumsResult Result { get; set; } = new(); }

public sealed class YandexDirectAlbumsResult
{
    public YandexPager Pager { get; set; } = new();
    public List<YandexAlbum> Albums { get; set; } = new();
}

public sealed class YandexPager
{
    public int Page { get; set; }
    public int PerPage { get; set; }
    public int Total { get; set; }
}

public sealed class YandexAlbumResponse { public YandexAlbum Result { get; set; } = new(); }

public sealed class YandexArtist
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Composer { get; set; }
    public YandexCover? Cover { get; set; }
    public string? OgImage { get; set; }
    public List<string> Genres { get; set; } = new();
    public List<YandexLink> Links { get; set; } = new();
    public List<string> DbAliases { get; set; } = new();

    // Some audiobook credits are encoded as a mixed JSON array containing
    // separators (strings) and additional artist objects. Keep the raw array
    // so the metadata provider can flatten all real contributor objects.
    public JArray? Decomposed { get; set; }

    // Enriched from /artists/{id}/about-artist.
    public string? Description { get; set; }
    public string? ArtistType { get; set; }
}

public sealed class YandexAlbum
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Type { get; set; }
    public string? MetaType { get; set; }
    public int Year { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public string? CoverUri { get; set; }
    public string? OgImage { get; set; }
    public string? Genre { get; set; }
    public int TrackCount { get; set; }
    public int LikesCount { get; set; }

    // Audiobooks/podcasts expose these fields from /albums/{id}/with-tracks.
    // Unlike normal music releases, trackCount may remain zero even when
    // volumes contains the complete chapter/part list.
    public int DurationSec { get; set; }
    public string? ShortDescription { get; set; }
    public string? Description { get; set; }
    public YandexPager? Pager { get; set; }

    public List<YandexArtist> Artists { get; set; } = new();
    public List<YandexLabel> Labels { get; set; } = new();
    public List<List<YandexTrack>> Volumes { get; set; } = new();
}

public sealed class YandexTrack
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public int DurationMs { get; set; }
    public string? Type { get; set; }
    public string? ShortDescription { get; set; }
    public string? PodcastEpisodeType { get; set; }
    public DateTime? PubDate { get; set; }
    public List<YandexArtist> Artists { get; set; } = new();
    public List<YandexTrackAlbum> Albums { get; set; } = new();
}

public sealed class YandexTrackAlbum
{
    public long Id { get; set; }
    public YandexTrackPosition? TrackPosition { get; set; }
}

public sealed class YandexTrackPosition
{
    public int Volume { get; set; }
    public int Index { get; set; }
}

public sealed class YandexCover
{
    public string? Uri { get; set; }
    public string? Type { get; set; }
}

public sealed class YandexLink
{
    public string? Title { get; set; }
    public string? Href { get; set; }
    public string? Url { get; set; }
    public string? Subtitle { get; set; }
    public string? Type { get; set; }
    public string? SocialNetwork { get; set; }
}

public sealed class YandexLabel
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
