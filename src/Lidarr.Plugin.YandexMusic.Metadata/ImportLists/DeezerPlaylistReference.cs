using System.Text.RegularExpressions;

namespace Lidarr.Plugin.YandexMusicMetadata.ImportLists;

internal sealed record DeezerPlaylistReference(long PlaylistId, string? ShareUrl = null)
{
    private static readonly Regex ForeignId = new(
        @"^(?:deezer|dz):playlist:(?<id>\d+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool RequiresResolution => PlaylistId <= 0 && !string.IsNullOrWhiteSpace(ShareUrl);

    public static bool TryParse(string? value, out DeezerPlaylistReference reference)
    {
        reference = null!;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var input = value.Trim();

        if (long.TryParse(input, out var rawId) && rawId > 0)
        {
            reference = new DeezerPlaylistReference(rawId);
            return true;
        }

        var foreign = ForeignId.Match(input);
        if (foreign.Success &&
            long.TryParse(foreign.Groups["id"].Value, out var foreignId) &&
            foreignId > 0)
        {
            reference = new DeezerPlaylistReference(foreignId);
            return true;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            !(uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
              uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (TryParsePlaylistUrl(uri, out var playlistId))
        {
            reference = new DeezerPlaylistReference(playlistId);
            return true;
        }

        // Deezer's Share action commonly returns one of these redirect URLs.
        // Keep the original URL and resolve it with Lidarr's own IHttpClient so
        // proxy, DNS and redirect behaviour remain consistent with the host.
        if (IsKnownShareHost(uri.Host))
        {
            reference = new DeezerPlaylistReference(0, input);
            return true;
        }

        return false;
    }

    private static bool TryParsePlaylistUrl(Uri uri, out long playlistId)
    {
        playlistId = 0;
        var host = uri.Host.TrimEnd('.');
        if (!host.Equals("deezer.com", StringComparison.OrdinalIgnoreCase) &&
            !host.EndsWith(".deezer.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (!segments[index].Equals("playlist", StringComparison.OrdinalIgnoreCase))
                continue;

            if (long.TryParse(segments[index + 1], out playlistId) && playlistId > 0)
                return true;
        }

        playlistId = 0;
        return false;
    }

    private static bool IsKnownShareHost(string host)
    {
        host = host.TrimEnd('.');
        return host.Equals("deezer.page.link", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("link.deezer.com", StringComparison.OrdinalIgnoreCase);
    }
}
