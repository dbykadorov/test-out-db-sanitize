using System.Diagnostics;

namespace Sanitize.Adapters.Postgres;

/// <summary>
/// Загружает готовый дамп в промежуточную базу - ветка `DumpSource`
/// из раздела 4 архитектуры.
///
/// Почему через промежуточную базу, а не напрямую. Greenmask умеет
/// восстанавливать с трансформацией только СВОИ дампы: чужой дамп он читать
/// не обязан, и делать вид, что обязан, было бы неправдой. Кроме того,
/// стадия анализа требует агрегатов и выборок по колонкам, а по файлу дампа
/// их не посчитать. Поэтому чужой дамп сначала разворачивается в рабочей зоне,
/// и дальше идёт ровно тот же конвейер, что и для подключения к реплике.
///
/// Промежуточная база - часть рабочей зоны: там лежат исходные персональные
/// данные, резервное копирование запрещено, содержимое стирается перед
/// каждой загрузкой.
/// </summary>
public sealed class DumpLoader
{
    /// <summary>Подпись дампа PostgreSQL в собственном формате.</summary>
    private static ReadOnlySpan<byte> CustomFormatMagic => "PGDMP"u8;

    private readonly string _stagingDsn;
    private readonly string _binPath;

    public DumpLoader(string stagingDsn, string binPath = "/usr/lib/postgresql/18/bin")
    {
        ArgumentException.ThrowIfNullOrEmpty(stagingDsn);

        _stagingDsn = stagingDsn;
        _binPath = binPath;
    }

    public sealed record LoadResult(bool Success, string Detail);

    /// <summary>
    /// Разворачивает дамп в промежуточную базу.
    ///
    /// Формат определяется по содержимому, а не по расширению: расширение
    /// говорит о намерении автора, а не о том, что в файле.
    /// </summary>
    public async Task<LoadResult> LoadAsync(string dumpPath, IReadOnlyList<string> schemas, CancellationToken token)
    {
        if (!File.Exists(dumpPath) && !Directory.Exists(dumpPath))
            return new LoadResult(false, $"дамп не найден: {dumpPath}");

        var probe = new PostgresProbe(_stagingDsn);

        // Промежуточная база стирается перед загрузкой: остатки предыдущего
        // прогона - это чужие персональные данные в чужой выгрузке.
        foreach (var schema in schemas)
            await probe.ResetSchemaAsync(schema, token).ConfigureAwait(false);

        var custom = Directory.Exists(dumpPath) || IsCustomFormat(dumpPath);

        return custom
            ? await RestoreAsync(dumpPath, token).ConfigureAwait(false)
            : await ReplayAsync(dumpPath, token).ConfigureAwait(false);
    }

    private static bool IsCustomFormat(string path)
    {
        using var stream = File.OpenRead(path);

        Span<byte> head = stackalloc byte[5];
        var read = stream.Read(head);

        return read == head.Length && head.SequenceEqual(CustomFormatMagic);
    }

    private Task<LoadResult> RestoreAsync(string dumpPath, CancellationToken token) =>
        RunAsync(Path.Combine(_binPath, "pg_restore"),
            new[] { "--dbname", PostgresDsn.ToLibpq(_stagingDsn), "--no-owner", "--no-privileges", dumpPath },
            null, token);

    private Task<LoadResult> ReplayAsync(string dumpPath, CancellationToken token) =>
        RunAsync(Path.Combine(_binPath, "psql"),
            new[]
            {
                "--dbname", PostgresDsn.ToLibpq(_stagingDsn),
                // Ошибка в середине текстового дампа обязана останавливать
                // загрузку: половина схемы хуже, чем её отсутствие, потому что
                // анализ пройдёт и выдаст политику по неполной базе.
                "--set", "ON_ERROR_STOP=1",
                "--quiet",
                "--file", dumpPath
            },
            null, token);

    private static async Task<LoadResult> RunAsync(
        string executable, IReadOnlyList<string> arguments, string? input, CancellationToken token)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            UseShellExecute = false
        };

        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        process.Start();

        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);

        await process.WaitForExitAsync(token).ConfigureAwait(false);

        var stderr = await error.ConfigureAwait(false);
        await output.ConfigureAwait(false);

        if (process.ExitCode == 0) return new LoadResult(true, "дамп развёрнут в промежуточной базе");

        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var from = Math.Max(0, lines.Length - 8);

        return new LoadResult(false,
            $"загрузка дампа не выполнена (код {process.ExitCode}): " +
            string.Join(" | ", lines[from..]));
    }
}
