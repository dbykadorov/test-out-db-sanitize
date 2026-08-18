using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sanitize.Core.Policy;

/// <summary>Адрес колонки в источнике. Для ядра это просто три имени.</summary>
public readonly record struct ColumnAddress(string Schema, string Table, string Column)
{
    public string Qualified => $"{Schema}.{Table}.{Column}";

    public string TableQualified => $"{Schema}.{Table}";
}

/// <summary>Как обрабатывается колонка.</summary>
public enum ColumnMode
{
    /// <summary>Типизированная колонка: значение заменяется целиком.</summary>
    Typed,

    /// <summary>Свободный текст: содержимое затирается целиком (умолчание F-4a).</summary>
    TextWipe,

    /// <summary>
    /// Свободный текст с точечной заменой вкраплений - именованное отступление
    /// от F-4, включается только решением владельца данных.
    /// </summary>
    TextSpot,

    /// <summary>
    /// Документ в чужом формате: структура сохраняется, заменяются листья.
    ///
    /// Затирать документ целиком нельзя - на его место пришлось бы положить
    /// строку, и колонка перестала бы быть документом, а восстановление упало
    /// бы на типе. Поэтому обход по путям, а решение по каждому пути - такое же,
    /// как по колонке.
    /// </summary>
    Document
}

/// <summary>Чем вычисляется замена.</summary>
public enum ReplacementStrategy
{
    /// <summary>Не заменяется. Причина обязана быть названа.</summary>
    None,

    /// <summary>Биекция через словарь прогона: ключи, уникальные колонки.</summary>
    Dictionary,

    /// <summary>Чистая функция без состояния: значение вне словарных доменов.</summary>
    Function,

    /// <summary>Беспорядок внутри конечного домена (Р-3).</summary>
    Derangement
}

/// <summary>Откуда взялся вердикт. Нужен, чтобы разметку можно было оспорить.</summary>
public enum VerdictSource
{
    /// <summary>Имя колонки - сильнейший признак и самый частый источник ошибок.</summary>
    ColumnName,

    /// <summary>Комментарий к объекту схемы.</summary>
    ObjectComment,

    /// <summary>Формат значений и контрольные суммы на выборке.</summary>
    ValueFormat,

    /// <summary>Разбор естественного языка.</summary>
    TextDetector,

    /// <summary>Арбитраж модели по вычисленным признакам.</summary>
    Model,

    /// <summary>Решение человека при утверждении политики.</summary>
    Human
}

public sealed record PolicyColumn
{
    [JsonPropertyName("schema")] public required string Schema { get; init; }
    [JsonPropertyName("table")] public required string Table { get; init; }
    [JsonPropertyName("column")] public required string Column { get; init; }

    /// <summary>Тип источника так, как его назвал адаптер. Ядро им не управляет.</summary>
    [JsonPropertyName("dataType")] public required string DataType { get; init; }

    [JsonPropertyName("sensitive")] public required bool Sensitive { get; init; }

    [JsonPropertyName("semanticType")] public string SemanticType { get; init; } = "unknown";

    [JsonPropertyName("mode")] public ColumnMode Mode { get; init; } = ColumnMode.Typed;

    [JsonPropertyName("strategy")] public ReplacementStrategy Strategy { get; init; } =
        ReplacementStrategy.None;

    /// <summary>Как канонизировать значение перед поиском ключа (F-7).</summary>
    [JsonPropertyName("canonicalKind")] public string CanonicalKind { get; init; } = "Text";

    [JsonPropertyName("confidence")] public double Confidence { get; init; }

    [JsonPropertyName("source")] public VerdictSource Source { get; init; } = VerdictSource.ColumnName;

    /// <summary>
    /// Почему колонка не заменяется. Пустая причина у незаменяемой колонки -
    /// это необъяснённая ПДн в выгрузке, поэтому политика такую отвергает.
    /// </summary>
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";

    /// <summary>Уверенность ниже порога: решение принимает человек.</summary>
    [JsonPropertyName("needsApproval")] public bool NeedsApproval { get; init; }

    /// <summary>
    /// Разметка путей внутри документа: путь - семантический тип.
    ///
    /// Заполняется только для <see cref="ColumnMode.Document"/>. Путь, которого
    /// здесь нет, считается неразмеченным: строковое значение по такому пути
    /// затирается, потому что «не размечено» означает «не смотрели», а не «чисто».
    /// </summary>
    [JsonPropertyName("documentPaths")]
    public IReadOnlyDictionary<string, string> DocumentPaths { get; init; } =
        new Dictionary<string, string>();

