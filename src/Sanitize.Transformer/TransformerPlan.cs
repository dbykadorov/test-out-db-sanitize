using System.Text.Json;
using System.Text.Json.Serialization;
using Sanitize.Core.Policy;
using Sanitize.Core.Values;

namespace Sanitize.Transformer;

public sealed record ColumnPlan
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("mode")]
    public string Mode { get; init; } = nameof(ColumnMode.Typed);

    /// <summary>
    /// Чем гарантируется замена. Трансформеру это нужно ровно для одного:
    /// у колонки с беспорядком в конечном домене генератора нет и быть
    /// не должно - все значения приходят из словаря прогона.
    /// </summary>
    [JsonPropertyName("strategy")]
    public string Strategy { get; init; } = nameof(ReplacementStrategy.Function);

    /// <summary>Разметка путей документа: путь - семантический тип.</summary>
    [JsonPropertyName("documentPaths")]
    public IReadOnlyDictionary<string, string> DocumentPaths { get; init; } =
        new Dictionary<string, string>();

    // Разобранные виды помечены как несериализуемые намеренно: без этого
    // сериализатор вызывает их при записи плана, и опечатка в справочнике
    // артефакта роняет прогон на записи файла, а не на проверке политики.
    [JsonIgnore]
    public CanonicalKind CanonicalKind => Enum.Parse<CanonicalKind>(Kind, ignoreCase: true);

    [JsonIgnore]
    public SemanticTypeId SemanticType => SemanticTypeId.Of(Type);

    [JsonIgnore]
    public ColumnMode ColumnMode => Enum.Parse<ColumnMode>(Mode, ignoreCase: true);

    [JsonIgnore]
    public ReplacementStrategy ReplacementStrategy =>
        Enum.Parse<ReplacementStrategy>(Strategy, ignoreCase: true);
}

/// <summary>
/// План трансформера: что делать с каждой колонкой и откуда брать словарь
/// и секрет.
///
/// План порождается воркером из утверждённой политики, а не пишется руками:
/// решения живут в одном месте (E-2). Секрет здесь задан ПУТЁМ к файлу,
/// а не значением: аргументы процесса видны в списке процессов, а файл
/// конфигурации переживает прогон.
/// </summary>
public sealed record TransformerPlan
{
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    [JsonPropertyName("dictionaryPath")]
    public required string DictionaryPath { get; init; }

    [JsonPropertyName("secretPath")]
    public required string SecretPath { get; init; }

    [JsonPropertyName("columns")]
    public required IReadOnlyList<ColumnPlan> Columns { get; init; }

    /// <summary>Отпечатки значений, утверждённых как исключения Р-9.</summary>
    [JsonPropertyName("exceptionFingerprints")]
    public IReadOnlyList<string> ExceptionFingerprints { get; init; } = Array.Empty<string>();

    /// <summary>Канонический тип для значений, встреченных в нескольких доменах.</summary>
    [JsonPropertyName("crossDomainTypes")]
    public IReadOnlyDictionary<string, string> CrossDomainTypes { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Словари компонентов, порождённые моделью.</summary>
    [JsonPropertyName("components")]
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Components { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Шаблоны сборки по семантическим типам.</summary>
    [JsonPropertyName("templates")]
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Templates { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Отпечаток артефакта модели - для манифеста происхождения значений (F-6).</summary>
    [JsonPropertyName("artifactFingerprint")]
    public string ArtifactFingerprint { get; init; } = "не задан";

    /// <summary>Число обработчиков в пуле. Ноль означает «по числу ядер».</summary>
    [JsonPropertyName("workers")]
    public int Workers { get; init; }

    public static TransformerPlan Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var json = File.ReadAllText(path);
        var plan = JsonSerializer.Deserialize<TransformerPlan>(json)
                   ?? throw new InvalidDataException($"Пустой план трансформера: {path}");

        if (plan.Columns.Count == 0)
            throw new InvalidDataException("План без колонок: трансформеру нечего делать");

        return plan;
    }
}
