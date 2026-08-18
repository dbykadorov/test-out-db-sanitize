using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Sanitize.Core.Policy;

namespace Sanitize.Core.Classification;

/// <summary>
/// Правило распознавания одного семантического типа.
///
/// Правила приходят артефактом модели, а не зашиты в код: E-1 требует, чтобы
/// новый семантический тип не требовал правки ядра. Здесь только их применение.
/// </summary>
public sealed record ClassificationRule
{
    [JsonPropertyName("semanticType")] public required string SemanticType { get; init; }

    /// <summary>Меньше ранг - специфичнее тип (F-7).</summary>
    [JsonPropertyName("rank")] public required int Rank { get; init; }

    [JsonPropertyName("canonicalKind")] public string CanonicalKind { get; init; } = "Text";

    [JsonPropertyName("mode")] public ColumnMode Mode { get; init; } = ColumnMode.Typed;

    /// <summary>Выражения по имени колонки.</summary>
    [JsonPropertyName("namePatterns")]
    public IReadOnlyList<string> NamePatterns { get; init; } = Array.Empty<string>();

    /// <summary>Выражения по комментарию к объекту схемы.</summary>
    [JsonPropertyName("commentPatterns")]
    public IReadOnlyList<string> CommentPatterns { get; init; } = Array.Empty<string>();

    /// <summary>Выражение, которому обязано соответствовать значение.</summary>
    [JsonPropertyName("valuePattern")] public string ValuePattern { get; init; } = "";

    /// <summary>
    /// Имя детерминированной проверки значения: контрольная сумма, если она
    /// у типа определена. Сама проверка живёт в коде - это форма, а не содержание.
    /// </summary>
    [JsonPropertyName("checksum")] public string Checksum { get; init; } = "";

    /// <summary>Типы источника, на которых правило вообще имеет смысл.</summary>
    [JsonPropertyName("dataTypes")]
    public IReadOnlyList<string> DataTypes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Артефакт модели: правила распознавания, словари компонентов и шаблоны сборки.
///
/// Он версионируется и хранится целиком. Без этого повторный прогон с тем же
/// секретом дал бы другие значения - модель недетерминирована, - и Р-6
/// не выполнилось бы.
/// </summary>
public sealed record ModelArtifact
{
    [JsonPropertyName("version")] public required string Version { get; init; }

    [JsonPropertyName("rules")] public required IReadOnlyList<ClassificationRule> Rules { get; init; }

    [JsonPropertyName("components")]
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Components { get; init; }

    [JsonPropertyName("templates")]
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Templates { get; init; }

    /// <summary>
    /// Отпечаток содержимого артефакта. Он попадает в паспорт выгрузки: доля
    /// значений, происходящих от модели (F-6), считается точно, а не оценивается.
    /// </summary>
    [JsonIgnore] public string Fingerprint { get; init; } = "";

    public static ModelArtifact FromJson(string json, string fingerprint)
    {
        var artifact = JsonSerializer.Deserialize<ModelArtifact>(json, RunPolicy.JsonOptions)
                       ?? throw new InvalidDataException("Пустой артефакт модели");

        if (artifact.Rules.Count == 0)
            throw new InvalidDataException("Артефакт модели без правил распознавания");

        if (artifact.Components.Count == 0)
            throw new InvalidDataException("Артефакт модели без словарей компонентов");

        return artifact with { Fingerprint = fingerprint };
    }

    /// <summary>Реестр типов и порядка специфичности, выведенный из правил (F-7).</summary>
    public IReadOnlyList<KeyValuePair<string, int>> TypeRanks()
    {
        var ranks = new List<KeyValuePair<string, int>>();
        foreach (var rule in Rules) ranks.Add(new KeyValuePair<string, int>(rule.SemanticType, rule.Rank));
        return ranks;
    }
}

/// <summary>Скомпилированное правило: выражения разбираются один раз на прогон.</summary>
internal sealed class CompiledRule
{
    public CompiledRule(ClassificationRule rule)
    {
        Rule = rule;

        Names = Compile(rule.NamePatterns);
        Comments = Compile(rule.CommentPatterns);
        Value = string.IsNullOrEmpty(rule.ValuePattern)
            ? null
            : new Regex(rule.ValuePattern, RegexOptions.CultureInvariant);
    }

    public ClassificationRule Rule { get; }
    public IReadOnlyList<Regex> Names { get; }
    public IReadOnlyList<Regex> Comments { get; }
    public Regex? Value { get; }

    private static IReadOnlyList<Regex> Compile(IReadOnlyList<string> patterns)
    {
        var compiled = new List<Regex>(patterns.Count);

        foreach (var pattern in patterns)
            compiled.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

        return compiled;
    }
}
