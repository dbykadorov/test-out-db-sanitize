using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Sanitize.Detection.Presidio;

public sealed record DetectedEntity
{
    [JsonPropertyName("entity_type")] public string EntityType { get; init; } = "";
    [JsonPropertyName("start")] public int Start { get; init; }
    [JsonPropertyName("end")] public int End { get; init; }
    [JsonPropertyName("score")] public double Score { get; init; }
}

/// <summary>
/// Разбор естественного языка через контейнер Presidio.
///
/// Взято готовым осознанно (C-1): русский NER - это модель spaCy и набор
/// распознавателей, а не наш код. Наше здесь только решение о том, что делать
/// с найденным, и оно принимается политикой, а не детектором.
/// </summary>
public sealed class PresidioDetector : IDisposable
{
    /// <summary>
    /// Порог, ниже которого находка считается шумом.
    ///
    /// Он влияет только на РАЗМЕТКУ колонки, а не на полноту замен: колонка
    /// со свободным текстом затирается целиком независимо от того, сколько
    /// вкраплений нашлось. Поэтому порог здесь - вопрос ложных срабатываний,
    /// а не пропущенных персональных данных.
    /// </summary>
    public const double DefaultScoreThreshold = 0.6;

    /// <summary>
    /// Сущности, которые сами по себе персональными данными не являются.
    /// Дата и организация в тексте встречаются постоянно и по ним колонку
    /// пришлось бы затирать всегда - то есть разметка перестала бы что-либо
    /// значить.
    /// </summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        "DATE_TIME", "ORGANIZATION", "NRP", "URL"
    };

    private readonly HttpClient _client;
    private readonly double _threshold;

    public PresidioDetector(string baseUrl, double threshold = DefaultScoreThreshold)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseUrl);

        _client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"),
            Timeout = TimeSpan.FromSeconds(120)
        };

        _threshold = threshold;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken token)
    {
        try
        {
            using var response = await _client.GetAsync("health", token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<DetectedEntity>> AnalyzeAsync(
        string text, string language, CancellationToken token)
    {
        var request = new { text, language };

        using var response = await _client
            .PostAsJsonAsync("analyze", request, token).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var found = await response.Content
            .ReadFromJsonAsync<List<DetectedEntity>>(token).ConfigureAwait(false)
            ?? new List<DetectedEntity>();

        var kept = new List<DetectedEntity>(found.Count);

        foreach (var entity in found)
            if (entity.Score >= _threshold && !Ignored.Contains(entity.EntityType))
                kept.Add(entity);

        return kept;
    }

    /// <summary>
    /// Доля значений выборки, в которых нашлись персональные данные.
    ///
    /// Это мера ЗАСОРЁННОСТИ колонки, а не полноты обнаружения. Полнота
    /// обнаружения в тексте принципиально не равна ста процентам, и подменять
    /// одно другим - ровно та ошибка, из-за которой публикуют текст
    /// с неизвестной долей пропусков.
    /// </summary>
    public async Task<double> HitShareAsync(
        IReadOnlyList<string> sample, string language, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(sample);

        if (sample.Count == 0) return 0;

        var hits = 0;

        foreach (var value in sample)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;

            var entities = await AnalyzeAsync(value, language, token).ConfigureAwait(false);
            if (entities.Count > 0) hits++;
        }

        return (double)hits / sample.Count;
    }

    public void Dispose() => _client.Dispose();
}
