using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;

namespace Lidarr.Plugin.YandexMusicMetadata.Metadata;

/// <summary>
/// Settings adapter for Lidarr's Settings -> Metadata page.
/// This provider intentionally writes no sidecar/NFO files; its Definition and
/// settings are consumed by YandexMusicMetadataProvider as source configuration.
/// </summary>
public sealed class YandexMusicMetadataConsumer : MetadataBase<YandexMusicMetadataSettings>
{
    public override string Name => "Яндекс Музыка";

    public override MetadataFile FindMetadataFile(Artist artist, string path) => null!;

    public override MetadataFileResult ArtistMetadata(Artist artist) => null!;

    public override MetadataFileResult AlbumMetadata(Artist artist, Album album, string albumPath) => null!;

    public override MetadataFileResult TrackMetadata(Artist artist, TrackFile trackFile) => null!;

    public override List<ImageFileResult> ArtistImages(Artist artist) => new();

    public override List<ImageFileResult> AlbumImages(Artist artist, Album album, string albumPath) => new();

    public override List<ImageFileResult> TrackImages(Artist artist, TrackFile trackFile) => new();
}
