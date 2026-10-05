using System.Text.RegularExpressions;

namespace Lidarr.Plugin.YandexMusicMetadata.ImportLists;

internal sealed record YandexMusicPlaylistReference(string? PlaylistUuid, string? Owner, string? Kind)
{
    private static readonly Regex ModernUrl = new(
        @"^https?://music\.yandex\.[^/]+/playlists/(?<id>[^/?#]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LegacyUrl = new(
        @"^https?://music\.yandex\.[^/]+/users/(?<owner>[^/?#]+)/playlists/(?<kind>[^/?#]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OwnerKind = new(
        @"^(?<owner>[^:\s]+):(?<kind>[^:\s]+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool IsModern => !string.IsNullOrWhiteSpace(PlaylistUuid);

    public static bool TryParse(string? value, out YandexMusicPlaylistReference reference)
    {
        reference = null!;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var input = value.Trim();

        var modern = ModernUrl.Match(input);
        if (modern.Success)
        {
            reference = new YandexMusicPlaylistReference(Uri.UnescapeDataString(modern.Groups["id"].Value), null, null);
            return true;
        }

        var legacy = LegacyUrl.Match(input);
        if (legacy.Success)
        {
            reference = new YandexMusicPlaylistReference(
                null,
                Uri.UnescapeDataString(legacy.Groups["owner"].Value),
                Uri.UnescapeDataString(legacy.Groups["kind"].Value));
            return true;
        }

        var ownerKind = OwnerKind.Match(input);
        if (ownerKind.Success)
        {
            reference = new YandexMusicPlaylistReference(
                null,
                ownerKind.Groups["owner"].Value,
                ownerKind.Groups["kind"].Value);
            return true;
        }

        // Raw playlist UUID/identifier, including Yandex forms such as lk.<uuid>.
        if (!input.Contains('/') && !input.Contains(' ') && input.Length >= 6)
        {
            reference = new YandexMusicPlaylistReference(input, null, null);
            return true;
        }

        return false;
    }
}
