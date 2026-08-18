namespace Sanitize.Core.Values;

/// <summary>
/// Идентификатор семантического типа: что значение означает по смыслу.
///
/// Это НЕ перечисление. E-1 требует, чтобы добавление нового семантического
/// типа не требовало изменений в ядре замен; перечисление внутри ядра означало
/// бы правку ядра на каждый новый тип. Поэтому тип - строковый идентификатор,
/// а перечень типов и их порядок специфичности приходят из политики.
///
/// По F-7 тип не входит в ключ отображения: он нужен генератору, чтобы
/// отрендерить замену правдоподобно (F-5), но на выбор индекса не влияет.
/// </summary>
public readonly record struct SemanticTypeId : IComparable<SemanticTypeId>
{
    public string Value { get; }

    private SemanticTypeId(string value) => Value = value;

    /// <summary>
    /// Тип не определён. По правилу «неизвестно значит чувствительно» такая
    /// колонка уходит человеку на утверждение, а не пропускается молча.
    /// </summary>
    public static SemanticTypeId Unknown { get; } = new("unknown");

    public static SemanticTypeId Of(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Пустой идентификатор семантического типа", nameof(name));

        return new SemanticTypeId(name.Trim().ToLowerInvariant());
    }

    public bool IsUnknown => Value == Unknown.Value;

    public int CompareTo(SemanticTypeId other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;
}

/// <summary>
/// Перечень семантических типов и их порядок специфичности.
///
/// Реестр строится из политики, а не зашит в ядро: правило F-7 требует
/// фиксированного порядка, но не требует, чтобы этот порядок жил в коде.
/// Так добавление типа становится правкой политики, как того требует E-1.
/// </summary>
public sealed class SemanticTypeRegistry
{
    private readonly IReadOnlyDictionary<SemanticTypeId, int> _ranks;

    /// <param name="ranks">
    /// Пары «тип - ранг». Меньше ранг - специфичнее тип. Ранги обязаны быть
    /// попарно различными: равные сделали бы выбор зависимым от порядка
    /// перечисления, то есть недетерминированным, и одно значение рендерилось
    /// бы по-разному в разных прогонах.
    /// </param>
    public SemanticTypeRegistry(IEnumerable<KeyValuePair<SemanticTypeId, int>> ranks)
    {
        ArgumentNullException.ThrowIfNull(ranks);

        var map = new Dictionary<SemanticTypeId, int>();
        var seenRanks = new Dictionary<int, SemanticTypeId>();

        foreach (var (type, rank) in ranks)
        {
            if (map.ContainsKey(type))
                throw new ArgumentException($"Тип {type} объявлен дважды", nameof(ranks));

            if (seenRanks.TryGetValue(rank, out var other))
            {
                throw new ArgumentException(
                    $"Ранг {rank} занят типами {other} и {type}: порядок специфичности " +
                    "обязан быть строгим, иначе выбор типа перестаёт быть детерминированным",
                    nameof(ranks));
            }

            map[type] = rank;
            seenRanks[rank] = type;
        }

        _ranks = map;
    }

    /// <summary>Ранг неизвестного типа - хуже любого объявленного.</summary>
    public int RankOf(SemanticTypeId type) => _ranks.TryGetValue(type, out var rank) ? rank : int.MaxValue;

    public bool IsKnown(SemanticTypeId type) => _ranks.ContainsKey(type);

    public IReadOnlyCollection<SemanticTypeId> Types => (IReadOnlyCollection<SemanticTypeId>)_ranks.Keys;

    /// <summary>
    /// Наиболее специфичный тип из набора - правило F-7 для значения,
    /// принадлежащего нескольким доменам разных типов.
    ///
    /// При равных рангах выбор был бы недетерминирован, но конструктор такие
    /// реестры отвергает. Незнакомые типы сравниваются между собой по имени,
    /// чтобы результат не зависел от порядка перечисления.
    /// </summary>
    public SemanticTypeId MostSpecific(IEnumerable<SemanticTypeId> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var best = SemanticTypeId.Unknown;
        var bestRank = int.MaxValue;
        var chosen = false;

        foreach (var candidate in candidates)
        {
            var rank = RankOf(candidate);

            if (!chosen || rank < bestRank ||
                (rank == bestRank && candidate.CompareTo(best) < 0))
            {
                bestRank = rank;
                best = candidate;
                chosen = true;
            }
        }

        return best;
    }
}
