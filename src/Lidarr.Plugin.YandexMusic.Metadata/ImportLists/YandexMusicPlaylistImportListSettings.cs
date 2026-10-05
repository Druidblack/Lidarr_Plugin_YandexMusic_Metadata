using FluentValidation.Results;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Validation;

namespace Lidarr.Plugin.YandexMusicMetadata.ImportLists;

public sealed class YandexMusicPlaylistImportListSettings : IImportListSettings
{
    public YandexMusicPlaylistImportListSettings()
    {
        BaseUrl = "https://api.music.yandex.net";
        Playlist = string.Empty;
        ImportAllTrackArtists = true;
    }

    public string BaseUrl { get; set; }

    [FieldDefinition(0, Label = "URL или UUID плейлиста", Type = FieldType.Textbox, HelpText = "Ссылка на плейлист Яндекс Музыки (например https://music.yandex.ru/playlists/lk....), UUID плейлиста, старая ссылка /users/<owner>/playlists/<kind> или формат owner:kind.")]
    public string Playlist { get; set; }

    [FieldDefinition(1, Label = "Импортировать всех исполнителей из совместных треков", Type = FieldType.Checkbox, HelpText = "Если включено, из каждого трека плейлиста импортируются все указанные исполнители. Если отключено, импортируется только первый (основной) исполнитель каждого трека.")]
    public bool ImportAllTrackArtists { get; set; }

    public NzbDroneValidationResult Validate()
    {
        var failures = new List<ValidationFailure>();
        if (string.IsNullOrWhiteSpace(Playlist))
        {
            failures.Add(new ValidationFailure(nameof(Playlist), "Необходимо указать URL или UUID плейлиста."));
        }
        else if (!YandexMusicPlaylistReference.TryParse(Playlist, out _))
        {
            failures.Add(new ValidationFailure(nameof(Playlist), "Неподдерживаемая ссылка или идентификатор плейлиста Яндекс Музыки."));
        }

        return new NzbDroneValidationResult(failures);
    }
}
