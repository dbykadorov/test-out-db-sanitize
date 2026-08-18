using System.IO.Compression;
using Npgsql;

// Сервис выдачи.
//
// Отдельная единица развёртывания намеренно (следствие разбора на C2):
// если бы поток артефакта шёл через API заявок, контур управления получил бы
// доступ к данным, и утверждение раздела 2 архитектуры стало бы ложным.
// Ссылку со сроком жизни выдать вместо этого нельзя - она предъявительская,
// и скачает её кто угодно. Значит, нужен контейнер, который одновременно
// аутентифицирует получателя и пропускает через себя поток.

var builder = WebApplication.CreateBuilder(args);

var dsn = Environment.GetEnvironmentVariable("SANITIZE_CONTROL_DSN")
          ?? throw new InvalidOperationException("Не задана переменная SANITIZE_CONTROL_DSN");

// Сервис выдачи читает ТОЛЬКО область публикации. Рабочая зона прогона
// ему не видна намеренно: там рядом с дампом лежит словарь соответствий,
// и доступ к нему сделал бы обезличивание обратимым (Р-1).
var published = Environment.GetEnvironmentVariable("SANITIZE_PUBLISH_DIR")
                ?? "/var/lib/sanitize/published";

builder.Services.AddSingleton(new NpgsqlDataSourceBuilder(dsn).Build());

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

async Task Audit(NpgsqlDataSource source, string actor, string action, string subject, string detail)
{
    await using var command = source.CreateCommand(
        "INSERT INTO audit (actor, action, subject, detail) VALUES ($1, $2, $3, $4)");

    command.Parameters.AddWithValue(actor);
    command.Parameters.AddWithValue(action);
    command.Parameters.AddWithValue(subject);
    command.Parameters.AddWithValue(detail);

    await command.ExecuteNonQueryAsync();
}

/// Право на выгрузку проверяется на каждое скачивание, а не один раз при выдаче:
/// отзыв обязан действовать немедленно, иначе он ничего не значит.
async Task<bool> Allowed(NpgsqlDataSource source, string runId, string recipient)
{
    await using var command = source.CreateCommand(
        """
        SELECT 1
        FROM grants g
        JOIN requests r ON r.id = g.request_id
        WHERE r.run_id = $1 AND g.recipient = $2 AND g.revoked_at IS NULL
          AND r.publishable IS TRUE
        """);

    command.Parameters.AddWithValue(runId);
    command.Parameters.AddWithValue(recipient);

    return await command.ExecuteScalarAsync() is not null;
}

app.MapGet("/download/{runId}/passport", async (NpgsqlDataSource source, HttpContext context, string runId) =>
{
    var recipient = context.Request.Headers["X-Subject"].ToString();
    if (recipient.Length == 0) return Results.StatusCode(401);

    if (!await Allowed(source, runId, recipient))
    {
        await Audit(source, recipient, "download.denied", runId, "паспорт");
        return Results.StatusCode(403);
    }

    var path = Path.Combine(published, runId, "passport.json");
    if (!File.Exists(path)) return Results.NotFound();

    await Audit(source, recipient, "download.passport", runId, "");

    return Results.File(path, "application/json", "passport.json");
});

app.MapGet("/download/{runId}/dump", async (NpgsqlDataSource source, HttpContext context, string runId) =>
{
    var recipient = context.Request.Headers["X-Subject"].ToString();
    if (recipient.Length == 0) return Results.StatusCode(401);

    if (!await Allowed(source, runId, recipient))
    {
        await Audit(source, recipient, "download.denied", runId, "дамп");
        return Results.StatusCode(403);
    }

    var storage = Path.Combine(published, runId, "dump");
    if (!Directory.Exists(storage)) return Results.NotFound();

    await Audit(source, recipient, "download.dump", runId, "");

    // Поток собирается на лету и не кладётся на диск: копия артефакта
    // в области выдачи - это ещё одно место, откуда он может утечь.
    context.Response.ContentType = "application/zip";
    context.Response.Headers.ContentDisposition = $"attachment; filename=\"{runId}.zip\"";

    using var archive = new ZipArchive(context.Response.BodyWriter.AsStream(), ZipArchiveMode.Create);

    foreach (var file in Directory.EnumerateFiles(storage, "*", SearchOption.AllDirectories))
    {
        var entryName = Path.GetRelativePath(storage, file);
        archive.CreateEntryFromFile(file, entryName, CompressionLevel.Fastest);
    }

    return Results.Empty;
});

// Ветка DatabaseSink: потока нет вовсе, получателю нужен доступ к готовой базе.
// Поэтому выдача заводит временную учётную запись в санитарной базе и отзывает
// её по тем же правилам, что и доступ к дампу.
app.MapPost("/access/{runId}/database", async (NpgsqlDataSource source, HttpContext context, string runId) =>
{
    var recipient = context.Request.Headers["X-Subject"].ToString();
    if (recipient.Length == 0) return Results.StatusCode(401);

    if (!await Allowed(source, runId, recipient))
    {
        await Audit(source, recipient, "access.denied", runId, "санитарная база");
        return Results.StatusCode(403);
    }

    var targetDsn = Environment.GetEnvironmentVariable("SANITIZE_TARGET_ADMIN_DSN");
    if (string.IsNullOrEmpty(targetDsn))
        return Results.Problem("Доступ к санитарной базе не настроен на этом стенде");

    // Имя учётной записи выводится из заявки и получателя: так подключение
    // в журнале базы однозначно сопоставляется с тем, кому его выдали.
    var login = "consumer_" + Math.Abs(HashCode.Combine(runId, recipient)).ToString();
    var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    await using var admin = new NpgsqlConnection(targetDsn);
    await admin.OpenAsync();

    // Имя роли составляется нами и проверяется по алфавиту, пароль передаётся
    // параметром там, где это возможно. Роль создаётся без права записи:
    // получателю нужна база на чтение, а не на правку.
    if (!login.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        return Results.Problem("Недопустимое имя учётной записи");

    await using (var command = new NpgsqlCommand(
        $"""
         DROP ROLE IF EXISTS "{login}";
         CREATE ROLE "{login}" LOGIN PASSWORD '{password}';
         GRANT CONNECT ON DATABASE target TO "{login}";
         GRANT USAGE ON SCHEMA public TO "{login}";
         GRANT SELECT ON ALL TABLES IN SCHEMA public TO "{login}";
         """, admin))
    {
        await command.ExecuteNonQueryAsync();
    }

    await Audit(source, recipient, "access.database", runId, login);

    return Results.Ok(new
    {
        runId,
        login,
        password,
        note = "Секрет замены получателю не выдаётся ни при каких условиях (Р-1)"
    });
});

app.Run();
