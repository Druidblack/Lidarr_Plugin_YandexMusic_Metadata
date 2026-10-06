using Lidarr.Plugin.YandexMusicMetadata.Api;
using NLog;

namespace Lidarr.Plugin.YandexMusicMetadata.Utility;

/// <summary>
/// Converts Yandex Music genre identifiers/titles to the official Russian
/// labels returned by /genres. The remote catalog is recursive and includes
/// subGenres, so new Yandex genres can be picked up without a plugin update.
/// </summary>
internal static class YandexGenreTranslator
{
    private static readonly object Sync = new();
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(15);
    private static DateTime _nextLoadAttemptUtc = DateTime.MinValue;
    private static bool _remoteCatalogLoaded;

    private static Dictionary<string, string> _translations = CreateFallbackTranslations();

    public static void EnsureLoaded(YandexMusicClient client, Logger logger)
    {
        lock (Sync)
        {
            if (_remoteCatalogLoaded || DateTime.UtcNow < _nextLoadAttemptUtc)
                return;

            try
            {
                var remote = client.GetRussianGenreTranslations();
                if (remote.Count > 0)
                {
                    // Keep fallback aliases as well. Remote values win because they
                    // are the current official Russian labels from Yandex Music.
                    var merged = CreateFallbackTranslations();
                    foreach (var pair in remote)
                        merged[pair.Key] = pair.Value;

                    _translations = merged;
                    _remoteCatalogLoaded = true;
                    logger.Debug("Yandex Music genre catalog loaded: {0} Russian genre aliases", merged.Count);
                }
                else
                {
                    _nextLoadAttemptUtc = DateTime.UtcNow + RetryInterval;
                    logger.Warn("Yandex Music /genres returned an empty catalog; using built-in Russian genre fallback");
                }
            }
            catch (Exception ex)
            {
                _nextLoadAttemptUtc = DateTime.UtcNow + RetryInterval;
                logger.Warn(ex,
                    "Yandex Music genre catalog lookup failed; using built-in Russian genre fallback and retrying later");
            }
        }
    }

    public static string Translate(string? genre)
    {
        if (string.IsNullOrWhiteSpace(genre))
            return string.Empty;

        var value = genre.Trim();
        lock (Sync)
        {
            return _translations.TryGetValue(value, out var translated) && !string.IsNullOrWhiteSpace(translated)
                ? translated
                : value;
        }
    }

    public static List<string> Translate(IEnumerable<string>? genres)
    {
        if (genres == null)
            return new List<string>();

        return genres
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(Translate)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<string, string> CreateFallbackTranslations()
    {
        // This dictionary is deliberately a fallback, not the source of truth.
        // /genres supplies the complete live catalog, including subgenres. These
        // values cover common IDs plus non-music content seen in album responses
        // when /genres is temporarily unavailable.
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["all"] = "Все жанры",
            ["pop"] = "Поп",
            ["ruspop"] = "Русская поп-музыка",
            ["foreignpop"] = "Зарубежная поп-музыка",
            ["rock"] = "Рок",
            ["rusrock"] = "Русский рок",
            ["foreignrock"] = "Зарубежный рок",
            ["metal"] = "Метал",
            ["alternative"] = "Альтернатива",
            ["punk"] = "Панк",
            ["indie"] = "Инди",
            ["electronic"] = "Электронная музыка",
            ["dance"] = "Танцевальная музыка",
            ["house"] = "Хаус",
            ["techno"] = "Техно",
            ["trance"] = "Транс",
            ["drumandbass"] = "Драм-н-бэйс",
            ["dubstep"] = "Дабстеп",
            ["ambient"] = "Эмбиент",
            ["experimental"] = "Экспериментальная музыка",
            ["hiphop"] = "Хип-хоп",
            ["rap"] = "Рэп",
            ["rusrap"] = "Русский рэп",
            ["foreignrap"] = "Зарубежный рэп",
            ["rnb"] = "R&B",
            ["soul"] = "Соул",
            ["funk"] = "Фанк",
            ["jazz"] = "Джаз",
            ["blues"] = "Блюз",
            ["reggae"] = "Регги",
            ["ska"] = "Ска",
            ["country"] = "Кантри",
            ["folk"] = "Фолк",
            ["world"] = "Музыка мира",
            ["latinfolk"] = "Латиноамериканская музыка",
            ["latin"] = "Латино",
            ["classical"] = "Классическая музыка",
            ["opera"] = "Опера",
            ["estrada"] = "Эстрада",
            ["shanson"] = "Шансон",
            ["bard"] = "Авторская песня",
            ["children"] = "Детская музыка",
            ["kids"] = "Детская музыка",
            ["relax"] = "Релакс",
            ["meditation"] = "Медитация",
            ["newage"] = "Нью-эйдж",
            ["soundtrack"] = "Саундтреки",
            ["films"] = "Музыка из фильмов",
            ["videogame"] = "Музыка из видеоигр",
            ["musical"] = "Мюзиклы",
            ["holiday"] = "Праздничная музыка",
            ["christmas"] = "Новогодняя и рождественская музыка",
            ["religious"] = "Духовная музыка",
            ["podcasts"] = "Подкасты",
            ["podcast"] = "Подкасты",
            ["fiction"] = "Художественная литература",
            ["fantasyliterature"] = "Фэнтези",
            ["horrorandthrillers"] = "Ужасы и триллеры",
            ["detectives"] = "Детективы",
            ["sciencefiction"] = "Научная фантастика",
            ["romance"] = "Любовные романы",
            ["adventure"] = "Приключения",
            ["historical"] = "Историческая литература",
            ["modernprose"] = "Современная проза",
            ["classicliterature"] = "Классическая литература",
            ["poetry"] = "Поэзия",
            ["fairytales"] = "Сказки",
            ["childrensliterature"] = "Детская литература",
            ["nonfiction"] = "Нон-фикшн",
            ["biographies"] = "Биографии и мемуары",
            ["business"] = "Бизнес",
            ["psychology"] = "Психология",
            ["psychologyandphilosophy"] = "Психология и философия",
            ["community"] = "Общество",
            ["selfdevelopment"] = "Саморазвитие",
            ["history"] = "История",
            ["science"] = "Наука",
            ["education"] = "Образование",
            ["health"] = "Здоровье",
            ["esoterics"] = "Эзотерика",
            ["humor"] = "Юмор",
            ["comedy"] = "Юмор"
        };
    }
}
