using System.Text.RegularExpressions;

namespace Lidarr.Plugin.YandexMusicMetadata.Utility;

internal static class YandexIdParser
{
    private static readonly Regex ArtistUrl = new(@"https?://(?:music\.)?yandex\.(?:ru|com|kz|by|uz)/artist/(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AlbumUrl = new(@"https?://(?:music\.)?yandex\.(?:ru|com|kz|by|uz)/album/(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string ArtistForeignId(long id) => $"yandex:artist:{id}";
    public static string PodcastArtistForeignId(long albumId) => $"yandex:podcast:{albumId}";
    public static string AlbumForeignId(long id, long? parentArtistId = null) => parentArtistId.HasValue ? $"yandex:album:{id}:artist:{parentArtistId.Value}" : $"yandex:album:{id}";

    // v0.2.9+: releases use a compact v2 namespace. Parent-scoped releases are
    // intentionally distinct for the same Yandex album when it is exposed under
    // several Lidarr artists (common for multi-reader audiobooks).
    public static string ReleaseForeignId(long albumId, long? parentArtistId = null) =>
        parentArtistId.HasValue ? $"yandex:r:{albumId}:a:{parentArtistId.Value}" : $"yandex:r:{albumId}";

    // Tracks must be album-scoped as well as artist-scoped. Yandex can reuse a
    // track/episode id in duplicate/reissue/participation album graphs while Lidarr
    // enforces Tracks.ForeignTrackId globally across the whole database.
    public static string TrackForeignId(long trackId, long albumId, long? parentArtistId = null) =>
        parentArtistId.HasValue
            ? $"yandex:t:{trackId}:al:{albumId}:a:{parentArtistId.Value}"
            : $"yandex:t:{trackId}:al:{albumId}";

    public static string RecordingForeignId(long trackId, long albumId, long? parentArtistId = null) =>
        parentArtistId.HasValue
            ? $"yandex:rec:{trackId}:al:{albumId}:a:{parentArtistId.Value}"
            : $"yandex:rec:{trackId}:al:{albumId}";

    // Safe migration aliases from v0.2.8. For parent-scoped content never expose
    // the older bare id as an alias: it is shared by every narrator copy and makes
    // Lidarr pull children from another artist during Refresh*Service matching.
    public static string LegacyReleaseForeignId(long albumId, long? parentArtistId = null) =>
        parentArtistId.HasValue ? $"yandex:release:{albumId}:artist:{parentArtistId.Value}" : $"yandex:release:{albumId}";

    public static string LegacyTrackForeignId(long trackId, long? parentArtistId = null) =>
        parentArtistId.HasValue ? $"yandex:track:{trackId}:artist:{parentArtistId.Value}" : $"yandex:track:{trackId}";

    public static string LegacyRecordingForeignId(long trackId, long? parentArtistId = null) =>
        parentArtistId.HasValue ? $"yandex:recording:{trackId}:artist:{parentArtistId.Value}" : $"yandex:recording:{trackId}";

    public static bool IsCurrentReleaseForeignId(string? value, long albumId, long? parentArtistId) =>
        string.Equals(value, ReleaseForeignId(albumId, parentArtistId), StringComparison.OrdinalIgnoreCase);

    public static bool IsCurrentTrackForeignId(string? value, long albumId, long? parentArtistId)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var albumToken = $":al:{albumId}";
        if (!value.StartsWith("yandex:t:", StringComparison.OrdinalIgnoreCase) ||
            !value.Contains(albumToken, StringComparison.OrdinalIgnoreCase))
            return false;

        var artistToken = parentArtistId.HasValue ? $":a:{parentArtistId.Value}" : string.Empty;
        return parentArtistId.HasValue
            ? value.EndsWith(artistToken, StringComparison.OrdinalIgnoreCase)
            : !Regex.IsMatch(value, @":a:\d+$", RegexOptions.IgnoreCase);
    }

    public static bool TryPodcastArtistId(string? value, out long albumId)
    {
        albumId = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();
        const string prefix = "yandex:podcast:";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               long.TryParse(value[prefix.Length..].Trim(), out albumId);
    }

    public static bool TryArtistId(string? value, out long id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();
        var m = ArtistUrl.Match(value);
        if (m.Success) return long.TryParse(m.Groups["id"].Value, out id);
        foreach (var prefix in new[] { "yandex:artist:", "ym:artist:", "ym:", "yandex:" })
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return long.TryParse(value[prefix.Length..].Trim(), out id);
        return long.TryParse(value, out id);
    }

    public static bool TryAlbumId(string? value, out long id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();
        var m = AlbumUrl.Match(value);
        if (m.Success) return long.TryParse(m.Groups["id"].Value, out id);
        foreach (var prefix in new[] { "yandex:album:", "ym:album:", "ym-album:", "yandex-album:" })
        {
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = value[prefix.Length..].Trim();
            var first = rest.Split(':', 2)[0];
            return long.TryParse(first, out id);
        }
        return long.TryParse(value, out id);
    }

    public static bool TryAlbumParentArtistId(string? value, out long artistId)
    {
        artistId = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var match = Regex.Match(value, @"^yandex:album:\d+:artist:(?<id>\d+)$", RegexOptions.IgnoreCase);
        return match.Success && long.TryParse(match.Groups["id"].Value, out artistId);
    }
}
