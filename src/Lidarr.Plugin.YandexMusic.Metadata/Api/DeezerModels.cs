using Newtonsoft.Json;

namespace Lidarr.Plugin.YandexMusicMetadata.Api;

public interface IDeezerErrorContainer
{
    DeezerError? Error { get; }
}

public sealed class DeezerError
{
    public string Type { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int Code { get; set; }
}

public sealed class DeezerArtistResult
{
    public DeezerArtist Artist { get; set; } = new();
    public List<DeezerAlbum> Albums { get; set; } = new();
}

public sealed class DeezerArtist : IDeezerErrorContainer
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Link { get; set; }
    public string? Share { get; set; }
    public string? Picture { get; set; }

    [JsonProperty("picture_small")]
    public string? PictureSmall { get; set; }

    [JsonProperty("picture_medium")]
    public string? PictureMedium { get; set; }

    [JsonProperty("picture_big")]
    public string? PictureBig { get; set; }

    [JsonProperty("picture_xl")]
    public string? PictureXl { get; set; }

    [JsonProperty("nb_album")]
    public int NbAlbum { get; set; }

    [JsonProperty("nb_fan")]
    public long NbFan { get; set; }

    public bool Radio { get; set; }
    public string? Tracklist { get; set; }
    public string? Type { get; set; }
    public DeezerError? Error { get; set; }
}

public sealed class DeezerAlbum : IDeezerErrorContainer
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Upc { get; set; }
    public string? Link { get; set; }
    public string? Share { get; set; }
    public string? Cover { get; set; }

    [JsonProperty("cover_small")]
    public string? CoverSmall { get; set; }

    [JsonProperty("cover_medium")]
    public string? CoverMedium { get; set; }

    [JsonProperty("cover_big")]
    public string? CoverBig { get; set; }

    [JsonProperty("cover_xl")]
    public string? CoverXl { get; set; }

    [JsonProperty("md5_image")]
    public string? Md5Image { get; set; }

    [JsonProperty("genre_id")]
    public long? GenreId { get; set; }

    public DeezerGenreList Genres { get; set; } = new();
    public string? Label { get; set; }

    [JsonProperty("nb_tracks")]
    public int? NbTracks { get; set; }

    public int? Duration { get; set; }
    public int? Fans { get; set; }

    [JsonProperty("release_date")]
    public string? ReleaseDate { get; set; }

    [JsonProperty("record_type")]
    public string? RecordType { get; set; }

    public bool? Available { get; set; }
    public string? Tracklist { get; set; }

    [JsonProperty("explicit_lyrics")]
    public bool? ExplicitLyrics { get; set; }

    public DeezerArtist Artist { get; set; } = new();
    public List<DeezerArtist> Contributors { get; set; } = new();
    public DeezerPage<DeezerTrack> Tracks { get; set; } = new();
    public string? Type { get; set; }
    public DeezerError? Error { get; set; }
}

public sealed class DeezerTrack : IDeezerErrorContainer
{
    public long Id { get; set; }
    public bool Readable { get; set; }
    public string Title { get; set; } = string.Empty;

    [JsonProperty("title_short")]
    public string? TitleShort { get; set; }

    [JsonProperty("title_version")]
    public string? TitleVersion { get; set; }

    public string? Link { get; set; }
    public int Duration { get; set; }
    public int Rank { get; set; }

    [JsonProperty("explicit_lyrics")]
    public bool ExplicitLyrics { get; set; }

    public string? Preview { get; set; }

    [JsonProperty("track_position")]
    public int TrackPosition { get; set; }

    [JsonProperty("disk_number")]
    public int DiskNumber { get; set; }

    public DeezerArtist Artist { get; set; } = new();
    public List<DeezerArtist> Contributors { get; set; } = new();
    public DeezerAlbum Album { get; set; } = new();
    public string? Type { get; set; }
    public DeezerError? Error { get; set; }
}

public sealed class DeezerPlaylist : IDeezerErrorContainer
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Duration { get; set; }
    public bool Public { get; set; }
    public bool Collaborative { get; set; }
    public int Fans { get; set; }
    public string? Link { get; set; }
    public string? Share { get; set; }
    public string? Picture { get; set; }

    [JsonProperty("picture_small")]
    public string? PictureSmall { get; set; }

    [JsonProperty("picture_medium")]
    public string? PictureMedium { get; set; }

    [JsonProperty("picture_big")]
    public string? PictureBig { get; set; }

    [JsonProperty("picture_xl")]
    public string? PictureXl { get; set; }

    [JsonProperty("nb_tracks")]
    public int NbTracks { get; set; }

    [JsonProperty("creation_date")]
    public string? CreationDate { get; set; }

    public DeezerUser Creator { get; set; } = new();
    public DeezerPage<DeezerTrack> Tracks { get; set; } = new();
    public string? Type { get; set; }
    public DeezerError? Error { get; set; }
}

public sealed class DeezerUser
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Tracklist { get; set; }
    public string? Type { get; set; }
}

public sealed class DeezerGenre
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Picture { get; set; }
    public string? Type { get; set; }
}

public sealed class DeezerGenreList
{
    public List<DeezerGenre> Data { get; set; } = new();
}

public sealed class DeezerPage<T> : IDeezerErrorContainer
{
    public List<T> Data { get; set; } = new();
    public int Total { get; set; }
    public string? Next { get; set; }
    public string? Prev { get; set; }
    public DeezerError? Error { get; set; }
}
