using System.Globalization;
using System.Numerics;

namespace Sanitize.Core.Values;

/// <summary>
/// Вид значения для канонизации: как значение записано, а не что оно значит.
///
/// Перечень закрыт намеренно, в отличие от семантических типов: правила
/// канонизации заданы требованием F-7, и новый вид означал бы правку самого
/// требования, а не расширение системы.
///
/// <see cref="EmailAddress"/> - единственное место, где канонизация зависит
/// от смысла: F-7 велит приводить адреса почты к нижнему регистру, а у прочих
/// строк регистр сохранять.
/// </summary>
public enum CanonicalKind
{
    Text,
    EmailAddress,
    Integer,
    Decimal,
    Boolean,
    Timestamp,
    Binary
}

/// <summary>
/// Каноническая форма значения - ключ отображения по F-7.
///
/// Ключом служит не сырое значение, а его каноническая форма, иначе
/// целочисленная единица и строковая единица получат разные замены,
/// и сквозная консистентность нарушится.
///
/// Канонизация детерминирована и версионируется вместе с политикой:
/// её изменение меняет все замены, поэтому это смена версии, а не правка.
/// </summary>
public readonly record struct CanonicalValue
{
    /// <summary>
    /// Что именно отбрасывается с конца строки. Только пробел: F-7 говорит
    /// «без концевых пробелов», а не «без пробельных символов». Табуляция
    /// и перевод строки - часть значения, и их удаление склеило бы разные
    /// значения в один ключ.
    /// </summary>
    private const char TrimmedChar = ' ';

    /// <summary>Каноническая запись. Пусто, если значение ключом не является.</summary>
    public string Key { get; }

    /// <summary>
    /// Значения, которые по F-7 не заменяются вовсе и ключами не являются:
    /// отсутствующее значение и пустая строка.
    /// </summary>
    public bool IsKey => Key.Length > 0;

    private CanonicalValue(string key) => Key = key;

    /// <summary>Значение, которое не подлежит замене.</summary>
    public static CanonicalValue NotAKey { get; } = new(string.Empty);

    /// <summary>
    /// Приводит сырое значение к канонической форме.
    /// <paramref name="raw"/> равное null означает отсутствующее значение.
    /// </summary>
    public static CanonicalValue From(string? raw, CanonicalKind kind)
    {
        if (raw is null) return NotAKey;

        return kind switch
        {
            CanonicalKind.Text => FromText(raw),
            CanonicalKind.EmailAddress => FromEmail(raw),
            CanonicalKind.Integer => FromInteger(raw),
            CanonicalKind.Decimal => FromDecimal(raw),
            CanonicalKind.Boolean => FromBoolean(raw),
            CanonicalKind.Timestamp => FromTimestamp(raw),
            CanonicalKind.Binary => FromBinary(raw),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Неизвестный вид значения")
        };
    }

    /// <summary>Строки: без концевых пробелов, регистр сохраняется.</summary>
    private static CanonicalValue FromText(string raw)
    {
        var trimmed = raw.TrimEnd(TrimmedChar);
        return trimmed.Length == 0 ? NotAKey : new CanonicalValue(trimmed);
    }

    /// <summary>Адрес почты: концевые пробелы убраны, регистр приведён к нижнему.</summary>
    private static CanonicalValue FromEmail(string raw)
    {
        var trimmed = raw.TrimEnd(TrimmedChar);
        return trimmed.Length == 0 ? NotAKey : new CanonicalValue(trimmed.ToLowerInvariant());
    }

    /// <summary>Целые: десятичная запись без ведущих нулей.</summary>
    private static CanonicalValue FromInteger(string raw)
    {
        var trimmed = raw.Trim(TrimmedChar);
        if (trimmed.Length == 0) return NotAKey;

        // BigInteger, а не long: целое может прийти из numeric без дробной части,
        // а там разрядность не ограничена 64 битами.
        if (!BigInteger.TryParse(trimmed, NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException($"Значение не является целым: {trimmed}");
        }

        return new CanonicalValue(value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Дробные: без ведущих нулей и без незначащих нулей дробной части.
    ///
    /// Через decimal не считаем: у него 28-29 значащих цифр, а numeric
    /// в PostgreSQL шире. Молча округлить значение означало бы склеить два
    /// разных ключа в один и нарушить F-7 незаметно.
    /// </summary>
    private static CanonicalValue FromDecimal(string raw)
    {
        var trimmed = raw.Trim(TrimmedChar);
        if (trimmed.Length == 0) return NotAKey;

        var (mantissa, scale) = ParseDecimal(trimmed);

        if (mantissa.IsZero) return new CanonicalValue("0");

        // Снимаем незначащие нули дробной части: 1.500 и 1.5 обязаны дать один ключ.
        while (scale > 0 && BigInteger.Remainder(mantissa, 10).IsZero)
        {
            mantissa /= 10;
            scale--;
        }

        // Отрицательный масштаб означает экспоненту вида 1.5e3 - разворачиваем.
        while (scale < 0)
        {
            mantissa *= 10;
            scale++;
        }

        var negative = mantissa.Sign < 0;
        var digits = BigInteger.Abs(mantissa).ToString(CultureInfo.InvariantCulture);

        string text;
        if (scale == 0)
        {
            text = digits;
        }
        else
        {
            if (digits.Length <= scale) digits = digits.PadLeft(scale + 1, '0');
            text = digits[..^scale] + "." + digits[^scale..];
        }

        return new CanonicalValue(negative ? "-" + text : text);
    }

    /// <summary>Разбирает десятичную запись в мантиссу и масштаб без потери точности.</summary>
    private static (BigInteger Mantissa, int Scale) ParseDecimal(string text)
    {
        var span = text.AsSpan();
        var negative = false;
        var i = 0;

        if (i < span.Length && (span[i] == '+' || span[i] == '-'))
        {
            negative = span[i] == '-';
            i++;
        }

        var digits = new System.Text.StringBuilder();
        var scale = 0;
        var seenDot = false;
        var seenDigit = false;

        for (; i < span.Length; i++)
        {
            var c = span[i];

            if (c == '.')
            {
                if (seenDot) throw new FormatException($"Две десятичные точки: {text}");
                seenDot = true;
                continue;
            }

            if (c is 'e' or 'E') break;

            if (!char.IsAsciiDigit(c)) throw new FormatException($"Значение не является числом: {text}");

            digits.Append(c);
            seenDigit = true;
            if (seenDot) scale++;
        }

        if (!seenDigit) throw new FormatException($"Значение не является числом: {text}");

        if (i < span.Length && span[i] is 'e' or 'E')
        {
            var exponentText = span[(i + 1)..].ToString();
            if (!int.TryParse(exponentText, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var exponent))
            {
                throw new FormatException($"Неверная экспонента: {text}");
            }

            scale -= exponent;
        }

        var mantissa = BigInteger.Parse(digits.ToString(), CultureInfo.InvariantCulture);
        return (negative ? -mantissa : mantissa, scale);
    }

    private static CanonicalValue FromBoolean(string raw)
    {
        var trimmed = raw.Trim(TrimmedChar).ToLowerInvariant();
        return trimmed switch
        {
            "" => NotAKey,
            "t" or "true" or "1" or "yes" or "y" => new CanonicalValue("true"),
            "f" or "false" or "0" or "no" or "n" => new CanonicalValue("false"),
            _ => throw new FormatException($"Значение не является логическим: {raw}")
        };
    }

    /// <summary>Даты и метки времени: ISO 8601 в UTC.</summary>
    private static CanonicalValue FromTimestamp(string raw)
    {
        var trimmed = raw.Trim(TrimmedChar);
        if (trimmed.Length == 0) return NotAKey;

        if (!DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
        {
            throw new FormatException($"Значение не является меткой времени: {trimmed}");
        }

        return new CanonicalValue(value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ",
            CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Двоичные данные: шестнадцатеричная запись в нижнем регистре, без префикса.
    /// Нечётная длина или посторонний символ - ошибка, а не повод для догадок.
    /// </summary>
    private static CanonicalValue FromBinary(string raw)
    {
        var trimmed = raw.Trim(TrimmedChar);

        if (trimmed.StartsWith(@"\x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[2..];
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[2..];

        if (trimmed.Length == 0) return NotAKey;

        if (trimmed.Length % 2 != 0)
            throw new FormatException($"Нечётное число шестнадцатеричных цифр: {raw}");

        foreach (var c in trimmed)
        {
            if (!char.IsAsciiHexDigit(c))
                throw new FormatException($"Значение не является шестнадцатеричным: {raw}");
        }

        return new CanonicalValue(trimmed.ToLowerInvariant());
    }

    public override string ToString() => IsKey ? Key : "<не ключ>";
}
