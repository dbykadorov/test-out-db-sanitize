using System.Text.RegularExpressions;
using Sanitize.Core.Rendering;
using Sanitize.Core.Values;

namespace Sanitize.Core.Validation;

/// <summary>
/// Формальный уровень F-5: замена принадлежит тому же семантическому типу
/// и удовлетворяет ограничениям колонки - длина, формат, допустимые символы,
/// контрольная сумма там, где она определена.
///
/// Проверка применяется **к пулам замен при их построении**, а не к миллиардам
/// строк: если все значения пула проходят валидатор, то и все подстановки
/// из него проходят. Это разница между проверкой за секунды и проверкой
/// за сутки.
/// </summary>
public sealed class ValueValidator
{
    private readonly IReadOnlyDictionary<SemanticTypeId, Func<string, bool>> _rules;

    public ValueValidator(IReadOnlyDictionary<SemanticTypeId, Func<string, bool>> rules) =>
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));

    private static readonly Regex PhonePattern = new(@"^\+7\d{10}$", RegexOptions.Compiled);

    private static readonly Regex EmailPattern =
        new(@"^[^@\s]+@[^@\s.]+(\.[^@\s.]+)+$", RegexOptions.Compiled);

    private static readonly Regex PassportPattern = new(@"^[0-9]{4} [0-9]{6}$", RegexOptions.Compiled);

    /// <summary>
    /// Дата в том же виде, в каком её отдаёт и принимает канал: ISO, без времени.
    /// Разбор строгий - «2026-02-30» обязано быть отвергнуто здесь, а не базой.
    /// </summary>
    private static bool IsIsoDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _);

    public static ValueValidator Default() =>
        new(new Dictionary<SemanticTypeId, Func<string, bool>>
        {
            [SemanticTypeId.Of("inn_person")] = StructuredIdentifiers.IsValidInn,
            [SemanticTypeId.Of("inn_legal")] = StructuredIdentifiers.IsValidInn,
            [SemanticTypeId.Of("snils")] = StructuredIdentifiers.IsValidSnils,
            [SemanticTypeId.Of("phone")] = v => PhonePattern.IsMatch(v),
            [SemanticTypeId.Of("email")] = v => EmailPattern.IsMatch(v),
            [SemanticTypeId.Of("birth_date")] = IsIsoDate,
            [SemanticTypeId.Of("passport")] = v => PassportPattern.IsMatch(v),
            [SemanticTypeId.Of("marital_status")] = NonEmptyWords,
            [SemanticTypeId.Of("full_name")] = NonEmptyWords,
            [SemanticTypeId.Of("last_name")] = NonEmptyWords,
            [SemanticTypeId.Of("street")] = NonEmptyWords,
            [SemanticTypeId.Of("city")] = NonEmptyWords,
            [SemanticTypeId.Of("postal_address")] = NonEmptyWords,
            [SemanticTypeId.Of("free_text")] = NonEmptyWords
        });

    /// <summary>Есть ли для типа формальная проверка.</summary>
    public bool Covers(SemanticTypeId type) => _rules.ContainsKey(type);

    public bool IsValid(SemanticTypeId type, string value) =>
        _rules.TryGetValue(type, out var rule) && rule(value);

    /// <summary>
    /// Проверяет пул целиком. Возвращает первые несоответствия - молча
    /// подставлять невалидное значение нельзя: результат не загрузится
    /// в исходную схему, и это выяснится в конце прогона, а не в начале.
    /// </summary>
    public IReadOnlyList<string> Validate(SemanticTypeId type, IEnumerable<string> pool, int limit = 10)
    {
        ArgumentNullException.ThrowIfNull(pool);

        if (!Covers(type))
        {
            throw new InvalidOperationException(
                $"Для типа {type} нет формальной проверки. Требование F-5 задаёт валидатор " +
                "для каждого поддержанного типа: тип без проверки означает, что правдоподобность " +
                "никто не измерял.");
        }

        var bad = new List<string>();

        foreach (var value in pool)
        {
            if (IsValid(type, value)) continue;

            bad.Add(value);
            if (bad.Count >= limit) break;
        }

        return bad;
    }

    /// <summary>
    /// Минимальная проверка для значений без жёсткого формата: непустое,
    /// без концевых пробелов, без управляющих символов.
    ///
    /// Слабее, чем контрольная сумма, и это признаётся честно: экспертный
    /// уровень F-5 - слепая выборка с рецензентами - в рамках тестового
    /// не выполняется.
    /// </summary>
    private static bool NonEmptyWords(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value != value.Trim()) return false;

        return !value.Any(char.IsControl);
    }
}
