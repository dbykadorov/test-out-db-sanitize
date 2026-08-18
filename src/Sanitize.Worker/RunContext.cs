namespace Sanitize.Worker;

/// <summary>
/// Настройки прогона. Всё приходит из окружения контура, кроме секрета:
/// он подаётся файлом только на чтение, и в конфигурации лежит путь,
/// а не значение (раздел 8 архитектуры).
/// </summary>
public sealed record RunSettings
{
    /// <summary>
    /// Строки подключения приходят из каталога контура по идентификатору
    /// из заявки, а не из окружения: один контур обслуживает несколько
    /// источников, и выбор делает заказчик выгрузки.
    /// </summary>
    public string SourceDsn { get; init; } = "";

    public string TargetDsn { get; init; } = "";
    public required string SecretPath { get; init; }
    public required string ArtifactPath { get; init; }
    public required string WorkDirectory { get; init; }

    /// <summary>Каталог источников и приёмников контура.</summary>
    public required string CatalogPath { get; init; }

    /// <summary>
    /// Промежуточная база рабочей зоны: сюда разворачивается чужой дамп
    /// перед анализом. Часть рабочей зоны со всеми её правилами - там лежат
    /// исходные персональные данные.
    /// </summary>
    public string StagingDsn { get; init; } = "";

    /// <summary>
    /// Область публикации - отдельная зона и отдельный том. Рабочая зона
    /// прогона сервису выдачи не видна: там рядом с артефактом лежит словарь
    /// соответствий (раздел 8 архитектуры).
    /// </summary>
    public required string PublishDirectory { get; init; }

    /// <summary>
    /// База управления: очередь заявок, статусы, паспорта, аудит.
    /// Данных прогона там нет и быть не может - только метаданные.
    /// </summary>
    public string ControlDsn { get; init; } = "";

    public string PresidioUrl { get; init; } = "";
    public string GreenmaskPath { get; init; } = "/usr/bin/greenmask";
    public string TransformerPath { get; init; } = "/opt/sanitize/transformer";
    public string ApprovalsPath { get; init; } = "";
    public string GroundTruthPath { get; init; } = "";
    public string Language { get; init; } = "ru";
    public string[] Schemas { get; init; } = { "public" };

    /// <summary>Сколько значений брать в выборку при разметке колонки.</summary>
    public int SampleSize { get; init; } = 200;

    /// <summary>Сколько значений брать в эталонный корпус проверки полноты.</summary>
    public int CorpusSize { get; init; } = 2000;

    public int Jobs { get; init; } = 4;

    public static RunSettings FromEnvironment()
    {
        string Required(string name) =>
            Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"Не задана переменная {name}");

        string Optional(string name, string fallback) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

        return new RunSettings
        {
            CatalogPath = Required("SANITIZE_CATALOG_FILE"),
            StagingDsn = Optional("SANITIZE_STAGING_DSN", ""),
            SecretPath = Required("SANITIZE_SECRET_FILE"),
            ArtifactPath = Required("SANITIZE_ARTIFACT_FILE"),
            WorkDirectory = Optional("SANITIZE_WORK_DIR", "/var/lib/sanitize/runs"),
            PublishDirectory = Optional("SANITIZE_PUBLISH_DIR", "/var/lib/sanitize/published"),
            ControlDsn = Optional("SANITIZE_CONTROL_DSN", ""),
            PresidioUrl = Optional("SANITIZE_PRESIDIO_URL", ""),
            GreenmaskPath = Optional("SANITIZE_GREENMASK", "/usr/bin/greenmask"),
            TransformerPath = Optional("SANITIZE_TRANSFORMER", "/opt/sanitize/transformer"),
            ApprovalsPath = Optional("SANITIZE_APPROVALS_FILE", ""),
            GroundTruthPath = Optional("SANITIZE_GROUND_TRUTH_FILE", ""),
            Jobs = int.TryParse(Optional("SANITIZE_JOBS", "4"), out var jobs) ? jobs : 4
        };
    }
}

/// <summary>
/// Рабочая зона прогона. Самое опасное место системы: здесь одновременно
/// лежат выборки исходных данных и словарь соответствий. Каталог свой
/// на каждый прогон - чтобы его можно было удалить целиком.
/// </summary>
public sealed class RunPaths
{
    private readonly string _publishRoot;

    public RunPaths(string root, string publishRoot, string runId)
    {
        RunId = runId;
        _publishRoot = publishRoot;
        Root = Path.Combine(root, runId);

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(TransformerPlans);
        Directory.CreateDirectory(Storage);
    }

    public string RunId { get; }
    public string Root { get; }

    public string Policy => Path.Combine(Root, "policy.json");
    public string DictionaryFile => Path.Combine(Root, "dictionary.bin");
    public string TransformerPlans => Path.Combine(Root, "plans");
    public string GreenmaskConfig => Path.Combine(Root, "greenmask.yaml");
    public string Storage => Path.Combine(Root, "dumps");
    public string Checks => Path.Combine(Root, "checks.json");
    public string PassportJson => Path.Combine(Root, "passport.json");
    public string PassportText => Path.Combine(Root, "passport.md");
    public string Log => Path.Combine(Root, "run.log");

    /// <summary>
    /// Область публикации - отдельная зона (раздел 8 архитектуры).
    ///
    /// Рабочая зона прогона закрыта: там рядом лежат выборки исходных данных
    /// и словарь соответствий, и доступ к ней есть только у самого прогона.
    /// Получателю передаётся не она, а копия артефакта и паспорт. Без этого
    /// разделения сервису выдачи пришлось бы дать права на рабочую зону,
    /// то есть на словарь.
    /// </summary>
    public string Publication => Path.Combine(_publishRoot, RunId);
}

/// <summary>
/// Журнал прогона. Правило раздела 8 соблюдается механически: сюда пишутся
/// только сообщения, а значения данных в них не попадают - для этого в коде
/// нет ни одного вызова, который принимал бы значение колонки.
/// </summary>
public sealed class RunLog
{
    private readonly string _path;
    private readonly object _lock = new();

    public RunLog(string path)
    {
        _path = path;
        File.WriteAllText(path, "");
    }

    public void Write(string message)
    {
        var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} {message}";

        lock (_lock)
        {
            File.AppendAllText(_path, line + Environment.NewLine);
        }

        Console.WriteLine(line);
    }
}
