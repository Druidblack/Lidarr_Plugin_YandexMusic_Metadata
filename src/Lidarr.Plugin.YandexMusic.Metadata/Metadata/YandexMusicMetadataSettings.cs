using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace Lidarr.Plugin.YandexMusicMetadata.Metadata;

public sealed class YandexMusicMetadataSettings : IProviderConfig
{
    public YandexMusicMetadataSettings()
    {
        IncludeAlbums = true;
        IncludeSingles = true;
        IncludeEPs = true;
        IncludeAlsoAlbums = false;
        SearchArtistsByName = true;
        ArtistImages = true;
        ArtistImagesOriginalQuality = false;
        RandomArtistImage = false;
        AlbumCovers = true;
        AlbumCoversOriginalQuality = false;
        CalculateAlbumRatings = false;
        Genres = true;
        Labels = true;
        ExternalLinks = true;
        PreferExactReleaseDate = true;
        LightweightAlbumRequests = false;
    }

    [FieldDefinition(0, Label = "Альбомы", Type = FieldType.Checkbox, HelpText = "Добавлять обычные альбомы из Яндекс Музыки в дискографию исполнителя.")]
    public bool IncludeAlbums { get; set; }

    [FieldDefinition(1, Label = "Синглы", Type = FieldType.Checkbox, HelpText = "Добавлять релизы, отмеченные как синглы.")]
    public bool IncludeSingles { get; set; }

    [FieldDefinition(2, Label = "EP", Type = FieldType.Checkbox, HelpText = "Добавлять релизы, отмеченные как EP.")]
    public bool IncludeEPs { get; set; }

    [FieldDefinition(3, Label = "Альбомы с участием исполнителя", Type = FieldType.Checkbox, HelpText = "Добавлять совместные релизы, возвращаемые Яндекс Музыкой. По умолчанию отключено, чтобы не заполнять медиатеку посторонними релизами.")]
    public bool IncludeAlsoAlbums { get; set; }

    [FieldDefinition(4, Label = "Поиск исполнителей по имени", Type = FieldType.Checkbox, HelpText = "Добавлять результаты Яндекс Музыки при поиске по имени исполнителя. Стандартные результаты Lidarr/MusicBrainz сохраняются и объединяются с результатами Яндекс Музыки.")]
    public bool SearchArtistsByName { get; set; }

    [FieldDefinition(5, Label = "Изображения исполнителей", Type = FieldType.Checkbox, HelpText = "Использовать изображения исполнителей из Яндекс Музыки.")]
    public bool ArtistImages { get; set; }

    [FieldDefinition(6, Label = "Изображения исполнителей в оригинальном качестве", Type = FieldType.Checkbox, HelpText = "Загружать изображения исполнителей в оригинальном размере Яндекса вместо 1000x1000.")]
    public bool ArtistImagesOriginalQuality { get; set; }

    [FieldDefinition(7, Label = "Случайное изображение исполнителя", Type = FieldType.Checkbox, HelpText = "Если доступно несколько изображений исполнителя, при обновлении метаданных выбирать случайное. Если отключено, используется первое изображение из списка Яндекс Музыки.")]
    public bool RandomArtistImage { get; set; }

    [FieldDefinition(8, Label = "Обложки альбомов", Type = FieldType.Checkbox, HelpText = "Использовать обложки альбомов из Яндекс Музыки.")]
    public bool AlbumCovers { get; set; }

    [FieldDefinition(9, Label = "Обложки альбомов в оригинальном качестве", Type = FieldType.Checkbox, HelpText = "Загружать обложки альбомов в оригинальном размере Яндекса вместо 1000x1000.")]
    public bool AlbumCoversOriginalQuality { get; set; }

    [FieldDefinition(10, Label = "Рассчитывать рейтинг альбомов по лайкам", Type = FieldType.Checkbox, HelpText = "Рассчитывать рейтинг альбомов и синглов Яндекс Музыки. Релиз с максимальным числом лайков получает 100%, остальные масштабируются пропорционально. Лайки сохраняется как количество голосов рейтинга.")]
    public bool CalculateAlbumRatings { get; set; }

    [FieldDefinition(11, Label = "Жанры", Type = FieldType.Checkbox, HelpText = "Использовать жанры исполнителей и альбомов из Яндекс Музыки.")]
    public bool Genres { get; set; }

    [FieldDefinition(12, Label = "Лейблы", Type = FieldType.Checkbox, HelpText = "Использовать лейблы релизов из Яндекс Музыки.")]
    public bool Labels { get; set; }

    [FieldDefinition(13, Label = "Внешние ссылки", Type = FieldType.Checkbox, HelpText = "Добавлять в метаданные исполнителей и альбомов ссылки на Яндекс Музыку, социальные сети и официальные сайты.")]
    public bool ExternalLinks { get; set; }

    [FieldDefinition(14, Label = "Точные даты релизов", Type = FieldType.Checkbox, HelpText = "Использовать точную дату релиза из Яндекс Музыки. Если отключено, используется только год выпуска.")]
    public bool PreferExactReleaseDate { get; set; }

    [FieldDefinition(15, Label = "Облегчённые запросы альбомов", Type = FieldType.Checkbox, HelpText = "Уменьшает количество запросов к API Яндекс Музыки при обновлении исполнителя. Для альбомов, которые уже успешно синхронизировались в Lidarr, используются сохранённые локальные данные без повторного запроса информации об альбомах. Новые или повторно добавленные альбомы по-прежнему загружаются полностью. Чтобы принудительно обновить данные альбома при включённой опции, удалите альбом и обновите исполнителя, чтобы Lidarr добавил его заново. Включение этой опции значительно сокращает время повторного сканирования библиотеки. Включенная опция не позволит обновлять подкасты.")]
    public bool LightweightAlbumRequests { get; set; }

    public NzbDroneValidationResult Validate() => new();
}
