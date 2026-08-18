using System.Text.Json;
using Npgsql;

// API заявок: контур управления.
//
// Доступа к данным здесь нет вовсе - ни к источнику, ни к результату,
// ни к рабочей зоне. На стенде это подкреплено сетью: контейнер подключён
// только к сети контура управления. Через API проходят заявки, статусы,
// паспорта и аудит; поток артефакта идёт мимо, через сервис выдачи.

var builder = WebApplication.CreateBuilder(args);

var dsn = Environment.GetEnvironmentVariable("SANITIZE_CONTROL_DSN")
          ?? throw new InvalidOperationException("Не задана переменная SANITIZE_CONTROL_DSN");

builder.Services.AddSingleton(new NpgsqlDataSourceBuilder(dsn).Build());

var app = builder.Build();

var json = new JsonSerializerOptions { WriteIndented = true };

// Заглушка идентичности: субъект приходит заголовком, роль читается
// из статического списка. Настоящий провайдер подставляется здесь же,
// за тем же интерфейсом. Что теряется на заглушке - в README, раздел
// «Границы и честные оговорки».
async Task<string?> RoleOf(NpgsqlDataSource source, HttpContext context)
{
    var subject = context.Request.Headers["X-Subject"].ToString();
    if (subject.Length == 0) return null;

    await using var command = source.CreateCommand("SELECT role FROM identities WHERE subject = $1");
    command.Parameters.AddWithValue(subject);

    return await command.ExecuteScalarAsync() as string;
}

