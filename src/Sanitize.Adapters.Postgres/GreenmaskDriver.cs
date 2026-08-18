using System.Diagnostics;
using System.Text;
using Sanitize.Core.Planning;
using Sanitize.Core.Policy;

namespace Sanitize.Adapters.Postgres;

/// <summary>Куда и чем запускается перенос.</summary>
public sealed record GreenmaskSettings
{
    public required string SourceDsn { get; init; }
    public required string TargetDsn { get; init; }

    /// <summary>Каталог хранилища дампов.</summary>
    public required string StoragePath { get; init; }

    /// <summary>Путь к процессу-трансформеру внутри контейнера прогона.</summary>
    public required string TransformerPath { get; init; }

    /// <summary>Каталог с планами трансформера - по одному на таблицу.</summary>
    public required string TransformerPlanDirectory { get; init; }

    public string ExecutablePath { get; init; } = "/usr/bin/greenmask";

    public int Jobs { get; init; } = 4;

    /// <summary>
    /// Сколько ждать ответа трансформера на строку. Умолчание Greenmask - две
    /// секунды; для процесса, который на старте отображает словарь в память,
    /// этого мало, и прогон падал бы на первой же строке.
    /// </summary>
    public string TransformerTimeout { get; init; } = "300s";
}

/// <summary>
/// Драйвер Greenmask: порождение конфигурации из плана и запуск команд.
///
/// Своего кода переноса нет намеренно (C-1): дамп, трансформация на лету,
/// параллелизм и восстановление уже сделаны и проверены. Наше - только
/// то, чего Greenmask не умеет, и оно живёт в трансформере.
/// </summary>
public sealed class GreenmaskDriver
{
    private readonly GreenmaskSettings _settings;

    public GreenmaskDriver(GreenmaskSettings settings) =>
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <summary>
    /// Конфигурация прогона. Секрет замены сюда не попадает ни в каком виде:
    /// конфигурация переживает прогон, а секрет - нет (раздел 8).
    /// </summary>
    public string BuildConfig(RunPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var yaml = new StringBuilder();

        yaml.AppendLine("common:");
        yaml.AppendLine("  pg_bin_path: \"/usr/bin\"");
        yaml.AppendLine("  tmp_dir: \"/tmp\"");
        yaml.AppendLine();
        yaml.AppendLine("log:");
        yaml.AppendLine("  level: \"info\"");
        yaml.AppendLine("  format: \"json\"");
        yaml.AppendLine();
        yaml.AppendLine("storage:");
        yaml.AppendLine("  type: \"directory\"");
        yaml.AppendLine("  directory:");
        yaml.AppendLine($"    path: {Scalar(_settings.StoragePath)}");
        yaml.AppendLine();
        yaml.AppendLine("dump:");
        yaml.AppendLine("  pg_dump_options:");
        yaml.AppendLine($"    dbname: {Scalar(_settings.SourceDsn)}");
        yaml.AppendLine($"    jobs: {_settings.Jobs}");
        yaml.AppendLine("  transformation:");

        foreach (var table in plan.Tables)
        {
            yaml.AppendLine($"    - schema: {Scalar(table.Schema)}");
            yaml.AppendLine($"      name: {Scalar(table.Table)}");
            yaml.AppendLine("      transformers:");
            yaml.AppendLine("        - name: \"Cmd\"");
            yaml.AppendLine("          params:");
            yaml.AppendLine($"            executable: {Scalar(_settings.TransformerPath)}");
            yaml.AppendLine($"            args: [{Scalar(PlanPathOf(table))}]");
            yaml.AppendLine("            driver:");
            yaml.AppendLine("              name: \"json\"");
            yaml.AppendLine($"            timeout: {Scalar(_settings.TransformerTimeout)}");
            yaml.AppendLine("            expected_exit_code: 0");
            yaml.AppendLine("            validate: false");
            yaml.AppendLine("            columns:");

            foreach (var column in table.Columns)
                yaml.AppendLine($"              - name: {Scalar(column.Column)}");
        }

        yaml.AppendLine();
        yaml.AppendLine("validate:");
        yaml.AppendLine("  data: true");
        yaml.AppendLine("  diff: true");
        yaml.AppendLine("  rows_limit: 10");
        yaml.AppendLine("  table_format: \"vertical\"");
        yaml.AppendLine();
        yaml.AppendLine("restore:");
        yaml.AppendLine("  pg_restore_options:");
        yaml.AppendLine($"    dbname: {Scalar(_settings.TargetDsn)}");
        yaml.AppendLine($"    jobs: {_settings.Jobs}");

        return yaml.ToString();
    }

    public string PlanPathOf(TablePlan table) =>
        Path.Combine(_settings.TransformerPlanDirectory, $"{table.Schema}.{table.Table}.json");

    /// <summary>
    /// Скаляр YAML в двойных кавычках. Строка подключения и пути приходят
    /// из конфигурации контура, но экранирование всё равно обязательно:
    /// незакавыченное двоеточие в пути молча порождает другую структуру.
    /// </summary>
    private static string Scalar(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

        return "\"" + escaped + "\"";
    }

    public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public bool Success => ExitCode == 0;
    }

    public Task<CommandResult> DumpAsync(string configPath, CancellationToken token) =>
        RunAsync(configPath, new[] { "dump" }, token);

    public Task<CommandResult> RestoreAsync(string configPath, string dumpId, CancellationToken token) =>
        RunAsync(configPath, new[] { "restore", dumpId, "--restore-in-order" }, token);

    /// <summary>
    /// Сверка «до и после» на выборке. Отдельного кода это не требует (D-4):
    /// команда уже есть, и переписывать её было бы нарушением C-1.
    /// </summary>
    public Task<CommandResult> ValidateAsync(string configPath, CancellationToken token) =>
        RunAsync(configPath, new[] { "validate", "--data", "--diff", "--rows-limit", "10" }, token);

    public Task<CommandResult> ListDumpsAsync(string configPath, CancellationToken token) =>
        RunAsync(configPath, new[] { "list-dumps" }, token);

    private async Task<CommandResult> RunAsync(
        string configPath, IReadOnlyList<string> arguments, CancellationToken token)
    {
        var info = new ProcessStartInfo
        {
            FileName = _settings.ExecutablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        info.ArgumentList.Add("--config");
        info.ArgumentList.Add(configPath);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        process.Start();

        // Оба потока читаются одновременно: последовательное чтение упирается
        // в заполненный буфер второго потока и вешает процесс насмерть.
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);

        await process.WaitForExitAsync(token).ConfigureAwait(false);

        return new CommandResult(
            process.ExitCode,
            await output.ConfigureAwait(false),
            await error.ConfigureAwait(false));
    }
}
