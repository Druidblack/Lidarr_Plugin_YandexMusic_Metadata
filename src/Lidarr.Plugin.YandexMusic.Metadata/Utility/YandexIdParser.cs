using System.Text.RegularExpressions;

namespace Lidarr.Plugin.YandexMusicMetadata.Utility;

internal static class YandexIdParser
{
    private static readonly Regex ArtistUrl = new(@"https?://(?:music\.)?yandex\.(?:ru|com|kz|by|uz)/artist/(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AlbumUrl = new(@"https?://(?:music\.)?yandex\.(?:ru|com|kz|by|uz)/album/(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string ArtistForeignId(long id) => $"yandex:artist:{id}";
    public static string PodcastArtistForeignId(long albumId) => $"yandex:podcast:{albumId}";
    public static string AlbumForeignId(long id, long? parentArtistId = null) => parentArtistId.HasValue ? $"yandex:album:{id}:artist:{parentArtistId.Value}" : $"yandex:album:{id}";
    public static string ReleaseForeignId(long id, long? parentArtistId = null) => parentArtistId.HasValue ? $"yandex:release:{id}:artist:{parentArtistId.Value}" : $"yandex:release:{id}";
    public static string TrackForeignId(long id, long? parentArtistId = null) => parentArtistId.HasValue ? $"yandex:track:{id}:artist:{parentArtistId.Value}" : $"yandex:track:{id}";
    public static string RecordingForeignId(long id, long? parentArtistId = null) => parentArtistId.HasValue ? $"yandex:recording:{id}:artist:{parentArtistId.Value}" : $"yandex:recording:{id}";


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