async Task Audit(NpgsqlDataSource source, string actor, string action, string subject, string detail = "")
{
    await using var command = source.CreateCommand(
        "INSERT INTO audit (actor, action, subject, detail) VALUES ($1, $2, $3, $4)");

    command.Parameters.AddWithValue(actor);
    command.Parameters.AddWithValue(action);
    command.Parameters.AddWithValue(subject);
    command.Parameters.AddWithValue(detail);

    await command.ExecuteNonQueryAsync();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Перечень того, что вообще можно заказать. Строк подключения здесь нет:
// заказчик выбирает источник по имени, а не приносит своё подключение.
//
// Признак `ready` показывает, объявил ли воркер этот ресурс своим. Регистрация
// состоит из двух шагов в разных зонах - метаданные здесь, подключение
// в плоскости данных, - и без такого признака рассогласование шагов
// всплывало бы только в середине прогона.
app.MapGet("/api/sources", async (NpgsqlDataSource source) =>
{
    async Task<List<object>> Read(string table)
    {
        await using var command = source.CreateCommand(
            $"""
             SELECT id, title, kind, enabled, available_at,
                    available_at IS NOT NULL AND available_at > now() - interval '2 minutes'
             FROM {table} ORDER BY id
             """);

        await using var reader = await command.ExecuteReaderAsync();
        var items = new List<object>();

        while (await reader.ReadAsync())
        {
            var ready = reader.GetBoolean(5);

            items.Add(new
            {
                id = reader.GetString(0),
                title = reader.GetString(1),
                kind = reader.GetString(2),
                enabled = reader.GetBoolean(3),
                ready,
                hint = ready
                    ? ""
                    : "воркер не объявил этот ресурс своим: подключение к нему " +
                      "не заведено в каталоге плоскости данных"
            });
        }

        return items;
    }

    return Results.Ok(new { sources = await Read("sources"), sinks = await Read("sinks") });
});

// Регистрация ресурса - это объявление ИМЕНИ, а не подключения.
// Учётные данные заводятся отдельно и сюда не попадают ни в каком виде.
app.MapPost("/api/sources", async (NpgsqlDataSource source, HttpContext context, NewResource body) =>
{
    var role = await RoleOf(source, context);
    if (role is not ("owner" or "operator")) return Results.StatusCode(403);

    return await Register(source, context, "sources", body, new[] { "connection", "dump" });
});

app.MapPost("/api/sinks", async (NpgsqlDataSource source, HttpContext context, NewResource body) =>
{
    var role = await RoleOf(source, context);
    if (role is not ("owner" or "operator")) return Results.StatusCode(403);

    return await Register(source, context, "sinks", body, new[] { "database", "dump" });
});

async Task<IResult> Register(
    NpgsqlDataSource source, HttpContext context, string table, NewResource body, string[] kinds)
{
    var actor = context.Request.Headers["X-Subject"].ToString();
    var id = (body.Id ?? "").Trim();

    // Идентификатор попадает в имена файлов и в журналы, поэтому алфавит узкий.
    if (id.Length == 0 || id.Length > 64 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        return Results.BadRequest(new { error = "Идентификатор: буквы латиницы, цифры, дефис и подчёркивание, до 64 знаков" });

    if (!kinds.Contains(body.Kind))
        return Results.BadRequest(new { error = $"Вид ресурса: {string.Join(" или ", kinds)}" });

    // Подключение через контур управления не проходит. Проверка явная,
    // потому что подать его сюда - первое, что приходит в голову.
    if (!string.IsNullOrEmpty(body.Dsn))
    {
        return Results.BadRequest(new
        {
            error = "Строка подключения в контур управления не принимается",
            hint = "Подключение заводится в каталоге плоскости данных; здесь регистрируется только имя"
        });
    }

    await using var command = source.CreateCommand(
        $"""
         INSERT INTO {table} (id, title, kind, registered_by) VALUES ($1, $2, $3, $4)
         ON CONFLICT (id) DO UPDATE SET title = $2, kind = $3, enabled = true
         """);

    command.Parameters.AddWithValue(id);
    command.Parameters.AddWithValue(string.IsNullOrWhiteSpace(body.Title) ? id : body.Title!);
    command.Parameters.AddWithValue(body.Kind);
    command.Parameters.AddWithValue(actor);

    await command.ExecuteNonQueryAsync();
    await Audit(source, actor, table == "sources" ? "source.registered" : "sink.registered", id, body.Kind);

    return Results.Ok(new
    {
        id,
        kind = body.Kind,
        next = "Заведите подключение в каталоге плоскости данных. Готовность: GET /api/sources"
    });
}

app.MapDelete("/api/sources/{id}", async (NpgsqlDataSource source, HttpContext context, string id) =>
{
    var role = await RoleOf(source, context);
    if (role is not ("owner" or "operator")) return Results.StatusCode(403);

    var actor = context.Request.Headers["X-Subject"].ToString();

    // Ресурс выключается, а не удаляется: заявки на него уже ссылаются,
    // и их история должна остаться читаемой.
    await using var command = source.CreateCommand("UPDATE sources SET enabled = false WHERE id = $1");
    command.Parameters.AddWithValue(id);

    var affected = await command.ExecuteNonQueryAsync();
    await Audit(source, actor, "source.disabled", id, "");

    return affected > 0 ? Results.Ok(new { id, enabled = false }) : Results.NotFound();
});

app.MapPost("/api/requests", async (NpgsqlDataSource source, HttpContext context, NewRequest body) =>
{
    var role = await RoleOf(source, context);
    if (role is not ("owner" or "operator")) return Results.StatusCode(403);

    var actor = context.Request.Headers["X-Subject"].ToString();
    var runId = "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Random.Shared.Next(1000, 9999);

    // Источник обязателен и обязан быть зарегистрирован. Умолчания здесь нет
    // намеренно: «взять первый попавшийся» - это выгрузка не той базы,
    // а ошибиться базой в этой задаче дороже, чем не запуститься.
    var sourceId = body.SourceId ?? "";
    var sinkId = body.SinkId ?? "";

    // Проверяется не только регистрация, но и готовность: заявка на ресурс,
    // подключение к которому не заведено, обязана быть отклонена сейчас,
    // а не упасть в середине прогона.
    async Task<string?> Problem(string table, string id, string what)
    {
        if (id.Length == 0) return $"{what} не указан";

        await using var check = source.CreateCommand(
            $"""
             SELECT enabled, available_at IS NOT NULL AND available_at > now() - interval '2 minutes'
             FROM {table} WHERE id = $1
             """);

        check.Parameters.AddWithValue(id);

        await using var reader = await check.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return $"{what} {id} не зарегистрирован в контуре";

        if (!reader.GetBoolean(0)) return $"{what} {id} выключен";

        if (!reader.GetBoolean(1))
            return $"{what} {id} зарегистрирован, но воркер не объявил его своим: подключение не заведено";

        return null;
    }

    var problem = await Problem("sources", sourceId, "Источник") ??
                  await Problem("sinks", sinkId, "Приёмник");

    if (problem is not null)
    {
        return Results.BadRequest(new
        {
            error = problem,
            hint = "Перечень и готовность: GET /api/sources. Строку подключения в заявке " +
                   "передать нельзя: учётные данные через контур управления не проходят"
        });
    }

    await using var command = source.CreateCommand(
        """
        INSERT INTO requests (run_id, requested_by, purpose, source_id, sink_id)
        VALUES ($1, $2, $3, $4, $5) RETURNING id
        """);

    command.Parameters.AddWithValue(runId);
    command.Parameters.AddWithValue(actor);
    command.Parameters.AddWithValue(body.Purpose);
    command.Parameters.AddWithValue(sourceId);
    command.Parameters.AddWithValue(sinkId);

    var id = (long)(await command.ExecuteScalarAsync() ?? 0L);

    await Audit(source, actor, "request.created", runId, $"{sourceId} -> {sinkId}: {body.Purpose}");

    return Results.Ok(new { id, runId, status = "queued", sourceId, sinkId });
});

app.MapGet("/api/requests", async (NpgsqlDataSource source) =>
{
    await using var command = source.CreateCommand(
        """
        SELECT run_id, requested_by, purpose, status, created_at, finished_at, publishable,
               source_id, sink_id
        FROM requests ORDER BY id DESC LIMIT 100
        """);

    await using var reader = await command.ExecuteReaderAsync();
    var items = new List<object>();

    while (await reader.ReadAsync())
    {
        items.Add(new
        {
            runId = reader.GetString(0),
            requestedBy = reader.GetString(1),
            purpose = reader.GetString(2),
            status = reader.GetString(3),
            createdAt = reader.GetDateTime(4),
            finishedAt = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5),
            publishable = reader.IsDBNull(6) ? (bool?)null : reader.GetBoolean(6),
            sourceId = reader.GetString(7),
            sinkId = reader.GetString(8)
        });
    }

    return Results.Ok(items);
});

app.MapGet("/api/requests/{runId}/passport", async (NpgsqlDataSource source, string runId) =>
{
    await using var command = source.CreateCommand(
        "SELECT passport, status, error FROM requests WHERE run_id = $1");

    command.Parameters.AddWithValue(runId);

    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) return Results.NotFound();

    if (reader.IsDBNull(0))
    {
        return Results.Ok(new
        {
            status = reader.GetString(1),
            error = reader.IsDBNull(2) ? "" : reader.GetString(2),
            passport = (object?)null
        });
    }

    return Results.Content(reader.GetString(0), "application/json");
});

