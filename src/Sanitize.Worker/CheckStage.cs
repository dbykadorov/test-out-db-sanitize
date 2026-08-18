using Sanitize.Adapters.Postgres;
using Sanitize.Core.Planning;
using Sanitize.Core.Policy;
using Sanitize.Core.Validation;
using Sanitize.Core.Values;

namespace Sanitize.Worker;

public sealed record CheckResult
{
    public required string Requirement { get; init; }
    public required string Name { get; init; }
    public required bool Passed { get; init; }
    public required string Detail { get; init; }

    /// <summary>
    /// Провал блокирующей проверки означает, что артефакт не публикуется.
    /// Неблокирующая проверка описывает названную и принятую плату.
    /// </summary>
    public bool Blocking { get; init; } = true;
}

/// <summary>
/// Стадия 4: проверка. Прогон не считается успешным, пока проверки не пройдены.
///
/// Частично обезличенная база опаснее отсутствующей, поэтому провал блокирующей
/// проверки не помечает артефакт «с замечаниями», а запрещает публикацию.
/// </summary>
public sealed class CheckStage
{
    /// <summary>
    /// Допустимое отклонение мощности колонки. F-10 требует сохранения
    /// разнообразия, а не побитового совпадения: замены могут столкнуться,
    /// и небольшая потеря различных значений ожидаема.
    /// </summary>
    private const double CardinalityTolerance = 0.05;

    private readonly RunSettings _settings;
    private readonly RunLog _log;
    private readonly PostgresProbe _source;
    private readonly PostgresProbe _target;

    public CheckStage(RunSettings settings, RunLog log)
    {
        _settings = settings;
        _log = log;
        _source = new PostgresProbe(settings.SourceDsn);
        _target = new PostgresProbe(settings.TargetDsn);
    }

    public async Task<IReadOnlyList<CheckResult>> RunAsync(
        RunPolicy policy,
        RunPlan plan,
        IReadOnlyList<ColumnDescription> schema,
        CancellationToken token)
    {
        var results = new List<CheckResult>
        {
            await SchemaAsync(policy, token).ConfigureAwait(false)
        };

        results.AddRange(await VolumeAsync(schema, token).ConfigureAwait(false));
        results.AddRange(await CompletenessAsync(policy, schema, token).ConfigureAwait(false));
        results.AddRange(await TextCompletenessAsync(policy, token).ConfigureAwait(false));
        results.AddRange(await ConsistencyAsync(plan, schema, token).ConfigureAwait(false));
        results.AddRange(await DerangementAsync(policy, schema, token).ConfigureAwait(false));
        results.AddRange(await DiversityAsync(policy, token).ConfigureAwait(false));
        results.AddRange(await PlausibilityAsync(policy, token).ConfigureAwait(false));

        var failed = 0;
        foreach (var result in results)
            if (!result.Passed && result.Blocking)
                failed++;

        _log.Write($"проверки: всего {results.Count}, провалено блокирующих {failed}");

        return results;
    }

    /// <summary>F-11: схема результата совпадает со схемой источника.</summary>
    private async Task<CheckResult> SchemaAsync(RunPolicy policy, CancellationToken token)
    {
        var introspector = new SchemaIntrospector(_settings.TargetDsn, _settings.Schemas);
        var target = await introspector.DescribeAsync(token).ConfigureAwait(false);
        var fingerprint = SchemaIntrospector.FingerprintOf(target);

        var same = string.Equals(fingerprint, policy.SchemaFingerprint, StringComparison.Ordinal);

        return new CheckResult
        {
            Requirement = "F-11",
            Name = "схема сохранена",
            Passed = same,
            Detail = same
                ? $"отпечаток совпал: {fingerprint[..16]}"
                : $"отпечаток источника {policy.SchemaFingerprint[..16]}, результата {fingerprint[..16]}"
        };
    }

    /// <summary>F-9: число строк по каждой таблице совпадает.</summary>
    private async Task<IReadOnlyList<CheckResult>> VolumeAsync(
        IReadOnlyList<ColumnDescription> schema, CancellationToken token)
    {
        var tables = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var column in schema) tables.Add(column.Address.TableQualified);

        var results = new List<CheckResult>();

