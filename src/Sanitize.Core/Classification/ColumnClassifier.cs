using Sanitize.Core.Policy;
using Sanitize.Core.Rendering;

namespace Sanitize.Core.Classification;

/// <summary>
/// Стадия анализа: превращает признаки колонки в строку политики.
///
/// Правило разрешения конфликтов - «неизвестно значит чувствительно»: колонка,
/// про которую сигналы спорят, уходит человеку, а не пропускается молча.
/// Обратное умолчание давало бы тихие пропуски, а они и есть утечка.
/// </summary>
public sealed class ColumnClassifier
{
    /// <summary>Вес имени колонки. Признак сильный, но именно он чаще всего врёт.</summary>
    private const double NameWeight = 0.55;

    /// <summary>Вес комментария: человек писал его осознанно.</summary>
    private const double CommentWeight = 0.75;

    /// <summary>Вес формы значений на выборке.</summary>
    private const double FormatWeight = 0.70;

    /// <summary>Вес контрольной суммы: подделать её случайно нельзя.</summary>
    private const double ChecksumWeight = 0.95;

    /// <summary>Доля выборки, начиная с которой форма считается признаком колонки.</summary>
    private const double FormatShare = 0.90;

    /// <summary>Выше порога решение принимается без человека.</summary>
    private const double DecisionThreshold = 0.85;

    /// <summary>Ниже порога сигнал считается шумом.</summary>
    private const double NoiseThreshold = 0.40;

    private readonly IReadOnlyList<CompiledRule> _rules;

    public ColumnClassifier(ModelArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var compiled = new List<CompiledRule>(artifact.Rules.Count);
        foreach (var rule in artifact.Rules) compiled.Add(new CompiledRule(rule));

        // Порядок специфичности фиксирован: при равной уверенности выигрывает
        // более специфичный тип, иначе выбор зависел бы от порядка перечисления
        // правил в файле, то есть был бы недетерминированным.
        compiled.Sort((a, b) => a.Rule.Rank.CompareTo(b.Rule.Rank));
        _rules = compiled;
    }

    public PolicyColumn Classify(ColumnFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);

        CompiledRule? best = null;
        var bestScore = 0.0;
        var bestSource = VerdictSource.ColumnName;

        foreach (var rule in _rules)
        {
            var (score, source) = Score(rule, features);
            if (score <= bestScore) continue;

            bestScore = score;
            best = rule;
            bestSource = source;
        }

        if (best is not null && bestScore >= NoiseThreshold)
            return Typed(features, best, bestScore, bestSource);

        if (LooksLikeFreeText(features))
            return FreeText(features);

