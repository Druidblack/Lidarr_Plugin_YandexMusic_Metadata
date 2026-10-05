using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace Lidarr.Plugin.YandexMusicMetadata.Metadata;

public sealed class DeezerMetadataSettings : IProviderConfig
{
    public DeezerMetadataSettings()
    {
        IncludeAlbums = true;
        IncludeSingles = true;
        IncludeEPs = true;
        SearchArtistsByName = true;
        ArtistImages = true;
        AlbumCovers = true;
        CalculateAlbumRatings = false;
        Genres = true;
        Labels = true;
        ExternalLinks = true;
        PreferExactReleaseDate = true;
        LightweightAlbumRequests = false;
    }

    [FieldDefinition(0, Label = "Альбомы", Type = FieldType.Checkbox, HelpText = "Добавлять обычные альбомы Deezer в дискографию исполнителя.")]
    public bool IncludeAlbums { get; set; }

    [FieldDefinition(1, Label = "Синглы", Type = FieldType.Checkbox, HelpText = "Добавлять релизы Deezer с типом single.")]
    public bool IncludeSingles { get; set; }

    [FieldDefinition(2, Label = "EP", Type = FieldType.Checkbox, HelpText = "Добавлять релизы Deezer с типом ep.")]
    public bool IncludeEPs { get; set; }

    [FieldDefinition(3, Label = "Поиск исполнителей по имени", Type = FieldType.Checkbox, HelpText = "Добавлять результаты Deezer при поиске исполнителя по имени. Результаты Lidarr/MusicBrainz и других включённых источников сохраняются.")]
    public bool SearchArtistsByName { get; set; }

    [FieldDefinition(4, Label = "Изображения исполнителей", Type = FieldType.Checkbox, HelpText = "Использовать изображения исполнителей Deezer.")]
    public bool ArtistImages { get; set; }

    [FieldDefinition(5, Label = "Обложки альбомов", Type = FieldType.Checkbox, HelpText = "Использовать обложки альбомов Deezer.")]
    public bool AlbumCovers { get; set; }

    [FieldDefinition(6, Label = "Рассчитывать рейтинг альбомов по fans", Type = FieldType.Checkbox, HelpText = "Рассчитывать относительный рейтинг альбомов по полю fans Deezer: самый популярный релиз исполнителя получает 100%, остальные масштабируются пропорционально.")]
    public bool CalculateAlbumRatings { get; set; }

    [FieldDefinition(7, Label = "Жанры", Type = FieldType.Checkbox, HelpText = "Использовать жанры альбомов, возвращаемые Deezer.")]
    public bool Genres { get; set; }

    [FieldDefinition(8, Label = "Лейблы", Type = FieldType.Checkbox, HelpText = "Использовать лейблы из метаданных альбомов Deezer.")]
    public bool Labels { get; set; }

    [FieldDefinition(9, Label = "Внешние ссылки", Type = FieldType.Checkbox, HelpText = "Добавлять ссылки на страницы исполнителей и альбомов Deezer.")]
    public bool ExternalLinks { get; set; }

    [FieldDefinition(10, Label = "Точные даты релизов", Type = FieldType.Checkbox, HelpText = "Использовать точную дату релиза из Deezer. Если отключено, сохраняется только год.")]
    public bool PreferExactReleaseDate { get; set; }

    [FieldDefinition(11, Label = "Облегчённые запросы альбомов", Type = FieldType.Checkbox, HelpText = "Для уже успешно синхронизированных Deezer-альбомов повторно использовать сохранённый в Lidarr данные. Новые альбомы загружаются полностью.")]
    public bool LightweightAlbumRequests { get; set; }

    public NzbDroneValidationResult Validate() => new();
}
