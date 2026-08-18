using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using Sanitize.Core.Classification;
using Sanitize.Core.Policy;

namespace Sanitize.Adapters.Postgres;

/// <summary>Описание колонки так, как его видит адаптер источника.</summary>
public sealed record ColumnDescription
{
    public required ColumnAddress Address { get; init; }
    public required string DataType { get; init; }
    public required int Position { get; init; }
    public int? MaxLength { get; init; }
    public bool Nullable { get; init; }
    public string Comment { get; init; } = "";
    public bool IsPrimaryKey { get; init; }
    public bool IsUnique { get; init; }
    public bool IsForeignKey { get; init; }
    public IReadOnlyList<string> FiniteDomain { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Реализация способности «описание структуры» из контракта адаптера
/// (раздел 4a архитектуры).
///
/// Здесь и только здесь живёт знание о том, что источник - PostgreSQL. Ядро
/// получает <see cref="ColumnFeatures"/> и о существовании каталога не знает.
/// </summary>
public sealed class SchemaIntrospector
{
    private readonly string _connectionString;
    private readonly string[] _schemas;

    public SchemaIntrospector(string connectionString, params string[] schemas)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        _connectionString = connectionString;
        _schemas = schemas.Length > 0 ? schemas : new[] { "public" };
    }

    public async Task<IReadOnlyList<ColumnDescription>> DescribeAsync(CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);

        var columns = await ReadColumnsAsync(connection, token).ConfigureAwait(false);
        var keys = await ReadKeysAsync(connection, token).ConfigureAwait(false);
        var domains = await ReadFiniteDomainsAsync(connection, token).ConfigureAwait(false);

        var described = new List<ColumnDescription>(columns.Count);

        foreach (var column in columns)
        {
            var address = column.Address.Qualified;

            described.Add(column with
            {
                IsPrimaryKey = keys.PrimaryKeys.Contains(address),
                IsUnique = keys.Uniques.Contains(address),
                IsForeignKey = keys.ForeignKeys.Contains(address),
                FiniteDomain = domains.TryGetValue(address, out var values)
                    ? values
                    : Array.Empty<string>()
            });
        }

        return described;
    }

    private async Task<List<ColumnDescription>> ReadColumnsAsync(
        NpgsqlConnection connection, CancellationToken token)
    {
        const string sql = """
            SELECT n.nspname,
                   c.relname,
                   a.attname,
                   format_type(a.atttypid, a.atttypmod)          AS data_type,
                   a.attnum,
                   a.attnotnull,
                   COALESCE(col_description(c.oid, a.attnum), '') AS comment,
                   CASE WHEN a.atttypmod > 4 THEN a.atttypmod - 4 END AS max_length
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid
            WHERE c.relkind = 'r'
              AND a.attnum > 0
              AND NOT a.attisdropped
              AND n.nspname = ANY(@schemas)
            ORDER BY n.nspname, c.relname, a.attnum
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schemas", _schemas);

        var columns = new List<ColumnDescription>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            columns.Add(new ColumnDescription
            {
                Address = new ColumnAddress(reader.GetString(0), reader.GetString(1), reader.GetString(2)),
                DataType = reader.GetString(3),
                Position = reader.GetInt16(4),
                Nullable = !reader.GetBoolean(5),
                Comment = reader.GetString(6),
                MaxLength = reader.IsDBNull(7) ? null : reader.GetInt32(7)
            });
        }

        return columns;
    }

    private sealed record KeySets(
        HashSet<string> PrimaryKeys, HashSet<string> Uniques, HashSet<string> ForeignKeys);

    private async Task<KeySets> ReadKeysAsync(NpgsqlConnection connection, CancellationToken token)
    {
        // Уникальные индексы учитываются наравне с ограничениями: биекция нужна
        // и там, где уникальность объявлена индексом, а не CONSTRAINT.
        const string sql = """
            SELECT n.nspname, c.relname, a.attname, con.contype::text
            FROM pg_constraint con
            JOIN pg_class c ON c.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN LATERAL unnest(con.conkey) AS k(attnum) ON TRUE
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = k.attnum
            WHERE con.contype IN ('p', 'u', 'f') AND n.nspname = ANY(@schemas)

            UNION ALL

            SELECT n.nspname, c.relname, a.attname, 'u'::text
            FROM pg_index i
            JOIN pg_class c ON c.oid = i.indrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN LATERAL unnest(i.indkey) AS k(attnum) ON TRUE
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = k.attnum
            WHERE i.indisunique AND n.nspname = ANY(@schemas)
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schemas", _schemas);

        var primary = new HashSet<string>(StringComparer.Ordinal);
        var unique = new HashSet<string>(StringComparer.Ordinal);
        var foreign = new HashSet<string>(StringComparer.Ordinal);

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var address = new ColumnAddress(
                reader.GetString(0), reader.GetString(1), reader.GetString(2)).Qualified;

            switch (reader.GetString(3))
            {
                case "p": primary.Add(address); unique.Add(address); break;
                case "u": unique.Add(address); break;
                case "f": foreign.Add(address); break;
            }
        }

