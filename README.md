<div align="center">

# Lidarr Plugin YandexMusic Metadata

Плагин для Lidarr который предоставляет метаданные исполнителей (альбомов) с Yandex Music. Одновременно работаем с Yandex Music, Deezer и MusicBrainz. Так же умеет импортировать плейлисты с Yandex Music и Deezer.

Плагин рекомендуется использовать совместно с [Yandex.Music indexer + download client](https://github.com/kitsunoff/yandex-music-lidarr). По желанию можно использовать [плагин для Deezer](https://github.com/TrevTV/Lidarr.Plugin.Deezer). 

При совместном использовании можно получить Lidarr который полностью работает с YandexMusic.

</div>

---

Ключевые особенности плагина:

1. Плагин умеет делать поиск по названию. Поиск идет в трех источниках Yandex Music, Deezer и MusicBrainz. При условии включенных Yandex Music и Deezer.

![search](https://github.com/Druidblack/Lidarr_Plugin_YandexMusic_Metadata/blob/main/img/Search.jpg)

![link](https://github.com/Druidblack/Lidarr_Plugin_YandexMusic_Metadata/blob/main/img/link.jpg)

![id](https://github.com/Druidblack/Lidarr_Plugin_YandexMusic_Metadata/blob/main/img/id.jpg)

P.S. Поставщики метаданных Deezer и MusicBrainz добавлены в плагин на случай отсутствия альбома у исполнителя в Yandex Music.

2. В настройках плагина можно гибко настроить получение необходимых данных и качества получаемых изображений.

![info](https://github.com/Druidblack/Lidarr_Plugin_YandexMusic_Metadata/blob/main/img/info.jpg)

![menu](https://github.com/Druidblack/Lidarr_Plugin_YandexMusic_Metadata/blob/main/img/menu.jpg)

3. Плагин умеет работать с книгами и подкастами.
   
   Добавить чтеца можно ссылкой (например https://music.yandex.ru/artist/17077082) и Lidarr получит все книги которые есть у чтеца.

![au](https://github.com/Druidblack/Lidarr_Plugin_YandexMusic_Metadata/blob/main/img/audiobook.jpg)

   Добавить подкаст можно ссылкой (например https://music.yandex.ru/album/6408449) и Lidarr создаст исполнителя с названием подкаста и внутри разобьет подкаст на выпуски (сезоны, части).
   **P.S. При включении опции "Облегчённые запросы альбомов" новые подкасты добавляться не будут.**
![pod](https://github.com/Druidblack/Lidarr_Plugin_YandexMusic_Metadata/blob/main/img/podcast.jpg)

4. Опция "Облегчённые запросы альбомов" в настройках позволяет сократить время повторного сканирования библиотеки с условно 3-х часов до 10 минут (проводил проверку на примере добавленных 180 исполнителей).
   
   Принцип работы: плагин при повторных обновлениях данных проверяет наличие Id альбома, если он есть в базе Lidarr то обновление данных самого альбома будет пропущено. По этой причине обновление подкастов не будет работать и если артист внес изменение в альбом, то мы об этом не узнаем.

5. Плагин умеет импортировать плейлисты Yandex Music и Deezer.

   ![pl](https://github.com/Druidblack/Lidarr_Plugin_YandexMusic_Metadata/blob/main/img/playlist.jpg)
