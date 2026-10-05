using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using Lidarr.Plugin.YandexMusicMetadata.Api;
using Lidarr.Plugin.YandexMusicMetadata.Utility;

namespace Lidarr.Plugin.YandexMusicMetadata.ImportLists;

public sealed class YandexMusicPlaylistImportList : ImportListBase<YandexMusicPlaylistImportListSettings>
{
    private readonly YandexMusicClient _client;

    public YandexMusicPlaylistImportList(
        IHttpClient httpClient,
        IImportListStatusService importListStatusService,
        IConfigService configService,
        IParsingService parsingService,
        Logger logger)
        : base(importListStatusService, configService, parsingService, logger)
    {
        _client = new YandexMusicClient(httpClient, logger);
    }

    public override string Name => "Плейлист Yandex Music";
    public override ImportListType ListType => ImportListType.Other;
    public override TimeSpan MinRefreshInterval => TimeSpan.FromHours(1);

    public override IList<ImportListItemInfo> Fetch()
    {
        var playlist = FetchPlaylist();
        var artists = new Dictionary<long, YandexArtist>();

        foreach (var item in playlist.Tracks ?? new List<YandexPlaylistTrackItem>())
        {
            var trackArtists = item.Track?.Artists ?? new List<YandexArtist>();
            if (!Settings.ImportAllTrackArtists)
                trackArtists = trackArtists.Take(1).ToList();

            foreach (var artist in trackArtists)
            {
                if (artist.Id == 0 || string.IsNullOrWhiteSpace(artist.Name))
                    continue;

                artists.TryAdd(artist.Id, artist);
            }
        }

        var result = artists.Values
            .Select(artist => new ImportListItemInfo
            {
                Artist = artist.Name,
                // Lidarr's property keeps its historical MusicBrainz-oriented name,
                // but ImportListSyncService treats it as the generic artist ForeignArtistId.
                // Supplying our exact Yandex foreign ID avoids ambiguous name matching.
                ArtistMusicBrainzId = YandexIdParser.ArtistForeignId(artist.Id)
            })
            .ToList();

        _logger.Info(
            "Yandex Music playlist '{0}': tracks={1}, unique artists={2}, importAllTrackArtists={3}",
            playlist.Title,
            playlist.Tracks?.Count ?? 0,
            result.Count,
            Settings.ImportAllTrackArtists);

        return CleanupListItems(result);
    }

    protected override void Test(List<ValidationFailure> failures)
    {
        try
        {
            var playlist = FetchPlaylist();
            if (playlist.Tracks == null || playlist.Tracks.Count == 0)
            {
                failures.Add(new ValidationFailure(nameof(Settings.Playlist), "Плейлист найден, но не содержит треков."));
                return;
            }

            var artistCount = playlist.Tracks
                .Where(x => x.Track != null)
                .SelectMany(x => x.Track!.Artists ?? new List<YandexArtist>())
                .Where(x => x.Id != 0 && !string.IsNullOrWhiteSpace(x.Name))
                .Select(x => x.Id)
                .Distinct()
                .Count();

            if (artistCount == 0)
                failures.Add(new ValidationFailure(nameof(Settings.Playlist), "В плейлисте нет исполнителей Яндекс Музыки, которых можно импортировать."));
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "Unable to read Yandex Music playlist {0}", Settings.Playlist);
            failures.Add(new ValidationFailure(nameof(Settings.Playlist), "Не удалось прочитать плейлист Яндекс Музыки. Убедитесь, что плейлист публичный или доступен по ссылке, а URL или UUID указан правильно."));
        }
    }

    private YandexPlaylist FetchPlaylist()
    {
        if (!YandexMusicPlaylistReference.TryParse(Settings.Playlist, out var reference))
            throw new InvalidOperationException("Неподдерживаемая ссылка или идентификатор плейлиста Яндекс Музыки.");

        return reference.IsModern
            ? _client.GetPlaylist(reference.PlaylistUuid!)
            : _client.GetUserPlaylist(reference.Owner!, reference.Kind!);
    }
}
