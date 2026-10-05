namespace Lidarr.Plugin.YandexMusicMetadata.Api;

internal sealed class YandexMusicRateLimitException : InvalidOperationException
{
    public YandexMusicRateLimitException(string url, int attempts)
        : base($"Yandex Music HTTP error 429 after {attempts} attempts: {url}")
    {
        Url = url;
        Attempts = attempts;
    }

    public string Url { get; }
    public int Attempts { get; }
}
