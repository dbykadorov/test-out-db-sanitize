using Sanitize.Adapters.Postgres;
using Sanitize.Core.Domains;
using Sanitize.Core.Planning;
using Sanitize.Core.Replacement;
using Sanitize.Core.Values;
using Sanitize.Dictionary;

namespace Sanitize.Worker;

public sealed record DomainReport
{
    public required string SemanticType { get; init; }
    public required bool Constrained { get; init; }
    public required int DistinctValues { get; init; }
    public required int Attempts { get; init; }
    public string Note { get; init; } = "";
}

/// <summary>
/// Стадия 2: материализация словаря прогона.
///
/// Словарь строится один на прогон и покрывает домены, где нужна биекция:
/// ключи, уникальные колонки, конечные домены. Всё остальное считает чистая
/// функция без состояния - хранить её нечего.
/// </summary>
public sealed class DictionaryStage
{
    /// <summary>
    /// Сколько раз пробовать следующий индекс при столкновении замен.
    ///
    /// Предел нужен, чтобы бедный пул не превращался в бесконечный цикл:
    /// если словарь компонентов меньше числа различных значений, биекции
    /// не существует вовсе, и это надо сказать вслух, а не искать вечно.
    /// </summary>
    private const int MaxProbes = 64;

    private readonly RunSettings _settings;
    private readonly RunLog _log;

    public DictionaryStage(RunSettings settings, RunLog log)
    {
        _settings = settings;
        _log = log;
    }

    public async Task<IReadOnlyList<DomainReport>> BuildAsync(
        RunPlan plan,
        RunPaths paths,
        PseudoRandomFunction prf,
        IValueRenderer renderer,
        CancellationToken token)
    {
        var probe = new PostgresProbe(_settings.SourceDsn);
        var entries = new List<KeyValuePair<ReplacementKey, string>>();
        var reports = new List<DomainReport>();

        foreach (var domain in plan.Domains)
        {
            var values = domain.Constrained
                ? Canonical(domain.AllowedValues)
                : await CollectAsync(probe, domain, token).ConfigureAwait(false);

            reports.Add(domain.Constrained
                ? Derange(domain, values, prf, entries)
                : Map(domain, values, prf, renderer, entries));
        }

        var written = DictionaryWriter.Write(paths.DictionaryFile, entries);
        _log.Write($"словарь: записей {written}, доменов {reports.Count}");

        return reports;
    }

    private static IReadOnlyList<CanonicalValue> Canonical(IReadOnlyList<string> raw)
    {
        var values = new List<CanonicalValue>(raw.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in raw)
        {
            var canonical = CanonicalValue.From(item, CanonicalKind.Text);
            if (canonical.IsKey && seen.Add(canonical.Key)) values.Add(canonical);
        }

        return values;
    }

    private static async Task<IReadOnlyList<CanonicalValue>> CollectAsync(
        PostgresProbe probe, DictionaryDomain domain, CancellationToken token)
    {
        // Значения собираются по ВСЕМ колонкам домена сразу: колонка с внешним
        // ключом обязана получить ту же замену, что и цель ссылки, а значит
        // и жить в одном словаре с ней.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var values = new List<CanonicalValue>();

        foreach (var address in domain.Columns)
        {
            await foreach (var raw in probe.DistinctValuesAsync(address, token).ConfigureAwait(false))
            {
                var canonical = CanonicalValue.From(raw, CanonicalKind.Text);
                if (canonical.IsKey && seen.Add(canonical.Key)) values.Add(canonical);
            }
        }

        // Порядок фиксируется: он влияет на разрешение столкновений, а значит
        // и на содержимое словаря. Без сортировки повторный прогон с тем же
        // секретом дал бы другой словарь, и Р-6 не выполнилось бы.
        values.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

        return values;
    }

    private DomainReport Derange(
        DictionaryDomain domain,
        IReadOnlyList<CanonicalValue> values,
        PseudoRandomFunction prf,
        List<KeyValuePair<ReplacementKey, string>> entries)
    {
        var seed = prf.IndexOf(ReplacementKey.For(CanonicalValue.From(domain.SemanticType, CanonicalKind.Text)));
        var result = DerangementBuilder.Build(values, seed);

        if (!result.Ok)
        {
            // Вырожденный домен - это не повод придумать замену: требование F-4
            // называет три варианта, и выбирает из них владелец данных.
            throw new InvalidOperationException(
                $"домен {domain.SemanticType}: беспорядок не построен ({result.Failure}). " +
                "Требуется решение владельца данных по исключению из F-4");
        }

        foreach (var (from, to) in result.Mapping)
            entries.Add(new KeyValuePair<ReplacementKey, string>(
                ReplacementKey.For(CanonicalValue.From(from, CanonicalKind.Text)), to));

        _log.Write($"домен {domain.SemanticType}: беспорядок на {values.Count} значениях");

        return new DomainReport
        {
            SemanticType = domain.SemanticType,
            Constrained = true,
            DistinctValues = values.Count,
            Attempts = values.Count,
            Note = "перестановка без неподвижных точек внутри ограничения схемы (Р-3)"
        };
    }

    private DomainReport Map(
        DictionaryDomain domain,
        IReadOnlyList<CanonicalValue> values,
        PseudoRandomFunction prf,
        IValueRenderer renderer,
        List<KeyValuePair<ReplacementKey, string>> entries)
    {
        var type = SemanticTypeId.Of(domain.SemanticType);

        if (!renderer.Supports(type))
            throw new InvalidOperationException($"домен {domain.SemanticType}: нет генератора замен");

        var used = new HashSet<string>(StringComparer.Ordinal);
        var attempts = 0;

        foreach (var value in values)
        {
            var key = ReplacementKey.For(value);
            var index = prf.IndexOf(key);

            string? replacement = null;

            for (var probe = 0; probe < MaxProbes; probe++)
            {
                attempts++;

                // Смещение по индексу, а не повторный хэш: так разрешение
                // столкновения остаётся детерминированным и воспроизводится
                // при повторном прогоне с тем же секретом.
                var candidate = renderer.Render(type, unchecked(index + (ulong)probe));

                // Значение, заменённое само на себя, - это незаменённые
                // персональные данные, а не удачное совпадение. На больших
                // доменах это почти невозможно, на маленьких - обычное дело.
                if (string.Equals(candidate, value.Key, StringComparison.Ordinal)) continue;

                if (used.Add(candidate))
                {
                    replacement = candidate;
                    break;
                }
            }

            if (replacement is null)
            {
                throw new InvalidOperationException(
                    $"домен {domain.SemanticType}: биекция не построена на {values.Count} значениях. " +
                    $"Пул замен беднее домена - словари компонентов в артефакте модели " +
                    "нужно расширить, иначе уникальность колонки восстановить нельзя");
            }

            entries.Add(new KeyValuePair<ReplacementKey, string>(key, replacement));
        }

        _log.Write($"домен {domain.SemanticType}: значений {values.Count}, попыток {attempts}");

        return new DomainReport
        {
            SemanticType = domain.SemanticType,
            Constrained = false,
            DistinctValues = values.Count,
            Attempts = attempts,
            Note = "биекция через словарь прогона"
        };
    }
}
