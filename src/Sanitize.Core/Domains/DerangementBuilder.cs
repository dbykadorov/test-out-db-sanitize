using Sanitize.Core.Values;

namespace Sanitize.Core.Domains;

/// <summary>Почему беспорядок построить не удалось.</summary>
public enum DerangementFailure
{
    None = 0,

    /// <summary>
    /// Домен из одного значения. Заменить его нельзя, не нарушив ограничение
    /// схемы: требования F-4 и F-11 в этой точке несовместимы.
    /// </summary>
    SingleValue,

    /// <summary>
    /// Домен из двух значений. Единственный беспорядок - обмен, он полностью
    /// предсказуем и обратим любым получателем, поэтому колонка НЕ считается
    /// обезличенной.
    /// </summary>
    TwoValuesOnlySwap,

    /// <summary>
    /// Совершенного паросочетания без неподвижных точек не существует:
    /// пересечение допустимых значений слишком бедное.
    /// </summary>
    NoPerfectMatching,

    /// <summary>
    /// Во входном домене есть повторы. Перестановка строится по позициям,
    /// поэтому равные строки дали бы значению замену самим собой, а итоговое
    /// отображение оказалось бы короче домена.
    /// </summary>
    DuplicateValues,

    /// <summary>
    /// Ограничения допускают ровно одно совершенное паросочетание без
    /// неподвижных точек. Тогда получатель выгрузки восстанавливает его
    /// по самим ограничениям, не зная секрета, - ровно как обмен в домене
    /// из двух значений. Колонка НЕ считается обезличенной.
    /// </summary>
    MatchingIsUnique
}

/// <summary>
/// Результат построения. Отсутствие беспорядка - не исключение и не молчаливый
/// пропуск: конвейер обязан остановиться и запросить решение владельца данных
/// из трёх вариантов, названных в исключении F-4.
/// </summary>
public sealed class DerangementResult
{
    private readonly IReadOnlyDictionary<string, string>? _mapping;

    public DerangementFailure Failure { get; }

    public bool Ok => Failure == DerangementFailure.None;

    public IReadOnlyDictionary<string, string> Mapping =>
        _mapping ?? throw new InvalidOperationException(
            $"Беспорядок не построен: {Failure}. Требуется решение владельца данных.");

    private DerangementResult(IReadOnlyDictionary<string, string>? mapping, DerangementFailure failure)
    {
        _mapping = mapping;
        Failure = failure;
    }

    public static DerangementResult Success(IReadOnlyDictionary<string, string> mapping) =>
        new(mapping, DerangementFailure.None);

    public static DerangementResult Failed(DerangementFailure failure) =>
        new(null, failure);
}

/// <summary>
/// Строит беспорядок - перестановку множества допустимых значений на себя
/// БЕЗ неподвижных точек (Р-3).
///
/// Зачем именно беспорядок: колонка с ограничением схемы (перечисление,
/// проверочное ограничение со списком) не может принять значение извне домена -
/// это сломало бы F-11. Но значение, оставшееся собой, - это незаменённые
/// персональные данные. Перестановка внутри домена сохраняет ограничение
/// и рвёт связь с субъектом.
///
/// Плата - искажение распределения по этой колонке, и оно фиксируется
/// в паспорте выгрузки.
/// </summary>
public static class DerangementBuilder
{
    /// <summary>
    /// Беспорядок на одном домене. Детерминирован при одном и том же
    /// <paramref name="seed"/>: без этого нарушилась бы воспроизводимость O-1.
    /// </summary>
    public static DerangementResult Build(IReadOnlyList<CanonicalValue> domain, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(domain);

        var precheck = CheckDomain(domain, out var keys);
        if (precheck != DerangementFailure.None) return DerangementResult.Failed(precheck);

        var order = Enumerable.Range(0, keys.Count).ToArray();
        Sattolo(order, seed);

        var mapping = new Dictionary<string, string>(keys.Count, StringComparer.Ordinal);
        for (var i = 0; i < keys.Count; i++)
        {
            if (order[i] == i)
            {
                // Недостижимо: Сатолло не оставляет неподвижных точек
                // по построению. Проверка оставлена, потому что молча отдать
                // значение самому себе означало бы выпустить ПДн.
                return DerangementResult.Failed(DerangementFailure.NoPerfectMatching);
            }

            mapping[keys[i]] = keys[order[i]];
        }

        return DerangementResult.Success(mapping);
    }