app.MapPost("/api/requests/{runId}/grants", async (
    NpgsqlDataSource source, HttpContext context, string runId, NewGrant body) =>
{
    var role = await RoleOf(source, context);
    if (role != "owner") return Results.StatusCode(403);

    var actor = context.Request.Headers["X-Subject"].ToString();

    // Выдача разрешается только по пригодному к публикации прогону.
    // Частично обезличенная база опаснее отсутствующей, поэтому здесь
    // проверка, а не предупреждение.
    await using var check = source.CreateCommand(
        "SELECT id, publishable FROM requests WHERE run_id = $1");

    check.Parameters.AddWithValue(runId);

    long requestId;

    await using (var reader = await check.ExecuteReaderAsync())
    {
        if (!await reader.ReadAsync()) return Results.NotFound();

        requestId = reader.GetInt64(0);

        if (reader.IsDBNull(1) || !reader.GetBoolean(1))
        {
            return Results.BadRequest(new
            {
                error = "Артефакт не признан пригодным к публикации: выдача запрещена"
            });
        }
    }

    await using var insert = source.CreateCommand(
        """
        INSERT INTO grants (request_id, recipient, granted_by) VALUES ($1, $2, $3)
        ON CONFLICT (request_id, recipient) DO UPDATE SET revoked_at = NULL
        """);

    insert.Parameters.AddWithValue(requestId);
    insert.Parameters.AddWithValue(body.Recipient);
    insert.Parameters.AddWithValue(actor);

    await insert.ExecuteNonQueryAsync();
    await Audit(source, actor, "grant.issued", runId, body.Recipient);

    return Results.Ok(new { runId, recipient = body.Recipient });
});

app.MapDelete("/api/requests/{runId}/grants/{recipient}", async (
    NpgsqlDataSource source, HttpContext context, string runId, string recipient) =>
{
    var role = await RoleOf(source, context);
    if (role != "owner") return Results.StatusCode(403);

    var actor = context.Request.Headers["X-Subject"].ToString();

    await using var command = source.CreateCommand(
        """
        UPDATE grants SET revoked_at = now()
        WHERE recipient = $2
          AND request_id = (SELECT id FROM requests WHERE run_id = $1)
        """);

    command.Parameters.AddWithValue(runId);
    command.Parameters.AddWithValue(recipient);

    var affected = await command.ExecuteNonQueryAsync();
    await Audit(source, actor, "grant.revoked", runId, recipient);

    return affected > 0 ? Results.Ok(new { revoked = affected }) : Results.NotFound();
});

app.MapGet("/api/audit", async (NpgsqlDataSource source) =>
{
    await using var command = source.CreateCommand(
        "SELECT at, actor, action, subject, detail FROM audit ORDER BY id DESC LIMIT 200");

    await using var reader = await command.ExecuteReaderAsync();
    var items = new List<object>();

    while (await reader.ReadAsync())
    {
        items.Add(new
        {
            at = reader.GetDateTime(0),
            actor = reader.GetString(1),
            action = reader.GetString(2),
            subject = reader.GetString(3),
            detail = reader.GetString(4)
        });
    }

    return Results.Ok(items);
});

app.Run();

internal sealed record NewRequest(string Purpose, string? SourceId, string? SinkId);
internal sealed record NewGrant(string Recipient);

/// <summary>
/// Регистрация ресурса. Поле <see cref="Dsn"/> объявлено намеренно и всегда
/// отвергается: подать подключение сюда - первое, что приходит в голову,
/// и ответ на это должен быть внятным, а не «неизвестное поле».
/// </summary>
internal sealed record NewResource(string? Id, string? Title, string Kind, string? Dsn);
