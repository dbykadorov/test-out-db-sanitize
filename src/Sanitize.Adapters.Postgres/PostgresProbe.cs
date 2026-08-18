using Npgsql;
using Sanitize.Core.Classification;
using Sanitize.Core.Policy;

namespace Sanitize.Adapters.Postgres;

/// <summary>Агрегаты по колонке. Их считает СУБД - это её работа, а не наша.</summary>
public sealed record ColumnAggregate
{
    public required ColumnAddress Address { get; init; }
    public required long Rows { get; init; }
    public required long NonNull { get; init; }
    public required long DistinctValues { get; init; }
    public required double AverageLength { get; init; }

    public double NullShare => Rows == 0 ? 0 : (double)(Rows - NonNull) / Rows;
}

/// <summary>
/// Чтение источника: агрегаты, выборки значений, перечисление доменов.
///
/// Реализует способности «потоковое чтение» и «описание структуры» контракта
/// адаптера. Всё знание о синтаксисе запросов заперто здесь.
/// </summary>
public sealed class PostgresProbe
{
    /// <summary>
    /// Порог, выше которого точные агрегаты заменяются выборочными.
    ///
    /// Точный COUNT(DISTINCT) по миллиардам строк - это часы. Требование F-10
    /// разрешает выборку не менее 100 000 значений, и порог выставлен так, чтобы
    /// на объёмах стенда считалось точно, а на объёмах A-1 - выборочно.
    /// </summary>
    public const long ExactAggregateLimit = 5_000_000;

    private readonly string _connectionString;

    public PostgresProbe(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        _connectionString = connectionString;
    }