    /// <summary>
    /// Беспорядок, когда значение входит в несколько ограниченных доменов сразу.
    ///
    /// Независимого беспорядка тут недостаточно: замена обязана быть допустимой
    /// во всех доменах одновременно. Требуется совершенное паросочетание без
    /// неподвижных точек в двудольном графе «значение - допустимые замены».
    /// Конвейер строит его явно; если паросочетания нет - задача неразрешима,
    /// и решение принимает владелец данных.
    /// </summary>
    /// <param name="allowed">
    /// Для каждого значения - множество замен, допустимых во всех его доменах.
    /// </param>
    public static DerangementResult BuildConstrained(
        IReadOnlyList<CanonicalValue> values,
        IReadOnlyDictionary<string, IReadOnlySet<string>> allowed,
        ulong seed)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(allowed);

        // Те же вырожденные случаи, что и у простого построителя: домен из двух
        // значений предсказуем независимо от того, чем он ограничен.
        var precheck = CheckDomain(values, out var keys);
        if (precheck != DerangementFailure.None) return DerangementResult.Failed(precheck);

        var adjacency = BuildAdjacency(keys, allowed, seed);

        var matchOf = FindMatching(adjacency, keys.Count, forbiddenFrom: -1, forbiddenTo: -1);
        if (matchOf is null) return DerangementResult.Failed(DerangementFailure.NoPerfectMatching);

        // Паросочетание, единственное при данных ограничениях, получатель
        // восстанавливает сам: секрет в него не входит вовсе. Это тот же дефект,
        // что и обмен в домене из двух значений, только выглядит сложнее.
        if (!HasAlternative(adjacency, keys.Count, matchOf))
            return DerangementResult.Failed(DerangementFailure.MatchingIsUnique);

        var mapping = new Dictionary<string, string>(keys.Count, StringComparer.Ordinal);
        for (var j = 0; j < keys.Count; j++)
        {
            // Каждая левая вершина нашла цепь, долей поровну - значит, покрыты
            // и правые. Проверка оставлена: неполное паросочетание молча дало бы
            // значению замену самим собой.
            if (matchOf[j] == -1) return DerangementResult.Failed(DerangementFailure.NoPerfectMatching);

            mapping[keys[matchOf[j]]] = keys[j];
        }

