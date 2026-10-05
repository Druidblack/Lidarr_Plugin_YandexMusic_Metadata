using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;

namespace Lidarr.Plugin.YandexMusicMetadata.Metadata;

/// <summary>
/// Settings adapter for Lidarr's Settings -> Metadata page. Like the Yandex
/// adapter, it writes no local sidecar files; the definition is consumed by the
/// shared metadata proxy to enable/configure Deezer catalogue lookups.
/// </summary>
public sealed class DeezerMetadataConsumer : MetadataBase<DeezerMetadataSettings>
{
    public override string Name => "Deezer";

    public override MetadataFile FindMetadataFile(Artist artist, string path) => null!;
    public override MetadataFileResult ArtistMetadata(Artist artist) => null!;
    public override MetadataFileResult AlbumMetadata(Artist artist, Album album, string albumPath) => null!;
    public override MetadataFileResult TrackMetadata(Artist artist, TrackFile trackFile) => null!;
    public override List<ImageFileResult> ArtistImages(Artist artist) => new();
    public override List<ImageFileResult> AlbumImages(Artist artist, Album album, string albumPath) => new();
    public override List<ImageFileResult> TrackImages(Artist artist, TrackFile trackFile) => new();
}
