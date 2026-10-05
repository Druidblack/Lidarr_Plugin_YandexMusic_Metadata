using FluentValidation.Results;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Validation;

namespace Lidarr.Plugin.YandexMusicMetadata.ImportLists;

public sealed class DeezerPlaylistImportListSettings : IImportListSettings
{
    public DeezerPlaylistImportListSettings()
    {
        BaseUrl = "https://api.deezer.com";
        Playlist = string.Empty;
        ImportAllTrackArtists = true;
    }

    public string BaseUrl { get; set; }

    [FieldDefinition(0, Label = "URL или ID плейлиста", Type = FieldType.Textbox, HelpText = "Ссылка Deezer вида https://www.deezer.com/playlist/<id> (локализованные ссылки /en/playlist/<id> тоже поддерживаются), deezer:playlist:<id> или числовой ID. Также поддерживаются короткие share-ссылки deezer.page.link/... и link.deezer.com/s/... — плагин автоматически разрешит редирект до конечного URL плейлиста.")]
    public string Playlist { get; set; }

    [FieldDefinition(1, Label = "Импортировать всех исполнителей из совместных треков", Type = FieldType.Checkbox, HelpText = "Если включено, из каждого трека плейлиста импортируются все указанные исполнители. Если отключено, импортируется только первый (основной) исполнитель каждого трека.")]
    public bool ImportAllTrackArtists { get; set; }

    public NzbDroneValidationResult Validate()
    {
        var failures = new List<ValidationFailure>();
        if (string.IsNullOrWhiteSpace(Playlist))
        {
            failures.Add(new ValidationFailure(nameof(Playlist), "Необходимо указать URL или ID плейлиста Deezer."));
        }
        else if (!DeezerPlaylistReference.TryParse(Playlist, out _))
        {
            failures.Add(new ValidationFailure(nameof(Playlist), "Неподдерживаемая ссылка или идентификатор плейлиста Deezer."));
        }

        return new NzbDroneValidationResult(failures);
    }
}
