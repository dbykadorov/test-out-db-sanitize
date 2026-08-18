using Npgsql;

namespace Sanitize.Worker;

/// <summary>
/// Очередь заявок в базе управления.
///
/// Воркер сам ходит за заданиями, а не получает их толчком от API: так контур
/// управления не нуждается ни в сетевом доступе к плоскости данных, ни в знании
/// о том, где живут воркеры. Обратно уходят только метаданные - статус, паспорт,
/// текст ошибки. Значений из базы источника здесь не проходит ни одного.
/// </summary>
public sealed class ControlPlane
{
    private readonly NpgsqlDataSource _source;

    public ControlPlane(string dsn) => _source = new NpgsqlDataSourceBuilder(dsn).Build();

    /// <summary>
    /// Берёт следующую заявку и сразу помечает её исполняемой.
    ///
    /// Захват идёт одним запросом с блокировкой строки: два воркера,
    /// прочитавших очередь одновременно, иначе взяли бы одну заявку дважды
    /// и запустили два прогона на одну выгрузку.
    /// </summary>
    public async Task<(string RunId, string RequestedBy, string SourceId, string SinkId)?> TakeAsync(
        CancellationToken token)
    {
        await using var command = _source.CreateCommand(
            """
            UPDATE requests
            SET status = 'running', started_at = now()
            WHERE id = (
                SELECT id FROM requests
                WHERE status = 'queued'
                ORDER BY id
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            RETURNING run_id, requested_by, source_id, sink_id
            """);

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;

        return (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }

    /// <summary>
    /// Объявляет, какие ресурсы воркер в состоянии обслужить.
    ///
    /// Нужно потому, что регистрация ресурса состоит из двух шагов в разных
    /// зонах: метаданные заводит оператор через API, подключение - отдельно,
    /// в плоскости данных. Без объявления рассогласование этих шагов всплывало
    /// бы в середине прогона; с ним заявка на недоступный ресурс отклоняется
    /// сразу, до постановки в очередь.
    ///
    /// Передаются только идентификаторы. Ни строк подключения, ни учётных
    /// данных здесь не проходит - иначе объявление само стало бы тем каналом,
    /// который раздел 2 архитектуры запрещает.
    /// </summary>
    public async Task AnnounceAsync(
        IReadOnlyList<(string Id, string Title, string Kind)> sources,
        IReadOnlyList<(string Id, string Title, string Kind)> sinks,
        CancellationToken token)
    {
        // Два отдельных вызова, а не один составной запрос: подготовленный
        // запрос с параметрами не может содержать несколько команд.
        await MarkAsync("sources", sources, token).ConfigureAwait(false);
        await MarkAsync("sinks", sinks, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Заводит ресурс в реестре, если его там ещё нет, и отмечает доступность.
    ///
    /// Регистрация делается воркером намеренно: иначе добавление источника
    /// требовало бы двух согласованных действий в разных зонах, и человек
    /// неизбежно делал бы одно из них. Наружу при этом уходят ТОЛЬКО имя,
    /// заголовок и вид - ни строк подключения, ни путей к файлам. Именно эта
    /// граница и защищается, а не количество шагов.
    ///
    /// Уже заведённая запись не перетирается заголовком из каталога, если её
    /// правил оператор: явное решение человека главнее файла.
    /// </summary>
    private async Task MarkAsync(
        string table, IReadOnlyList<(string Id, string Title, string Kind)> items, CancellationToken token)
    {
        foreach (var (id, title, kind) in items)
        {
            await using var command = _source.CreateCommand(
                $"""
                 INSERT INTO {table} (id, title, kind, registered_by, available_at)
                 VALUES ($1, $2, $3, 'каталог контура', now())
                 ON CONFLICT (id) DO UPDATE SET available_at = now()
                 """);

            command.Parameters.AddWithValue(id);
            command.Parameters.AddWithValue(title.Length > 0 ? title : id);
            command.Parameters.AddWithValue(kind);

            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    public async Task FinishAsync(string runId, bool publishable, string passportJson, CancellationToken token)
    {
        await using var command = _source.CreateCommand(
            """
            UPDATE requests
            SET status = 'done', finished_at = now(), publishable = $2, passport = $3::jsonb, error = NULL
            WHERE run_id = $1
            """);

        command.Parameters.AddWithValue(runId);
        command.Parameters.AddWithValue(publishable);
        command.Parameters.AddWithValue(passportJson);

        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await AuditAsync(runId, publishable ? "run.published" : "run.blocked", "", token).ConfigureAwait(false);
    }

    public async Task FailAsync(string runId, string error, CancellationToken token)
    {
        await using var command = _source.CreateCommand(
            """
            UPDATE requests
            SET status = 'failed', finished_at = now(), publishable = FALSE, error = $2
            WHERE run_id = $1
            """);

        command.Parameters.AddWithValue(runId);
        command.Parameters.AddWithValue(error);

        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await AuditAsync(runId, "run.failed", error, token).ConfigureAwait(false);
    }

    public async Task AuditAsync(string runId, string action, string detail, CancellationToken token)
    {
        await using var command = _source.CreateCommand(
            "INSERT INTO audit (actor, action, subject, detail) VALUES ('worker', $1, $2, $3)");

        command.Parameters.AddWithValue(action);
        command.Parameters.AddWithValue(runId);
        command.Parameters.AddWithValue(detail);

        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
