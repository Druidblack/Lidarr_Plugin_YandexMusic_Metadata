using System.Text.RegularExpressions;

namespace Lidarr.Plugin.YandexMusicMetadata.Utility;

internal static class DeezerIdParser
{
    private static readonly Regex ArtistUrl = new(
        @"https?://(?:www\.)?deezer\.com/(?:[a-z]{2}/)?artist/(?<id>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AlbumUrl = new(
        @"https?://(?:www\.)?deezer\.com/(?:[a-z]{2}/)?album/(?<id>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string ArtistForeignId(long id) => $"deezer:artist:{id}";
    public static string AlbumForeignId(long id, long? parentArtistId = null) =>
        parentArtistId.HasValue ? $"deezer:album:{id}:artist:{parentArtistId.Value}" : $"deezer:album:{id}";
    public static string ReleaseForeignId(long id, long? parentArtistId = null) =>
        parentArtistId.HasValue ? $"deezer:release:{id}:artist:{parentArtistId.Value}" : $"deezer:release:{id}";
    public static string TrackForeignId(long id, long? parentArtistId = null) =>
        parentArtistId.HasValue ? $"deezer:track:{id}:artist:{parentArtistId.Value}" : $"deezer:track:{id}";
    public static string RecordingForeignId(long id, long? parentArtistId = null) =>
        parentArtistId.HasValue ? $"deezer:recording:{id}:artist:{parentArtistId.Value}" : $"deezer:recording:{id}";

    public static bool TryArtistId(string? value, out long id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();

        var match = ArtistUrl.Match(value);
        if (match.Success)
            return long.TryParse(match.Groups["id"].Value, out id);

        foreach (var prefix in new[] { "deezer:artist:", "dz:artist:", "deezer:", "dz:" })
        {
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = value[prefix.Length..].Trim();
            var first = rest.Split(':', 2)[0];
            return long.TryParse(first, out id);
        }

        // Deliberately do not treat a bare number as Deezer. Existing versions
        // use bare numeric IDs for Yandex Music; preserving that behaviour avoids
        // changing old search workflows. The provider may opt into bare Deezer
        // IDs only when Yandex metadata is disabled.
        return false;
    }

    public static bool TryAlbumId(string? value, out long id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();

        var match = AlbumUrl.Match(value);
        if (match.Success)
            return long.TryParse(match.Groups["id"].Value, out id);

        foreach (var prefix in new[] { "deezer:album:", "dz:album:", "deezer-album:", "dz-album:" })
        {
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = value[prefix.Length..].Trim();
            var first = rest.Split(':', 2)[0];
            return long.TryParse(first, out id);
        }

        return false;
    }

    public static bool TryAlbumParentArtistId(string? value, out long artistId)
    {
        artistId = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var match = Regex.Match(value, @"^deezer:album:\d+:artist:(?<id>\d+)$", RegexOptions.IgnoreCase);
        return match.Success && long.TryParse(match.Groups["id"].Value, out artistId);
    }

    public static bool IsArtistReference(string? value) => TryArtistId(value, out _);
    public static bool IsAlbumReference(string? value) => TryAlbumId(value, out _);
}