        foreach (var qualified in tables)
        {
            var parts = qualified.Split('.', 2);

            var before = await _source.CountRowsAsync(parts[0], parts[1], token).ConfigureAwait(false);
            var after = await _target.CountRowsAsync(parts[0], parts[1], token).ConfigureAwait(false);

            results.Add(new CheckResult
            {
                Requirement = "F-9",
                Name = $"объём: {qualified}",
                Passed = before == after,
                Detail = $"строк было {before}, стало {after}"
            });
        }

        return results;
    }

    /// <summary>
    /// F-4 для типизированных колонок: ни одно значение не осталось собой.
    ///
    /// Сравнение построчное, по первичному ключу. Проверять «ни одно исходное
    /// значение не встречается в результате» нельзя: замена законно может
    /// совпасть со значением ДРУГОЙ строки - у даты рождения или города пул
    /// замен и исходные значения живут в одном и том же пространстве, а у
    /// беспорядка в конечном домене они совпадают по построению. Такая проверка
    /// объявляла бы дефектом ровно то поведение, которого требует Р-3.
    ///
    /// Порог здесь сто процентов и без исключений: известно, где лежит
    /// значение, и не заменить его - это дефект, а не статистика.
    /// </summary>
    private async Task<IReadOnlyList<CheckResult>> CompletenessAsync(
        RunPolicy policy, IReadOnlyList<ColumnDescription> schema, CancellationToken token)
    {
        var keys = PrimaryKeys(schema);
        var results = new List<CheckResult>();

        foreach (var column in policy.Columns)
        {
            if (!column.Sensitive || column.Strategy == ReplacementStrategy.None) continue;
            if (column.Mode != ColumnMode.Typed) continue;

            if (keys.TryGetValue(column.Address.TableQualified, out var key))
            {
                var (compared, unchanged) = await FixedPointsAsync(column.Address, key, token)
                    .ConfigureAwait(false);

                results.Add(new CheckResult
                {
                    Requirement = "F-4",
                    Name = $"полнота: {column.Address.Qualified}",
                    Passed = unchanged == 0,
                    Detail = unchanged == 0
                        ? $"на {compared} строках значение не осталось собой ни разу"
                        : $"значение осталось собой в {unchanged} строках из {compared}"
                });

                continue;
            }

            // Без простого первичного ключа строки не сопоставить. Тогда
            // остаётся более грубая проверка по множеству значений - и она
            // применима только там, где пул замен заведомо не пересекается
            // с исходными значениями.
            var corpus = await _source
                .SampleAsync(column.Address, _settings.CorpusSize, token).ConfigureAwait(false);

            var survived = await _target
                .CountMatchingAsync(column.Address, corpus, token).ConfigureAwait(false);

            results.Add(new CheckResult
            {
                Requirement = "F-4",
                Name = $"полнота: {column.Address.Qualified}",
                Passed = survived == 0,
                Detail = (survived == 0
                             ? $"из {corpus.Count} исходных значений не уцелело ни одного"
                             : $"уцелело строк: {survived} из корпуса в {corpus.Count} значений") +
                         "; сравнение по множеству значений: у таблицы нет простого первичного ключа"
            });
        }

        return results;
    }

    /// <summary>Сколько строк сохранили исходное значение. Сопоставление по ключу.</summary>
    private async Task<(long Compared, long Unchanged)> FixedPointsAsync(
        ColumnAddress address, string key, CancellationToken token)
    {
        var before = await _source
            .KeyedValuesAsync(address, key, _settings.CorpusSize, token).ConfigureAwait(false);

        var after = await _target
            .KeyedValuesAsync(address, key, _settings.CorpusSize, token).ConfigureAwait(false);

        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rowKey, value) in after) byKey[rowKey] = value;

        long compared = 0;
        long unchanged = 0;

        foreach (var (rowKey, sourceValue) in before)
        {
            if (!byKey.TryGetValue(rowKey, out var targetValue)) continue;

            compared++;
            if (string.Equals(sourceValue, targetValue, StringComparison.Ordinal)) unchanged++;
        }

        return (compared, unchanged);
    }

    /// <summary>
    /// F-4 для текста и документов: исходные значения не встречаются
    /// подстрокой. Проверяется по эталонному корпусу - по значениям,
    /// про которые ТОЧНО известно, что они персональные данные.
    /// </summary>
    private async Task<IReadOnlyList<CheckResult>> TextCompletenessAsync(
        RunPolicy policy, CancellationToken token)
    {
        var corpus = new List<string>();

        foreach (var column in policy.Columns)
        {
            if (!column.Sensitive || column.Mode != ColumnMode.Typed) continue;
            if (column.SemanticType is "marital_status" or "city") continue;

            // Корпус небольшой намеренно: проверка подстрокой квадратична,
            // и на объёмах A-1 её считают на выборке, а не на всей базе.
            var values = await _source
                .SampleAsync(column.Address, 200, token).ConfigureAwait(false);

            foreach (var value in values)
                if (value.Length >= 6)
                    corpus.Add(value);
        }

        var results = new List<CheckResult>();

        foreach (var column in policy.Columns)
        {
            if (!column.Sensitive) continue;
            if (column.Mode is not (ColumnMode.TextWipe or ColumnMode.Document)) continue;

            var found = await _target
                .CountContainingAsync(column.Address, corpus, token).ConfigureAwait(false);

            results.Add(new CheckResult
            {
                Requirement = "F-4a",
                Name = $"вкрапления: {column.Address.Qualified}",
                Passed = found == 0,
                Detail = found == 0
                    ? $"ни одно из {corpus.Count} значений корпуса не найдено подстрокой"
                    : $"найдено строк с исходными значениями: {found}"
            });
        }

        return results;
    }

    /// <summary>
    /// F-7: одно исходное значение заменяется одинаково везде.
    ///
    /// Проверка идёт по паре «источник - результат», сопоставленных первичным
    /// ключом: он не заменяется, поэтому строки сравнимы. Счётчики внутри
    /// одного воркера не доказали бы ничего - воркеры видят разные диапазоны.
    /// </summary>
    private async Task<IReadOnlyList<CheckResult>> ConsistencyAsync(
        RunPlan plan, IReadOnlyList<ColumnDescription> schema, CancellationToken token)
    {
        var keys = PrimaryKeys(schema);
        var results = new List<CheckResult>();

        foreach (var domain in plan.Domains)
        {
            var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
            var conflicts = 0;
            var compared = 0;
            var skipped = new List<string>();

            foreach (var address in domain.Columns)
            {
                if (!keys.TryGetValue(address.TableQualified, out var key))
                {
                    skipped.Add(address.Qualified);
                    continue;
                }

                var before = await _source
                    .KeyedValuesAsync(address, key, _settings.CorpusSize, token).ConfigureAwait(false);

                var after = await _target
                    .KeyedValuesAsync(address, key, _settings.CorpusSize, token).ConfigureAwait(false);

                var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (rowKey, value) in after) byKey[rowKey] = value;

                foreach (var (rowKey, sourceValue) in before)
                {
                    if (!byKey.TryGetValue(rowKey, out var targetValue)) continue;

                    compared++;

                    if (mapping.TryGetValue(sourceValue, out var known))
                    {
                        if (!string.Equals(known, targetValue, StringComparison.Ordinal)) conflicts++;
                    }
                    else
                    {
                        mapping[sourceValue] = targetValue;
                    }
                }
            }

            var detail = $"сопоставлено значений {compared}, расхождений {conflicts}";
            if (skipped.Count > 0)
                detail += $"; вне сравнения (нет простого первичного ключа): {string.Join(", ", skipped)}";

            results.Add(new CheckResult
            {
                Requirement = "F-7",
                Name = $"единственность замены: домен {domain.SemanticType}",
                Passed = conflicts == 0,
                Detail = detail
            });
        }

        return results;
    }

    /// <summary>Р-3: в конечном домене не осталось неподвижных точек.</summary>
    private async Task<IReadOnlyList<CheckResult>> DerangementAsync(
        RunPolicy policy, IReadOnlyList<ColumnDescription> schema, CancellationToken token)
    {
        var keys = PrimaryKeys(schema);
        var results = new List<CheckResult>();

        foreach (var column in policy.Columns)
        {
            if (column.Strategy != ReplacementStrategy.Derangement) continue;
            if (!keys.TryGetValue(column.Address.TableQualified, out var key)) continue;

            var before = await _source
                .KeyedValuesAsync(column.Address, key, _settings.CorpusSize, token).ConfigureAwait(false);

            var after = await _target
                .KeyedValuesAsync(column.Address, key, _settings.CorpusSize, token).ConfigureAwait(false);

            var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (rowKey, value) in after) byKey[rowKey] = value;

            var fixedPoints = 0;
            var compared = 0;

            foreach (var (rowKey, sourceValue) in before)
            {
                if (!byKey.TryGetValue(rowKey, out var targetValue)) continue;

                compared++;
                if (string.Equals(sourceValue, targetValue, StringComparison.Ordinal)) fixedPoints++;
            }

            results.Add(new CheckResult
            {
                Requirement = "Р-3",
                Name = $"беспорядок: {column.Address.Qualified}",
                Passed = fixedPoints == 0,
                Detail = fixedPoints == 0
                    ? $"на {compared} строках значение не осталось собой ни разу"
                    : $"значение осталось собой в {fixedPoints} строках из {compared}"
            });
        }

        return results;
    }

    /// <summary>F-10: мощность и доля пустых сохранены.</summary>
    private async Task<IReadOnlyList<CheckResult>> DiversityAsync(
        RunPolicy policy, CancellationToken token)
    {
        var results = new List<CheckResult>();

        foreach (var column in policy.Columns)
        {
            if (!column.Sensitive || column.Strategy == ReplacementStrategy.None) continue;

            var before = await _source.AggregateAsync(column.Address, token).ConfigureAwait(false);
            var after = await _target.AggregateAsync(column.Address, token).ConfigureAwait(false);

            var lost = before.DistinctValues == 0
                ? 0
                : 1 - (double)after.DistinctValues / before.DistinctValues;

            var nullsMatch = Math.Abs(before.NullShare - after.NullShare) < 1e-9;

            // Затирание текста заведомо теряет разнообразие: на месте тысяч
            // разных записей оказывается пул синтетических. Это названная
            // в F-4a плата, а не дефект, поэтому проверка неблокирующая -
            // но она всё равно выполняется и попадает в паспорт.
            var wiped = column.Mode is ColumnMode.TextWipe;

            results.Add(new CheckResult
            {
                Requirement = "F-10",
                Name = $"разнообразие: {column.Address.Qualified}",
                Passed = nullsMatch && (wiped || lost <= CardinalityTolerance),
                Blocking = !wiped,
                Detail = $"различных было {before.DistinctValues}, стало {after.DistinctValues} " +
                         $"(потеря {lost * 100:F1}%), доля пустых {before.NullShare:F4} и {after.NullShare:F4}" +
                         (wiped ? "; потеря ожидаема: колонка затирается целиком (F-4a)" : "")
            });
        }

        return results;
    }

    /// <summary>F-5: значения результата проходят валидаторы своего типа.</summary>
    private async Task<IReadOnlyList<CheckResult>> PlausibilityAsync(
        RunPolicy policy, CancellationToken token)
    {
        var validator = ValueValidator.Default();
        var results = new List<CheckResult>();

        foreach (var column in policy.Columns)
        {
            if (!column.Sensitive || column.Mode != ColumnMode.Typed) continue;
            if (column.Strategy == ReplacementStrategy.None) continue;

            var type = SemanticTypeId.Of(column.SemanticType);
            if (!validator.Covers(type)) continue;

            var sample = await _target
                .SampleAsync(column.Address, 500, token).ConfigureAwait(false);

            var bad = validator.Validate(type, sample);

            results.Add(new CheckResult
            {
                Requirement = "F-5",
                Name = $"правдоподобность: {column.Address.Qualified}",
                Passed = bad.Count == 0,
                Detail = bad.Count == 0
                    ? $"все {sample.Count} значений выборки прошли валидатор типа {type}"
                    : $"не прошли валидатор: {bad.Count} значений"
            });
        }

        return results;
    }

    private static Dictionary<string, string> PrimaryKeys(IReadOnlyList<ColumnDescription> schema)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var column in schema)
        {
            if (!column.IsPrimaryKey) continue;

            var table = column.Address.TableQualified;
            counts[table] = counts.GetValueOrDefault(table) + 1;
            keys[table] = column.Address.Column;
        }

        // Составной ключ сюда не годится: сопоставление строк по одной колонке
        // было бы неоднозначным, и проверка врала бы. Такие таблицы честно
        // остаются вне сравнения и называются в отчёте.
        foreach (var (table, count) in counts)
            if (count > 1)
                keys.Remove(table);

        return keys;
    }
}
