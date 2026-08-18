using System.Text;

namespace Sanitize.Core.Rendering;

/// <summary>
/// Форма структурных идентификаторов: разрядность и контрольные суммы.
///
/// Содержания здесь нет: осмысленные части - код региона в ИНН, код оператора
/// в телефоне - приходят из артефактов модели и передаются сюда параметром.
/// Код достраивает только то, что F-6 называет технической достройкой.
/// </summary>
public static class StructuredIdentifiers
{
    private static readonly int[] Inn10 = { 2, 4, 10, 3, 5, 9, 4, 6, 8 };
    private static readonly int[] Inn12First = { 7, 2, 4, 10, 3, 5, 9, 4, 6, 8 };
    private static readonly int[] Inn12Second = { 3, 7, 2, 4, 10, 3, 5, 9, 4, 6, 8 };

    /// <summary>
    /// ИНН юридического лица: код региона из артефакта модели, затем цифры
    /// и контрольная.
    /// </summary>
    public static string InnLegal(string region, ref IndexStream stream)
    {
        var digits = region + Digits(ref stream, 9 - region.Length);
        return digits + Checksum(digits, Inn10);
    }

    /// <summary>
    /// ИНН физического лица: код региона из артефакта модели, затем цифры
    /// и две контрольные.
    /// </summary>
    public static string InnPerson(string region, ref IndexStream stream)
    {
        var digits = region + Digits(ref stream, 10 - region.Length);
        var eleventh = Checksum(digits, Inn12First);
        var twelfth = Checksum(digits + eleventh, Inn12Second);
        return digits + eleventh + twelfth;
    }

    /// <summary>
    /// СНИЛС: девять цифр номера плюс две контрольные.
    ///
    /// Осмысленного префикса у СНИЛС нет - номер не кодирует ни региона,
    /// ни ведомства. Поэтому здесь содержания нет вовсе, и значение целиком
    /// относится к технической достройке в смысле F-6.
    /// </summary>
    public static string Snils(ref IndexStream stream)
    {
        var digits = Digits(ref stream, 9);

        var sum = 0;
        for (var i = 0; i < 9; i++) sum += (digits[i] - '0') * (9 - i);

        var control = sum switch
        {
            < 100 => sum,
            100 or 101 => 0,
            _ => sum % 101 == 100 ? 0 : sum % 101
        };

        return $"{digits[..3]}-{digits[3..6]}-{digits[6..9]} {control:D2}";
    }

    public static bool IsValidInn(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.All(char.IsAsciiDigit)) return false;

        return value.Length switch
        {
            10 => Checksum(value[..9], Inn10) == value[9],
            12 => Checksum(value[..10], Inn12First) == value[10] &&
                  Checksum(value[..11], Inn12Second) == value[11],
            _ => false
        };
    }

    public static bool IsValidSnils(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;

        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != 11) return false;

        var sum = 0;
        for (var i = 0; i < 9; i++) sum += (digits[i] - '0') * (9 - i);

        var expected = sum switch
        {
            < 100 => sum,
            100 or 101 => 0,
            _ => sum % 101 == 100 ? 0 : sum % 101
        };

        return int.Parse(digits[9..]) == expected;
    }

    private static char Checksum(string digits, int[] weights)
    {
        var sum = 0;
        for (var i = 0; i < weights.Length; i++) sum += (digits[i] - '0') * weights[i];
        return (char)('0' + sum % 11 % 10);
    }

    /// <summary>
    /// Цифры из индекса. Берутся по одной из потока, а не остатком от деления:
    /// остаток на десять в степени N выбрасывал бы старшие биты и резал
    /// мощность пула.
    /// </summary>
    private static string Digits(ref IndexStream stream, int count)
    {
        var builder = new StringBuilder(count);

        for (var i = 0; i < count; i++) builder.Append((char)('0' + (int)stream.Next(10)));

        return builder.ToString();
    }
}

/// <summary>
/// Поток независимых чисел из одного индекса.
///
/// Нужен, когда значение собирается из нескольких компонентов: брать для всех
/// один и тот же индекс нельзя - фамилия, имя и отчество оказались бы жёстко
/// связаны, и мощность пула упала бы до длины одного словаря.
/// </summary>
public struct IndexStream
{
    private ulong _state;

    public IndexStream(ulong index) => _state = index;

    /// <summary>Следующее число из диапазона от нуля до <paramref name="bound"/>, не включая.</summary>
    public ulong Next(ulong bound)
    {
        if (bound == 0) throw new ArgumentOutOfRangeException(nameof(bound), "Пустой диапазон");

        var limit = ulong.MaxValue - ulong.MaxValue % bound;

        ulong value;
        do
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            value = z ^ (z >> 31);
        }
        while (value >= limit);

        return value % bound;
    }
}