    [JsonIgnore] public ColumnAddress Address => new(Schema, Table, Column);
}

/// <summary>
/// Именованное отступление от требования. Паспорт выгрузки перечисляет их
/// поимённо: остаточный риск управляем только тогда, когда назван.
/// </summary>
public sealed record Departure
{
    [JsonPropertyName("requirement")] public required string Requirement { get; init; }
    [JsonPropertyName("what")] public required string What { get; init; }
    [JsonPropertyName("acceptedBy")] public required string AcceptedBy { get; init; }
}

/// <summary>
/// Политика прогона: утверждаемый артефакт, привязанный к отпечатку схемы.
///
/// Ядру она известна целиком, а вот как она получена - интроспекцией,
/// разбором дампа или чтением файла - ядро не знает и знать не должно.
/// </summary>
public sealed record RunPolicy
{
    [JsonPropertyName("version")] public required string Version { get; init; }

    /// <summary>Отпечаток схемы источника. Его расхождение останавливает прогон (F-4).</summary>
    [JsonPropertyName("schemaFingerprint")] public required string SchemaFingerprint { get; init; }

    /// <summary>Режим отправки имён во внешнюю модель: normal или strict (раздел 8).</summary>
    [JsonPropertyName("nameMode")] public string NameMode { get; init; } = "strict";

    [JsonPropertyName("artifactFingerprint")] public string ArtifactFingerprint { get; init; } = "";

    [JsonPropertyName("columns")] public required IReadOnlyList<PolicyColumn> Columns { get; init; }

    /// <summary>
    /// Исключения из единой замены (Р-9): только отпечатки значений.
    /// Сами значения сюда не попадают - иначе перечень исключений
    /// сам стал бы утечкой.
    /// </summary>
    [JsonPropertyName("exceptionFingerprints")]
    public IReadOnlyList<string> ExceptionFingerprints { get; init; } = Array.Empty<string>();

    [JsonPropertyName("departures")]
    public IReadOnlyList<Departure> Departures { get; init; } = Array.Empty<Departure>();

    /// <summary>
    /// Проверка внутренней согласованности до запуска. Политика, которая
    /// не сходится сама с собой, испортит данные молча, а не громко.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var column in Columns)
        {
            var address = column.Address.Qualified;

            if (!seen.Add(address))
                problems.Add($"{address}: колонка описана в политике дважды");

            if (column.Sensitive && column.Strategy == ReplacementStrategy.None &&
                string.IsNullOrWhiteSpace(column.Reason))
            {
                problems.Add(
                    $"{address}: чувствительная колонка без замены и без причины. " +
                    "Незаменённые персональные данные обязаны быть объяснены (Р-2, Р-3)");
            }

            if (!column.Sensitive && column.Strategy != ReplacementStrategy.None)
                problems.Add($"{address}: замена назначена колонке, помеченной как не чувствительная");

            if (column.Sensitive && column.SemanticType == "unknown" &&
                column.Strategy != ReplacementStrategy.None)
            {
                problems.Add($"{address}: замена назначена, но семантический тип не определён");
            }

            if (column.NeedsApproval)
                problems.Add($"{address}: колонка ждёт решения человека, политика не утверждена");

            // Вид канонизации проверяется здесь, а не при первой строке потока:
            // опечатка в справочнике артефакта иначе уронила бы прогон
            // на середине переноса.
            if (column.Strategy != ReplacementStrategy.None &&
                !Enum.TryParse<Values.CanonicalKind>(column.CanonicalKind, ignoreCase: true, out _))
            {
                problems.Add(
                    $"{address}: неизвестный вид канонизации {column.CanonicalKind}");
            }
        }

        if (string.IsNullOrWhiteSpace(SchemaFingerprint))
            problems.Add("политика без отпечатка схемы: проверка F-4 невыполнима");

        return problems;
    }

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,

        // Файлы политики и решений человека пишут люди, а не только программа.
        // Разбор с учётом регистра означал бы, что «Column» вместо «column»
        // молча теряет решение владельца - а это решение о персональных данных.
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static RunPolicy FromJson(string json) =>
        JsonSerializer.Deserialize<RunPolicy>(json, JsonOptions)
        ?? throw new InvalidDataException("Пустая политика прогона");
}
