using Sanitize.Core.Policy;

namespace Sanitize.Core.Planning;

/// <summary>
/// Домен значений, для которого нужен словарь прогона.
///
/// Домен собирается по семантическому типу, а не по колонке: значение,
/// встреченное в нескольких колонках, обязано получить одну замену (F-7),
/// а колонка с внешним ключом обязана попасть в тот же домен, что и цель
/// ссылки, иначе связь порвётся (F-8).
/// </summary>
public sealed record DictionaryDomain
{
    public required string SemanticType { get; init; }

    /// <summary>Колонки, значения которых образуют домен.</summary>
    public required IReadOnlyList<ColumnAddress> Columns { get; init; }

    /// <summary>
    /// Домен ограничен схемой: замена обязана остаться внутри множества
    /// исходных значений, поэтому строится беспорядок, а не свободная замена.
    /// </summary>
    public required bool Constrained { get; init; }

    /// <summary>Значения ограниченного домена, если он задан схемой.</summary>
    public IReadOnlyList<string> AllowedValues { get; init; } = Array.Empty<string>();
}

public sealed record TablePlan
{
    public required string Schema { get; init; }
    public required string Table { get; init; }
    public required IReadOnlyList<PolicyColumn> Columns { get; init; }

    public string Qualified => $"{Schema}.{Table}";
}

/// <summary>
/// План исполнения: что перечисляется в словарь, что считается функцией,
/// какие таблицы вообще трогаются.
///
/// План выводится из политики детерминированно и проверяем до запуска -
/// именно этим он отличается от «разберёмся по ходу».
/// </summary>
public sealed class RunPlan
{
    private RunPlan(
        IReadOnlyList<TablePlan> tables,
        IReadOnlyList<DictionaryDomain> domains,
        IReadOnlyList<string> untouchedReasons)
    {
        Tables = tables;
        Domains = domains;
        UntouchedReasons = untouchedReasons;
    }

    public IReadOnlyList<TablePlan> Tables { get; }

    public IReadOnlyList<DictionaryDomain> Domains { get; }

    /// <summary>Почему остальные колонки остаются как есть. Нужен паспорту.</summary>
    public IReadOnlyList<string> UntouchedReasons { get; }

    public static RunPlan From(RunPolicy policy, IReadOnlyDictionary<string, IReadOnlyList<string>> finiteDomains)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(finiteDomains);

        var problems = policy.Problems();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "Политика не утверждена, план строить нельзя:\n  " + string.Join("\n  ", problems));
        }

        var byTable = new Dictionary<string, List<PolicyColumn>>(StringComparer.Ordinal);
        var byDomain = new Dictionary<string, List<ColumnAddress>>(StringComparer.Ordinal);
        var constrained = new HashSet<string>(StringComparer.Ordinal);
        var allowed = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var untouched = new List<string>();

        foreach (var column in policy.Columns)
        {
            if (column.Strategy == ReplacementStrategy.None)
            {
                if (column.Sensitive)
                    untouched.Add($"{column.Address.Qualified}: {column.Reason}");

                continue;
            }

            var table = column.Address.TableQualified;
            if (!byTable.TryGetValue(table, out var columns))
            {
                columns = new List<PolicyColumn>();
                byTable[table] = columns;
            }

            columns.Add(column);

            if (column.Strategy is not (ReplacementStrategy.Dictionary or ReplacementStrategy.Derangement))
                continue;

            if (!byDomain.TryGetValue(column.SemanticType, out var members))
            {
                members = new List<ColumnAddress>();
                byDomain[column.SemanticType] = members;
            }

            members.Add(column.Address);

            if (column.Strategy != ReplacementStrategy.Derangement) continue;

            constrained.Add(column.SemanticType);

            // Множество допустимых значений задаёт схема, а не мы. Если ограничение
            // объявлено, а значений нет - это ошибка анализа, и молча продолжать
            // нельзя: замена ушла бы за пределы домена и уронила восстановление.
            if (!finiteDomains.TryGetValue(column.Address.Qualified, out var values) || values.Count == 0)
            {
                throw new InvalidOperationException(
                    $"{column.Address.Qualified}: назначен беспорядок в конечном домене, " +
                    "но множество допустимых значений не определено");
            }

            allowed[column.SemanticType] = values;
        }

        var tables = new List<TablePlan>();
        foreach (var (qualified, columns) in byTable)
        {
            var parts = qualified.Split('.', 2);
            tables.Add(new TablePlan { Schema = parts[0], Table = parts[1], Columns = columns });
        }

        // Порядок фиксирован: план должен быть побайтно одинаков между прогонами,
        // иначе его отпечаток в паспорте ничего не значит.
        tables.Sort((a, b) => string.CompareOrdinal(a.Qualified, b.Qualified));

        var domains = new List<DictionaryDomain>();
        foreach (var (type, members) in byDomain)
        {
            domains.Add(new DictionaryDomain
            {
                SemanticType = type,
                Columns = members,
                Constrained = constrained.Contains(type),
                AllowedValues = allowed.TryGetValue(type, out var values) ? values : Array.Empty<string>()
            });
        }

        domains.Sort((a, b) => string.CompareOrdinal(a.SemanticType, b.SemanticType));
        untouched.Sort(StringComparer.Ordinal);

        return new RunPlan(tables, domains, untouched);
    }
}