        return NotSensitive(features);
    }

    private (double Score, VerdictSource Source) Score(CompiledRule rule, ColumnFeatures features)
    {
        if (rule.Rule.DataTypes.Count > 0 && !Mentions(rule.Rule.DataTypes, features.DataType))
            return (0, VerdictSource.ColumnName);

        var score = 0.0;
        var source = VerdictSource.ColumnName;

        if (Matches(rule.Names, features.Address.Column))
            score = Combine(score, NameWeight);

        if (features.Comment.Length > 0 && Matches(rule.Comments, features.Comment))
        {
            score = Combine(score, CommentWeight);
            source = VerdictSource.ObjectComment;
        }

        var sample = NonEmpty(features.Sample);

        if (sample.Count > 0)
        {
            if (rule.Value is not null && Share(sample, v => rule.Value.IsMatch(v)) >= FormatShare)
            {
                score = Combine(score, FormatWeight);
                source = VerdictSource.ValueFormat;
            }

            var checksum = ChecksumOf(rule.Rule.Checksum);
            if (checksum is not null && Share(sample, checksum) >= FormatShare)
            {
                score = Combine(score, ChecksumWeight);
                source = VerdictSource.ValueFormat;
            }
        }

        return (score, source);
    }

    /// <summary>
    /// Сложение независимых признаков: ни один не даёт единицы сам по себе,
    /// но два согласных признака дают больше, чем сильнейший из них.
    /// </summary>
    private static double Combine(double current, double signal) => 1 - (1 - current) * (1 - signal);

    private static bool Matches(IReadOnlyList<System.Text.RegularExpressions.Regex> patterns, string text)
    {
        foreach (var pattern in patterns)
            if (pattern.IsMatch(text))
                return true;

        return false;
    }

    private static bool Mentions(IReadOnlyList<string> allowed, string dataType)
    {
        foreach (var item in allowed)
            if (dataType.Contains(item, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private static IReadOnlyList<string> NonEmpty(IReadOnlyList<string> sample)
    {
        var values = new List<string>(sample.Count);

        foreach (var value in sample)
            if (!string.IsNullOrWhiteSpace(value))
                values.Add(value);

        return values;
    }

    private static double Share(IReadOnlyList<string> sample, Func<string, bool> predicate)
    {
        var hits = 0;
        foreach (var value in sample)
            if (predicate(value))
                hits++;

        return (double)hits / sample.Count;
    }

    private static Func<string, bool>? ChecksumOf(string name) => name switch
    {
        "" => null,
        "inn" => StructuredIdentifiers.IsValidInn,
        "snils" => StructuredIdentifiers.IsValidSnils,
        _ => throw new InvalidDataException($"Артефакт модели ссылается на неизвестную проверку: {name}")
    };

    /// <summary>
    /// Свободный текст: длинные значения без устойчивой формы. Порог по длине
    /// намеренно грубый - решение всё равно принимает детектор, а не длина.
    /// </summary>
    private static bool LooksLikeFreeText(ColumnFeatures features) =>
        features.DataType.Contains("text", StringComparison.OrdinalIgnoreCase) ||
        features.AverageLength >= 40;

    private PolicyColumn Typed(
        ColumnFeatures features, CompiledRule rule, double score, VerdictSource source)
    {
        var strategy = StrategyFor(features);

        return new PolicyColumn
        {
            Schema = features.Address.Schema,
            Table = features.Address.Table,
            Column = features.Address.Column,
            DataType = features.DataType,
            Sensitive = true,
            SemanticType = rule.Rule.SemanticType,
            Mode = rule.Rule.Mode,
            Strategy = strategy,
            CanonicalKind = rule.Rule.CanonicalKind,
            Confidence = score,
            Source = source,
            NeedsApproval = score < DecisionThreshold
        };
    }

    private static PolicyColumn FreeText(ColumnFeatures features)
    {
        // Детектор не запускался - это не «чисто», это «не смотрели».
        var unmeasured = features.TextDetectorHitShare < 0;
        var found = features.TextDetectorHitShare > 0;

        return new PolicyColumn
        {
            Schema = features.Address.Schema,
            Table = features.Address.Table,
            Column = features.Address.Column,
            DataType = features.DataType,
            Sensitive = unmeasured || found,
            SemanticType = unmeasured || found ? "free_text" : "unknown",
            Mode = ColumnMode.TextWipe,
            Strategy = unmeasured || found ? ReplacementStrategy.Function : ReplacementStrategy.None,
            CanonicalKind = "Text",
            Confidence = unmeasured ? 0 : features.TextDetectorHitShare,
            Source = VerdictSource.TextDetector,
            Reason = unmeasured || found
                ? ""
                : "разбор текста на выборке не нашёл персональных данных",
            NeedsApproval = unmeasured
        };
    }

    private static PolicyColumn NotSensitive(ColumnFeatures features) => new()
    {
        Schema = features.Address.Schema,
        Table = features.Address.Table,
        Column = features.Address.Column,
        DataType = features.DataType,
        Sensitive = false,
        SemanticType = "unknown",
        Strategy = ReplacementStrategy.None,
        Confidence = 0,
        Source = VerdictSource.ValueFormat,
        Reason = "ни один признак не сработал выше порога шума"
    };

    /// <summary>
    /// Порог, ниже которого колонка идёт через словарь, даже не будучи
    /// уникальной.
    ///
    /// Причина - F-10. Замена чистой функцией сталкивается: два разных
    /// исходных значения получают одну замену, и мощность колонки падает.
    /// На большой мощности это доли процента, а на маленькой - катастрофа:
    /// колонка из шести городов теряет треть различных значений с первого
    /// же столкновения. Словарь такую потерю исключает по построению, а стоит
    /// он ровно столько, какова мощность колонки, - не столько, сколько строк
    /// в таблице. Поэтому порог по числу РАЗЛИЧНЫХ значений, а не по объёму.
    /// </summary>
    public const long DictionaryBelowDistinct = 100_000;

    /// <summary>
    /// Чем гарантировать свойство замены.
    ///
    /// Конечный домен главнее уникальности: замена вне домена сломала бы
    /// ограничение схемы, и прогон упал бы на восстановлении, а не на проверке.
    /// </summary>
    private static ReplacementStrategy StrategyFor(ColumnFeatures features)
    {
        if (features.FiniteDomain.Count > 0) return ReplacementStrategy.Derangement;

        if (features.IsUnique || features.IsPrimaryKey || features.IsForeignKey)
            return ReplacementStrategy.Dictionary;

        if (features.DistinctValues > 0 && features.DistinctValues <= DictionaryBelowDistinct)
            return ReplacementStrategy.Dictionary;

        return ReplacementStrategy.Function;
    }
}
