using Npgsql;

namespace Sanitize.Adapters.Postgres;

/// <summary>
/// Перевод строки подключения между двумя форматами.
///
/// Их действительно два, и они несовместимы: Npgsql разбирает строку
/// по точке с запятой (`Host=db;Port=5432`), а pg_dump и pg_restore, которые
/// вызывает Greenmask, ждут формат libpq через пробел (`host=db port=5432`).
/// Строка одного формата, поданная в другой, не падает с внятной ошибкой:
/// она молча уезжает целиком в имя хоста, и разбираться приходится
/// по «Name or service not known».
///
/// Поэтому контур везде хранит формат Npgsql, а перевод для внешних программ
/// делается здесь и только здесь.
/// </summary>
public static class PostgresDsn
{
    public static string ToLibpq(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var parts = new List<string>();

        void Add(string key, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;

            // Экранирование по правилам libpq: одинарная кавычка и обратный слэш.
            var escaped = value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("'", "\\'", StringComparison.Ordinal);

            parts.Add($"{key}='{escaped}'");
        }

        Add("host", builder.Host);
        Add("port", builder.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add("user", builder.Username);
        Add("password", builder.Password);
        Add("dbname", builder.Database);

        return string.Join(' ', parts);
    }
}
