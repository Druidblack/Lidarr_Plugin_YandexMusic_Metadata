using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NzbDrone.Core.MediaCover;

namespace Lidarr.Plugin.YandexMusicMetadata.Infrastructure;

/// <summary>
/// Wraps Lidarr's built-in media-cover proxy so Yandex Music images can be
/// rendered in artist lookup results. Yandex avatar URLs end in a size token
/// such as /1000x1000 or /orig, while Lidarr's MediaCoverProxyMapper only
/// serves proxy paths whose filename ends in .jpg/.png/.gif.
///
/// The built-in proxy still owns the URL cache and performs the HTTP download.
/// We only normalize the public proxy path for Yandex Music images by appending
/// .jpg after the cached hash/filename and removing the query string from the
/// public path. The original Yandex URL (including webp=false) remains stored
/// under the same hash, so GetImage() fetches the correct JPEG bytes.
/// </summary>
public sealed class YandexMusicMediaCoverProxy : IMediaCoverProxy
{
    private static readonly Regex SupportedImageExtension =
        new(@"\.(jpg|png|gif)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly MediaCoverProxy _lidarrDefault;

    public YandexMusicMediaCoverProxy(MediaCoverProxy lidarrDefault)
    {
        _lidarrDefault = lidarrDefault;
    }

    public string RegisterUrl(string url)
    {
        var registered = _lidarrDefault.RegisterUrl(url);
        if (string.IsNullOrWhiteSpace(registered) || !IsYandexMusicImage(url))
            return registered;

        // MediaCoverProxy.RegisterUrl() uses Path.GetFileName(url). For Yandex
        // this can become e.g. `1000x1000?webp=false&filename=image.jpg`. The
        // browser treats everything after '?' as a query, so Lidarr's mapper
        // sees only `1000x1000` and returns 404. Remove that query from the
        // public proxy URL and append a real .jpg path suffix.
        var queryIndex = registered.IndexOf('?');
        var publicPath = queryIndex >= 0 ? registered[..queryIndex] : registered;

        if (!SupportedImageExtension.IsMatch(publicPath))
            publicPath += ".jpg";

        return publicPath;
    }

    public string GetUrl(string hash) => _lidarrDefault.GetUrl(hash);

    public Task<byte[]> GetImage(string hash) => _lidarrDefault.GetImage(hash);

    private static bool IsYandexMusicImage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host;
        var isYandexAvatarHost =
            host.Equals("avatars.yandex.net", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("avatars.mds.yandex.net", StringComparison.OrdinalIgnoreCase);

        return isYandexAvatarHost &&
               uri.AbsolutePath.Contains("/get-music-content/", StringComparison.OrdinalIgnoreCase);
    }
}