        return DerangementResult.Success(mapping);
    }

    /// <summary>
    /// Список смежности: из значения в допустимые замены, кроме него самого -
    /// неподвижная точка запрещена по определению беспорядка.
    ///
    /// Порядок обхода перемешивается секретом прогона. Без этого алгоритм Куна
    /// выдавал бы одно и то же паросочетание для одних и тех же ограничений,
    /// то есть замену, не зависящую от секрета вовсе.
    /// </summary>
    private static List<int>[] BuildAdjacency(
        IReadOnlyList<string> keys,
        IReadOnlyDictionary<string, IReadOnlySet<string>> allowed,
        ulong seed)
    {
        var index = new Dictionary<string, int>(keys.Count, StringComparer.Ordinal);
        for (var i = 0; i < keys.Count; i++) index[keys[i]] = i;

        var random = new SplitMix64(seed);
        var adjacency = new List<int>[keys.Count];

        for (var i = 0; i < keys.Count; i++)
        {
            adjacency[i] = new List<int>();
            if (!allowed.TryGetValue(keys[i], out var candidates)) continue;

            foreach (var candidate in candidates)
            {
                if (index.TryGetValue(candidate, out var j) && j != i) adjacency[i].Add(j);
            }

            // Сначала фиксированный порядок, потом перемешивание секретом:
            // так результат не зависит от порядка перечисления множества,
            // но зависит от секрета (O-1 и Р-1 одновременно).
            adjacency[i].Sort();
            Shuffle(adjacency[i], ref random);
        }

        return adjacency;
    }

    private static void Shuffle(List<int> items, ref SplitMix64 random)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = (int)random.NextBelow((ulong)i);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    /// <summary>
    /// Ищет совершенное паросочетание, при желании запретив одно ребро.
    /// Возвращает null, если совершенного паросочетания нет.
    /// </summary>
    private static int[]? FindMatching(List<int>[] adjacency, int size, int forbiddenFrom, int forbiddenTo)
    {
        var matchOf = new int[size];
        Array.Fill(matchOf, -1);

        for (var i = 0; i < size; i++)
        {
            var visited = new bool[size];
            if (!TryAugment(i, adjacency, matchOf, visited, forbiddenFrom, forbiddenTo)) return null;
        }

        return matchOf;
    }

    /// <summary>
    /// Есть ли у найденного паросочетания хотя бы одна альтернатива.
    ///
    /// Проверяется прямо: по очереди запрещаем каждое использованное ребро
    /// и смотрим, найдётся ли другое совершенное паросочетание. Домены здесь
    /// конечные и короткие - это перечисления и списки кодов, - поэтому
    /// квадратичная проверка дешевле, чем риск выпустить предсказуемую замену.
    /// </summary>
    private static bool HasAlternative(List<int>[] adjacency, int size, int[] matchOf)
    {
        for (var to = 0; to < size; to++)
        {
            var from = matchOf[to];
            if (from == -1) continue;

            if (FindMatching(adjacency, size, from, to) is not null) return true;
        }

        return false;
    }

    /// <summary>
    /// Общие для обоих построителей проверки входного домена.
    ///
    /// Повторы ищутся по КАНОНИЧЕСКИМ формам, а не по сырым записям: «A» и «A »,
    /// разный регистр почты и разные записи одного числа схлопнутся в один ключ
    /// позже, и словарь молча потеряет часть отображения.
    /// </summary>
    private static DerangementFailure CheckDomain(
        IReadOnlyList<CanonicalValue> domain,
        out IReadOnlyList<string> keys)
    {
        var list = new List<string>(domain.Count);
        foreach (var value in domain)
        {
            if (!value.IsKey)
            {
                // Значение, не являющееся ключом, в домене замен появиться
                // не может: оно не заменяется вовсе.
                keys = Array.Empty<string>();
                return DerangementFailure.DuplicateValues;
            }

            list.Add(value.Key);
        }

        keys = list;

        var distinct = new HashSet<string>(list, StringComparer.Ordinal);
        if (distinct.Count != list.Count) return DerangementFailure.DuplicateValues;

        return list.Count switch
        {
            <= 1 => DerangementFailure.SingleValue,
            2 => DerangementFailure.TwoValuesOnlySwap,
            _ => DerangementFailure.None
        };
    }

    private static bool TryAugment(int from, List<int>[] adjacency, int[] matchOf, bool[] visited,
        int forbiddenFrom, int forbiddenTo)
    {
        foreach (var to in adjacency[from])
        {
            if (from == forbiddenFrom && to == forbiddenTo) continue;
            if (visited[to]) continue;
            visited[to] = true;

            if (matchOf[to] == -1 ||
                TryAugment(matchOf[to], adjacency, matchOf, visited, forbiddenFrom, forbiddenTo))
            {
                matchOf[to] = from;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Алгоритм Сатолло: перестановка без неподвижных точек по построению,
    /// за один проход и без повторных попыток.
    ///
    /// Отличие от перемешивания Фишера-Йетса с последующей починкой: тот даёт
    /// смещённое распределение (на домене из трёх элементов почти все исходные
    /// перестановки сводились к одному беспорядку). Сатолло выбирает равномерно
    /// среди циклических перестановок - это подмножество беспорядков, зато
    /// выбор внутри него честный, а завершение гарантировано.
    /// </summary>
    private static void Sattolo(int[] items, ulong seed)
    {
        var random = new SplitMix64(seed);

        for (var i = items.Length - 1; i > 0; i--)
        {
            // Строго меньше i - именно это отличает Сатолло от Фишера-Йетса
            // и исключает неподвижные точки. NextBelow включает верхнюю границу,
            // поэтому здесь i-1, а не i: с i перемешивание снова стало бы
            // Фишером-Йетсом, и значение смогло бы остаться собой.
            var j = (int)random.NextBelow((ulong)(i - 1));
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    /// <summary>
    /// SplitMix64 с отдельно хранимым состоянием.
    ///
    /// Собственный генератор нужен ровно потому, что результат обязан
    /// воспроизводиться между прогонами и между версиями платформы:
    /// System.Random такой гарантии не даёт.
    /// </summary>
    private struct SplitMix64
    {
        private ulong _state;

        // Нулевое состояние подменять не нужно: гамма прибавляется до
        // перемешивания, поэтому нуль - такое же состояние, как любое другое.
        // Подмена делала бы два разных секрета одной последовательностью.
        public SplitMix64(ulong seed) => _state = seed;

        public ulong Next()
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>
        /// Равномерное число из диапазона от нуля до <paramref name="bound"/>
        /// включительно. Взятие остатка напрямую смещало бы выбор в сторону
        /// младших значений, поэтому неполный последний отрезок отбрасывается.
        /// </summary>
        public ulong NextBelow(ulong bound)
        {
            var range = bound + 1;
            var limit = ulong.MaxValue - (ulong.MaxValue % range);

            ulong value;
            do
            {
                value = Next();
            }
            while (value >= limit);

            return value % range;
        }
    }
}
