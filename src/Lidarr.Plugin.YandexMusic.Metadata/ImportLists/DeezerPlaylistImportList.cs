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

public sealed class DeezerPlaylistImportList : ImportListBase<DeezerPlaylistImportListSettings>
{
    private readonly DeezerClient _client;

    public DeezerPlaylistImportList(
        IHttpClient httpClient,
        IImportListStatusService importListStatusService,
        IConfigService configService,
        IParsingService parsingService,
        Logger logger)
        : base(importListStatusService, configService, parsingService, logger)
    {
        _client = new DeezerClient(httpClient, logger);
    }

    public override string Name => "Плейлист Deezer";
    public override ImportListType ListType => ImportListType.Other;
    public override TimeSpan MinRefreshInterval => TimeSpan.FromHours(1);

    public override IList<ImportListItemInfo> Fetch()
    {
        var playlist = FetchPlaylist();
        var result = BuildArtistItems(playlist);

        _logger.Info(
            "Deezer playlist '{0}' ({1}): tracks={2}, returnedArtists={3}, importAllTrackArtists={4}",
            playlist.Title,
            playlist.Id,
            playlist.Tracks?.Data?.Count ?? 0,
            result.Count,
            Settings.ImportAllTrackArtists);

        return CleanupListItems(result);
    }

    protected override void Test(List<ValidationFailure> failures)
    {
        try
        {
            var playlist = FetchPlaylist();
            var tracks = playlist.Tracks?.Data ?? new List<DeezerTrack>();

            if (tracks.Count == 0)
            {
                failures.Add(new ValidationFailure(nameof(Settings.Playlist), "Плейлист найден, но не содержит доступных треков."));
                return;
            }

            var artistCount = tracks
                .SelectMany(GetTrackArtists)
                .Where(x => x.Id != 0 && !string.IsNullOrWhiteSpace(x.Name))
                .Select(x => x.Id)
                .Distinct()
                .Count();

            if (artistCount == 0)
                failures.Add(new ValidationFailure(nameof(Settings.Playlist), "В плейлисте нет исполнителей Deezer, которых можно импортировать."));
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "Unable to read Deezer playlist {0}", Settings.Playlist);
            failures.Add(new ValidationFailure(nameof(Settings.Playlist), "Не удалось прочитать плейлист Deezer. Убедитесь, что плейлист публичный и URL или ID указан правильно."));
        }
    }

    private List<ImportListItemInfo> BuildArtistItems(DeezerPlaylist playlist)
    {
        var artists = new Dictionary<long, DeezerArtist>();

        foreach (var track in playlist.Tracks?.Data ?? new List<DeezerTrack>())
        {
            var trackArtists = GetTrackArtists(track);
            if (!Settings.ImportAllTrackArtists)
                trackArtists = trackArtists.Take(1).ToList();

            foreach (var artist in trackArtists)
            {
                if (artist.Id == 0 || string.IsNullOrWhiteSpace(artist.Name))
                    continue;

                artists.TryAdd(artist.Id, artist);
            }
        }

        return artists.Values
            .Select(artist => new ImportListItemInfo
            {
                Artist = artist.Name,
                ArtistMusicBrainzId = DeezerIdParser.ArtistForeignId(artist.Id)
            })
            .ToList();
    }

    private static List<DeezerArtist> GetTrackArtists(DeezerTrack track)
    {
        var artists = new List<DeezerArtist>();
        if (track.Artist?.Id > 0)
            artists.Add(track.Artist);

        artists.AddRange(track.Contributors ?? new List<DeezerArtist>());

        return artists
            .Where(x => x.Id != 0 && !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .ToList();
    }

    private DeezerPlaylist FetchPlaylist()
    {
        if (!DeezerPlaylistReference.TryParse(Settings.Playlist, out var reference))
            throw new InvalidOperationException("Неподдерживаемая ссылка или идентификатор плейлиста Deezer.");

        if (!reference.RequiresResolution)
            return _client.GetPlaylist(reference.PlaylistId);

        var resolvedUrl = _client.ResolveShareUrl(reference.ShareUrl!);
        if (!DeezerPlaylistReference.TryParse(resolvedUrl, out var resolvedReference) ||
            resolvedReference.RequiresResolution ||
            resolvedReference.PlaylistId <= 0)
        {
            throw new InvalidOperationException($"Короткая ссылка Deezer не привела к URL плейлиста: {resolvedUrl}");
        }

        return _client.GetPlaylist(resolvedReference.PlaylistId);
    }
}
