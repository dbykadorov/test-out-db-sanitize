using System.Text.Json;
using Sanitize.Adapters.Postgres;
using Sanitize.Core.Classification;
using Sanitize.Core.Planning;
using Sanitize.Core.Policy;
using Sanitize.Transformer;

namespace Sanitize.Worker;

/// <summary>
/// Стадия 3: перенос.
///
/// Своего кода переноса нет: дамп, трансформация на лету, параллелизм
/// и восстановление выполняет Greenmask. Наше - только конфигурация и планы
/// трансформера, порождённые из утверждённой политики.
/// </summary>
public sealed class ExecutionStage
{
    private readonly RunSettings _settings;
    private readonly RunLog _log;

    public ExecutionStage(RunSettings settings, RunLog log)
    {
        _settings = settings;
        _log = log;
    }

    public GreenmaskDriver Driver(RunPaths paths) => new(new GreenmaskSettings
    {
        // Greenmask передаёт строку внешним pg_dump и pg_restore, поэтому
        // здесь нужен формат libpq, а не формат Npgsql.
        SourceDsn = PostgresDsn.ToLibpq(_settings.SourceDsn),
        TargetDsn = PostgresDsn.ToLibpq(_settings.TargetDsn),
        StoragePath = paths.Storage,
        TransformerPath = _settings.TransformerPath,
        TransformerPlanDirectory = paths.TransformerPlans,
        ExecutablePath = _settings.GreenmaskPath,
        Jobs = _settings.Jobs
    });

    /// <summary>
    /// План на каждую таблицу отдельным файлом.
    ///
    /// Один общий план был бы ошибкой: имена колонок в разных таблицах
    /// совпадают, и общий словарь «имя - решение» склеил бы разные колонки.
    /// Greenmask запускает свой экземпляр трансформера на таблицу, поэтому
    /// разделение бесплатно.
    /// </summary>
    public void WritePlans(RunPlan plan, RunPolicy policy, ModelArtifact artifact, RunPaths paths)
    {
        foreach (var table in plan.Tables)
        {
            var columns = new List<ColumnPlan>(table.Columns.Count);

            foreach (var column in table.Columns)
            {
                columns.Add(new ColumnPlan
                {
                    Name = column.Column,
                    Kind = column.CanonicalKind,
                    Type = column.SemanticType,
                    Mode = column.Mode.ToString(),
                    Strategy = column.Strategy.ToString(),
                    DocumentPaths = column.DocumentPaths
                });
            }

            var transformerPlan = new TransformerPlan
            {
                RunId = paths.RunId,
                DictionaryPath = paths.DictionaryFile,
                SecretPath = _settings.SecretPath,
                Columns = columns,
                ExceptionFingerprints = policy.ExceptionFingerprints,
                Components = artifact.Components,
                Templates = artifact.Templates,
                ArtifactFingerprint = artifact.Fingerprint,
                Workers = 0
            };

            var path = Path.Combine(paths.TransformerPlans, $"{table.Schema}.{table.Table}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(transformerPlan, RunPolicy.JsonOptions));
        }

        _log.Write($"планы трансформера: таблиц {plan.Tables.Count}");
    }

    public async Task<string> DumpAsync(RunPlan plan, RunPaths paths, CancellationToken token)
    {
        var driver = Driver(paths);

        File.WriteAllText(paths.GreenmaskConfig, driver.BuildConfig(plan));
        _log.Write("конфигурация переноса записана");

        var dump = await driver.DumpAsync(paths.GreenmaskConfig, token).ConfigureAwait(false);

        if (!dump.Success)
        {
            throw new InvalidOperationException(
                $"дамп не выполнен (код {dump.ExitCode}):{Environment.NewLine}{Tail(dump.StandardError)}");
        }

        _log.Write("дамп с трансформацией выполнен");

        return LatestDumpId(paths);
    }

    public async Task RestoreAsync(RunPaths paths, string dumpId, CancellationToken token)
    {
        // Приёмник очищается перед загрузкой. Иначе pg_restore спотыкается
        // о существующие объекты, данные не грузятся, а код возврата остаётся
        // нулевым - и проверки идут по результату прошлого прогона.
        var target = new PostgresProbe(_settings.TargetDsn);

        foreach (var schema in _settings.Schemas)
            await target.ResetSchemaAsync(schema, token).ConfigureAwait(false);

        _log.Write("приёмник очищен перед восстановлением");

        var driver = Driver(paths);
        var restore = await driver.RestoreAsync(paths.GreenmaskConfig, dumpId, token).ConfigureAwait(false);

        // Код возврата - не доказательство. pg_restore сообщает об ошибках
        // в поток вывода и всё равно завершается нулём, поэтому вывод читается
        // отдельно: «восстановление выполнено» обязано означать «данные легли».
        var complaints = Complaints(restore.StandardOutput, restore.StandardError);

        if (complaints.Count > 0)
        {
            throw new InvalidOperationException(
                "восстановление сообщило об ошибках, хотя завершилось кодом " +
                $"{restore.ExitCode}:{Environment.NewLine}" +
                string.Join(Environment.NewLine, complaints));
        }

        if (!restore.Success)
        {
            // Восстановление включает ограничения и ключи: его успех и есть
            // проверка целостности связей (F-8). Провал здесь означает, что
            // замены порвали связи, а не что «база капризничает».
            throw new InvalidOperationException(
                $"восстановление не выполнено (код {restore.ExitCode}):" +
                $"{Environment.NewLine}{Tail(restore.StandardError)}");
        }

        _log.Write($"восстановление выполнено, дамп {dumpId}");
    }

    /// <summary>
    /// Идентификатор последнего дампа - имя каталога в хранилище. Оно же
    /// числовое и монотонное, поэтому «последний» определяется однозначно.
    /// </summary>
    private static string LatestDumpId(RunPaths paths)
    {
        var directories = Directory.GetDirectories(paths.Storage);

        if (directories.Length == 0)
            throw new InvalidOperationException("хранилище дампов пусто: переносить нечего");

        Array.Sort(directories, StringComparer.Ordinal);

        return Path.GetFileName(directories[^1]);
    }

    /// <summary>
    /// Жалобы восстановления, найденные в его выводе.
    ///
    /// Ищутся именно строки pg_restore: greenmask пересылает их как есть,
    /// пометив уровнем info, и на код возврата они не влияют.
    /// </summary>
    private static IReadOnlyList<string> Complaints(params string[] streams)
    {
        var found = new List<string>();

        foreach (var stream in streams)
        {
            foreach (var line in stream.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.Contains("pg_restore: error", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("pg_restore: warning", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                found.Add(line.Trim());
                if (found.Count >= 10) return found;
            }
        }

        return found;
    }

    /// <summary>
    /// Хвост потока ошибок. Целиком его в сообщение класть нельзя: там строки
    /// журнала Greenmask, и в них при отладочном уровне попадают значения.
    /// </summary>
    private static string Tail(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var from = Math.Max(0, lines.Length - 12);

        return string.Join(Environment.NewLine, lines[from..]);
    }
}