        return new KeySets(primary, unique, foreign);
    }

    /// <summary>Литералы внутри определения ограничения: 'new'::character varying.</summary>
    private static readonly Regex Literal = new(@"'([^']*)'::", RegexOptions.CultureInvariant);

    /// <summary>
    /// Конечные домены, заданные ограничением CHECK.
    ///
    /// Разбор идёт по литералам определения, а не по разбору выражения целиком:
    /// полный разбор грамматики ограничений - отдельная задача, и делать её
    /// наполовину опаснее, чем не делать. Поэтому правило узкое и названное:
    /// ограничение вида «колонка входит в перечень литералов». Всё остальное
    /// конечным доменом не считается, и колонка идёт обычным путём.
    /// </summary>
    private async Task<Dictionary<string, IReadOnlyList<string>>> ReadFiniteDomainsAsync(
        NpgsqlConnection connection, CancellationToken token)
    {
        const string sql = """
            SELECT n.nspname, c.relname, a.attname, pg_get_constraintdef(con.oid)
            FROM pg_constraint con
            JOIN pg_class c ON c.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN LATERAL unnest(con.conkey) AS k(attnum) ON TRUE
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = k.attnum
            WHERE con.contype = 'c'
              AND cardinality(con.conkey) = 1
              AND n.nspname = ANY(@schemas)
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schemas", _schemas);

        var domains = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var address = new ColumnAddress(
                reader.GetString(0), reader.GetString(1), reader.GetString(2)).Qualified;

            var definition = reader.GetString(3);
            if (!definition.Contains("ANY", StringComparison.Ordinal)) continue;

            var values = new List<string>();
            foreach (Match match in Literal.Matches(definition)) values.Add(match.Groups[1].Value);

            if (values.Count > 0) domains[address] = values;
        }

        return domains;
    }

    /// <summary>
    /// Отпечаток схемы. Его расхождение между анализом и исполнением
    /// останавливает прогон: политика привязана к структуре, а не к имени базы.
    /// </summary>
    public static string FingerprintOf(IReadOnlyList<ColumnDescription> columns)
    {
        var builder = new StringBuilder();

        foreach (var column in columns.OrderBy(c => c.Address.Qualified, StringComparer.Ordinal))
        {
            builder.Append(column.Address.Qualified).Append('|')
                   .Append(column.DataType).Append('|')
                   .Append(column.Nullable ? '?' : '!').Append('|')
                   .Append(column.IsPrimaryKey ? 'p' : '-')
                   .Append(column.IsUnique ? 'u' : '-')
                   .Append(column.IsForeignKey ? 'f' : '-').Append('|')
                   .Append(column.Comment).Append('|')
                   .Append(string.Join(',', column.FiniteDomain))
                   .Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
