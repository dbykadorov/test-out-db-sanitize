using System.Text.Json;
using System.Text.Json.Serialization;
using Sanitize.Core.Policy;

namespace Sanitize.Worker;

/// <summary>Зарегистрированный источник данных.</summary>
public sealed record CatalogSource
{
    [JsonPropertyName("id")] public required string Id { get; init; }

    /// <summary>`connection` - подключение к реплике, `dump` - готовый дамп (раздел 4).</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = "connection";

    [JsonPropertyName("title")] public string Title { get; init; } = "";

    [JsonPropertyName("schemas")] public IReadOnlyList<string> Schemas { get; init; } = new[] { "public" };

    /// <summary>
    /// Строка подключения для вида `connection`. За пределы плоскости данных
    /// не выходит.
    /// </summary>
    [JsonPropertyName("dsn")] public string Dsn { get; init; } = "";

    /// <summary>
    /// Путь к файлу дампа для вида `dump`. Дамп разворачивается
    /// в промежуточную базу, и дальше идёт обычный конвейер.
    /// </summary>
    [JsonPropertyName("path")] public string Path { get; init; } = "";

    public bool IsDump => string.Equals(Kind, "dump", StringComparison.Ordinal);

    /// <summary>
    /// Проверка сразу при чтении каталога: источник без подключения или без
    /// пути обнаружится при регистрации, а не в середине прогона.
    /// </summary>
    public void Validate()
    {
        if (IsDump)
        {
            if (Path.Length == 0)
                throw new InvalidDataException($"Источник {Id} вида dump объявлен без пути к файлу");
        }
        else if (Dsn.Length == 0)
        {
            throw new InvalidDataException($"Источник {Id} вида connection объявлен без подключения");
        }
    }
}

/// <summary>Зарегистрированный приёмник результата.</summary>
public sealed record CatalogSink
{
    [JsonPropertyName("id")] public required string Id { get; init; }

    /// <summary>`database` - готовая санитарная база, `dump` - дамп для передачи.</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = "database";

    [JsonPropertyName("title")] public string Title { get; init; } = "";

    [JsonPropertyName("dsn")] public required string Dsn { get; init; }
}

/// <summary>
/// Каталог источников и приёмников контура.
///
/// Заявка называет источник **идентификатором**, а не строкой подключения.
/// Причина не в удобстве. Строка подключения в теле заявки означала бы, что
/// учётные данные проходят через контур управления - а раздел 2 архитектуры
/// говорит, что доступа к данным у него нет вовсе. Заодно это закрывает
/// очевидное злоупотребление: иначе заявка превращала бы воркер в утилиту,
/// подключающуюся куда угодно от его имени.
///
/// На стенде каталог - файл, смонтированный только на чтение. В боевом контуре
/// на его месте хранилище секретов, выдающее временные учётные данные на прогон;
/// интерфейс от этого не меняется.
/// </summary>
public sealed class Catalog
{
    [JsonPropertyName("sources")]
    public IReadOnlyList<CatalogSource> Sources { get; init; } = Array.Empty<CatalogSource>();

    [JsonPropertyName("sinks")]
    public IReadOnlyList<CatalogSink> Sinks { get; init; } = Array.Empty<CatalogSink>();

    /// <summary>Записи, пропущенные из-за незаданных переменных. Для журнала.</summary>
    [JsonIgnore] public IReadOnlyList<string> Skipped { get; init; } = Array.Empty<string>();

    public static Catalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path))
            throw new FileNotFoundException("Каталог источников не найден", path);

        var catalog = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(path), RunPolicy.JsonOptions)
                      ?? throw new InvalidDataException($"Пустой каталог источников: {path}");

        var sources = new List<CatalogSource>();
        var sinks = new List<CatalogSink>();
        var skipped = new List<string>();

        foreach (var source in catalog.Sources)
        {
            var dsn = Expand(source.Dsn, out var missingDsn);
            var file = Expand(source.Path, out var missingPath);
            var missing = missingDsn ?? missingPath;

            if (missing is not null)
            {
                skipped.Add($"{source.Id}: переменная {missing} не задана");
                continue;
            }

            sources.Add(source with { Dsn = dsn, Path = file });
        }

        foreach (var sink in catalog.Sinks)
        {
            var dsn = Expand(sink.Dsn, out var missing);

            if (missing is not null)
            {
                skipped.Add($"{sink.Id}: переменная {missing} не задана");
                continue;
            }

            sinks.Add(sink with { Dsn = dsn });
        }

        if (sources.Count == 0)
            throw new InvalidDataException("В каталоге нет ни одного доступного источника");

        if (sinks.Count == 0)
            throw new InvalidDataException("В каталоге нет ни одного доступного приёмника");

        foreach (var source in sources) source.Validate();

        return new Catalog { Sources = sources, Sinks = sinks, Skipped = skipped };
    }

    /// <summary>
    /// Подстановка переменных окружения вида ${ИМЯ}.
    ///
    /// Нужна, чтобы учётные данные не приходилось вписывать в файл, который
    /// лежит в репозитории: в каталоге остаётся ссылка, а само значение
    /// подаётся окружением. Незаданная переменная НЕ подставляется пустотой -
    /// запись просто пропускается, и об этом пишется в журнал. Пустая строка
    /// подключения означала бы попытку соединиться неизвестно с чем, а шаблон
    /// в каталоге не должен ломать уже работающие источники.
    /// </summary>
    private static string Expand(string value, out string? missing)
    {
        missing = null;
        if (value.Length == 0 || !value.Contains("${", StringComparison.Ordinal)) return value;

        var result = new System.Text.StringBuilder(value.Length);
        var i = 0;

        while (i < value.Length)
        {
            var open = value.IndexOf("${", i, StringComparison.Ordinal);
            if (open < 0) { result.Append(value, i, value.Length - i); break; }

            var close = value.IndexOf('}', open);
            if (close < 0) { result.Append(value, i, value.Length - i); break; }

            result.Append(value, i, open - i);

            var name = value[(open + 2)..close];
            var resolved = Environment.GetEnvironmentVariable(name);

            if (string.IsNullOrEmpty(resolved))
            {
                missing = name;
                return "";
            }

            result.Append(resolved);
            i = close + 1;
        }

        return result.ToString();
    }

    /// <summary>
    /// Неизвестный идентификатор останавливает прогон, а не подставляет
    /// умолчание: «взять первый попавшийся источник» - это выгрузка не той базы.
    /// </summary>
    public CatalogSource SourceOf(string id)
    {
        foreach (var source in Sources)
            if (string.Equals(source.Id, id, StringComparison.Ordinal))
                return source;

        throw new InvalidOperationException(
            $"Источник {id} в каталоге контура не зарегистрирован. Известные: " +
            string.Join(", ", Sources.ToList().ConvertAll(s => s.Id)));
    }

    public CatalogSink SinkOf(string id)
    {
        foreach (var sink in Sinks)
            if (string.Equals(sink.Id, id, StringComparison.Ordinal))
                return sink;

        throw new InvalidOperationException(
            $"Приёмник {id} в каталоге контура не зарегистрирован. Известные: " +
            string.Join(", ", Sinks.ToList().ConvertAll(s => s.Id)));
    }
}