    public async Task<NpgsqlConnection> ConnectAsync(CancellationToken token)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        return connection;
    }

    private static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string QualifiedTable(ColumnAddress address) =>
        Quote(address.Schema) + "." + Quote(address.Table);

    /// <summary>Оценка числа строк по каталогу: точный счёт здесь не нужен.</summary>
    public async Task<long> EstimateRowsAsync(string schema, string table, CancellationToken token)
    {
        await using var connection = await ConnectAsync(token).ConfigureAwait(false);

        const string sql = """
            SELECT GREATEST(c.reltuples, 0)::bigint
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);

        var value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        return value is long rows ? rows : 0;
    }

    public async Task<long> CountRowsAsync(string schema, string table, CancellationToken token)
    {
        await using var connection = await ConnectAsync(token).ConfigureAwait(false);

        var sql = $"SELECT count(*) FROM {Quote(schema)}.{Quote(table)}";
        await using var command = new NpgsqlCommand(sql, connection);

        var value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        return value is long rows ? rows : 0;
    }

    /// <summary>
    /// Очищает приёмник перед восстановлением.
    ///
    /// Без этого pg_restore натыкается на уже существующие объекты, пишет
    /// «relation already exists» и НЕ загружает данные - при нулевом коде
    /// возврата. Проверки тогда идут по результату предыдущего прогона,
    /// и прогон выглядит успешным, ничего не изменив. Это ровно тот случай,
    /// когда молчаливый успех опаснее громкого отказа.
    ///
    /// Действие разрушительное и относится только к санитарной базе -
    /// к области публикации, которую этот же прогон и создаёт заново.
    /// К источнику у прогона доступ только на чтение.
    /// </summary>
    public async Task ResetSchemaAsync(string schema, CancellationToken token)
    {
        await using var connection = await ConnectAsync(token).ConfigureAwait(false);

        var name = Quote(schema);
        var sql = $"DROP SCHEMA IF EXISTS {name} CASCADE; CREATE SCHEMA {name};";

        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 600 };
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    public async Task<ColumnAggregate> AggregateAsync(ColumnAddress address, CancellationToken token)
    {
        await using var connection = await ConnectAsync(token).ConfigureAwait(false);

        var column = Quote(address.Column);
        var sql = $"""
            SELECT count(*),
                   count({column}),
                   count(DISTINCT {column}::text),
                   COALESCE(avg(length({column}::text)), 0)
            FROM {QualifiedTable(address)}
            """;

        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 600 };
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

        if (!await reader.ReadAsync(token).ConfigureAwait(false))
            throw new InvalidOperationException($"{address.Qualified}: агрегаты не получены");

        return new ColumnAggregate
        {
            Address = address,
            Rows = reader.GetInt64(0),
            NonNull = reader.GetInt64(1),
            DistinctValues = reader.GetInt64(2),
            AverageLength = (double)reader.GetDecimal(3)
        };
    }

    /// <summary>Выборка различных значений колонки. Не покидает контур.</summary>
    public async Task<IReadOnlyList<string>> SampleAsync(
        ColumnAddress address, int limit, CancellationToken token)
    {
        await using var connection = await ConnectAsync(token).ConfigureAwait(false);

        var column = Quote(address.Column);
        var sql = $"""
            SELECT DISTINCT {column}::text
            FROM {QualifiedTable(address)}
            WHERE {column} IS NOT NULL
            LIMIT {limit}
            """;

        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 600 };
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

        var values = new List<string>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            if (!reader.IsDBNull(0))
                values.Add(reader.GetString(0));

        return values;
    }

    /// <summary>
    /// Потоковое перечисление различных значений колонки - вход для построения
    /// словаря. Курсор серверный: память не зависит от объёма (P-3).
    /// </summary>
    public async IAsyncEnumerable<string> DistinctValuesAsync(
        ColumnAddress address,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        await using var connection = await ConnectAsync(token).ConfigureAwait(false);

        var column = Quote(address.Column);
        var sql = $"""
            SELECT DISTINCT {column}::text
            FROM {QualifiedTable(address)}
            WHERE {column} IS NOT NULL
            ORDER BY 1
            """;

        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 3600 };
        await using var reader = await command.ExecuteReaderAsync(
            System.Data.CommandBehavior.SequentialAccess, token).ConfigureAwait(false);

        while (await reader.ReadAsync(token).ConfigureAwait(false))
            if (!reader.IsDBNull(0))
                yield return reader.GetString(0);
    }

    /// <summary>
    /// Сколько значений из перечня встречается в колонке. Основа проверки
    /// полноты (F-4): если хоть одно исходное значение уцелело, прогон провален.
    /// </summary>
    public async Task<long> CountMatchingAsync(
        ColumnAddress address, IReadOnlyList<string> values, CancellationToken token)
    {
        if (values.Count == 0) return 0;

        await using var connection = await ConnectAsync(token).ConfigureAwait(false);

        var column = Quote(address.Column);
        var sql = $"""
            SELECT count(*)
            FROM {QualifiedTable(address)}
            WHERE {column}::text = ANY(@values)
            """;

        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 600 };
        command.Parameters.AddWithValue("values", values.ToArray());

        var result = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        return result is long count ? count : 0;
    }

    /// <summary>
    /// Сколько строк содержит любое из значений как подстроку. Нужно для текста:
    /// затирание обязано убрать вкрапления, а не только совпадения целиком.
    /// </summary>
    public async Task<long> CountContainingAsync(
        ColumnAddress address, IReadOnlyList<string> values, CancellationToken token)
    {
        if (values.Count == 0) return 0;

        await using var connection = await ConnectAsync(token).ConfigureAwait(false);

        var column = Quote(address.Column);
        var sql = $"""
            SELECT count(*)
            FROM {QualifiedTable(address)} t
            WHERE EXISTS (
                SELECT 1 FROM unnest(@values) AS v(needle)
                WHERE v.needle <> '' AND position(v.needle IN t.{column}::text) > 0
            )
            """;

        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 1800 };
        command.Parameters.AddWithValue("values", values.ToArray());

        var result = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        return result is long count ? count : 0;
    }

    /// <summary>
    /// Пары «значение источника - значение результата» по первичному ключу.
    /// Ими проверяется единственность замены (F-7) между разными колонками.
    /// </summary>
    public async Task<IReadOnlyList<(string Key, string Value)>> KeyedValuesAsync(
        ColumnAddress address, string keyColumn, int limit, CancellationToken token)
    {
        await using var connection = await ConnectAsync(token).ConfigureAwait(false);

        var column = Quote(address.Column);
        var key = Quote(keyColumn);
        var sql = $"""
            SELECT {key}::text, {column}::text
            FROM {QualifiedTable(address)}
            WHERE {column} IS NOT NULL
            ORDER BY {key}
            LIMIT {limit}
            """;

        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 600 };
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

        var pairs = new List<(string, string)>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            pairs.Add((reader.GetString(0), reader.GetString(1)));

        return pairs;
    }

    /// <summary>Признаки колонки для стадии анализа.</summary>
    public async Task<ColumnFeatures> ProfileAsync(
        ColumnDescription column, int sampleSize, CancellationToken token)
    {
        var aggregate = await AggregateAsync(column.Address, token).ConfigureAwait(false);
        var sample = await SampleAsync(column.Address, sampleSize, token).ConfigureAwait(false);

        return new ColumnFeatures
        {
            Address = column.Address,
            DataType = column.DataType,
            MaxLength = column.MaxLength,
            Comment = column.Comment,
            IsUnique = column.IsUnique,
            IsPrimaryKey = column.IsPrimaryKey,
            IsForeignKey = column.IsForeignKey,
            FiniteDomain = column.FiniteDomain,
            Rows = aggregate.Rows,
            DistinctValues = aggregate.DistinctValues,
            NullShare = aggregate.NullShare,
            AverageLength = aggregate.AverageLength,
            Sample = sample
        };
    }
}
