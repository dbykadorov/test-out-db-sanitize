using System.Text;
using Sanitize.Core.Replacement;
using Sanitize.Core.Values;

namespace Sanitize.Core.Rendering;

/// <summary>
/// Собирает значение по шаблону из словарей компонентов, порождённых моделью.
///
/// Каждый компонент выбирается СВОИМ числом из потока: один индекс на все
/// компоненты жёстко связал бы фамилию с именем и отчеством, и мощность пула
/// упала бы до длины одного словаря вместо их произведения.
/// </summary>
public sealed class CatalogueRenderer : IValueRenderer
{
    private readonly ComponentCatalogue _catalogue;

    public CatalogueRenderer(ComponentCatalogue catalogue) =>
        _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));

    public bool Supports(SemanticTypeId type) => _catalogue.Supports(type);

    public string Render(SemanticTypeId type, ulong index)
    {
        var stream = new IndexStream(index);

        // Вариант шаблона выбирается первым числом потока: у согласованных
        // вариантов свои словари компонентов, и смешивать их нельзя.
        var variants = _catalogue.TemplatesOf(type);
        var template = variants[(int)stream.Next((ulong)variants.Count)];
        var result = new StringBuilder(template.Pattern.Length + 32);

        var i = 0;
        while (i < template.Pattern.Length)
        {
            var c = template.Pattern[i];

            if (c != '{')
            {
                result.Append(c);
                i++;
                continue;
            }

            var close = template.Pattern.IndexOf('}', i);
            var name = template.Pattern[(i + 1)..close];
            var values = _catalogue.ComponentValues(name);

            result.Append(values[(int)stream.Next((ulong)values.Count)]);
            i = close + 1;
        }

        return result.ToString();
    }
}

/// <summary>
/// Генератор структурных идентификаторов: ИНН, СНИЛС, телефон.
///
/// Граница между содержанием и формой проведена решением владельца
/// от 2026-08-18. Содержание - осмысленные префиксы: код региона в ИНН,
/// код оператора в телефоне. Они приходят из артефактов модели, как и любые
/// другие словари. Код достраивает только форму: разрядность и контрольную
/// сумму, то есть ровно те «технические достройки», которые F-6 разрешает
/// в оставшихся процентах.
///
/// Без этого на базе, где чувствительные колонки - одни идентификаторы,
/// доля значений из артефактов модели оказалась бы нулевой, и обязательное
/// требование было бы провалено формально при полностью рабочем решении.
/// </summary>
public sealed class StructuredIdentifierRenderer : IValueRenderer
{
    /// <summary>Словарь кодов регионов для ИНН: две цифры.</summary>
    public const string InnRegionComponent = "inn_region";

    /// <summary>Словарь кодов операторов для телефона: три цифры.</summary>
    public const string PhoneOperatorComponent = "phone_operator";

    /// <summary>Словарь серий паспорта: четыре цифры.</summary>
    public const string PassportSeriesComponent = "passport_series";

    private readonly IReadOnlyList<string> _innRegions;
    private readonly IReadOnlyList<string> _phoneOperators;
    private readonly IReadOnlyList<string> _passportSeries;
    private readonly IReadOnlySet<SemanticTypeId> _supported;

    /// <param name="innRegions">Коды регионов из артефакта модели.</param>
    /// <param name="phoneOperators">Коды операторов из артефакта модели.</param>
    /// <param name="passportSeries">Серии паспорта из артефакта модели.</param>
    public StructuredIdentifierRenderer(
        IReadOnlyList<string> innRegions,
        IReadOnlyList<string> phoneOperators,
        IReadOnlyList<string> passportSeries)
    {
        ArgumentNullException.ThrowIfNull(innRegions);
        ArgumentNullException.ThrowIfNull(phoneOperators);
        ArgumentNullException.ThrowIfNull(passportSeries);

        Require(innRegions, 2, nameof(innRegions));
        Require(phoneOperators, 3, nameof(phoneOperators));
        Require(passportSeries, 4, nameof(passportSeries));

        _innRegions = innRegions;
        _phoneOperators = phoneOperators;
        _passportSeries = passportSeries;

        _supported = new HashSet<SemanticTypeId>
        {
            SemanticTypeId.Of("inn_person"),
            SemanticTypeId.Of("inn_legal"),
            SemanticTypeId.Of("snils"),
            SemanticTypeId.Of("phone"),
            SemanticTypeId.Of("passport")
        };
    }

    private static void Require(IReadOnlyList<string> values, int length, string name)
    {
        if (values.Count == 0)
            throw new ArgumentException($"Пустой словарь {name}: содержание брать неоткуда", name);

        foreach (var value in values)
        {
            if (value.Length != length || !value.All(char.IsAsciiDigit))
            {
                throw new ArgumentException(
                    $"Значение {value} в словаре {name} не является кодом из {length} цифр", name);
            }
        }
    }

    public bool Supports(SemanticTypeId type) => _supported.Contains(type);

    public string Render(SemanticTypeId type, ulong index)
    {
        var stream = new IndexStream(index);

        return type.Value switch
        {
            "inn_person" => StructuredIdentifiers.InnPerson(Pick(_innRegions, ref stream), ref stream),
            "inn_legal" => StructuredIdentifiers.InnLegal(Pick(_innRegions, ref stream), ref stream),
            "snils" => StructuredIdentifiers.Snils(ref stream),
            "phone" => "+7" + Pick(_phoneOperators, ref stream) + Digits(ref stream, 7),
            "passport" => Pick(_passportSeries, ref stream) + " " + Digits(ref stream, 6),
            _ => throw new InvalidOperationException($"Нет генератора для типа {type}")
        };
    }

    private static string Pick(IReadOnlyList<string> values, ref IndexStream stream) =>
        values[(int)stream.Next((ulong)values.Count)];

    private static string Digits(ref IndexStream stream, int count)
    {
        var builder = new StringBuilder(count);
        for (var i = 0; i < count; i++) builder.Append((char)('0' + (int)stream.Next(10)));
        return builder.ToString();
    }
}

/// <summary>
/// Генератор дат рождения.
///
/// Содержание здесь - правдоподобный диапазон годов: он зависит от того, чья
/// это база, и берётся из артефакта модели наравне с прочими словарями. Код
/// достраивает месяц и день с учётом длины месяца - это форма, а не содержание.
/// </summary>
public sealed class DateRenderer : IValueRenderer
{
    /// <summary>Словарь правдоподобных годов рождения: четыре цифры.</summary>
    public const string BirthYearComponent = "birth_year";

    private static readonly SemanticTypeId BirthDate = SemanticTypeId.Of("birth_date");

    private readonly IReadOnlyList<int> _years;

    public DateRenderer(IReadOnlyList<string> years)
    {
        ArgumentNullException.ThrowIfNull(years);

        if (years.Count == 0)
            throw new ArgumentException("Пустой словарь годов: содержание брать неоткуда", nameof(years));

        var parsed = new List<int>(years.Count);

        foreach (var year in years)
        {
            if (year.Length != 4 || !year.All(char.IsAsciiDigit))
                throw new ArgumentException($"Значение {year} не является годом из четырёх цифр", nameof(years));

            parsed.Add(int.Parse(year, System.Globalization.CultureInfo.InvariantCulture));
        }

        _years = parsed;
    }

    public bool Supports(SemanticTypeId type) => type == BirthDate;

    public string Render(SemanticTypeId type, ulong index)
    {
        if (!Supports(type)) throw new InvalidOperationException($"Нет генератора для типа {type}");

        var stream = new IndexStream(index);

        var year = _years[(int)stream.Next((ulong)_years.Count)];
        var month = 1 + (int)stream.Next(12);

        // День берётся по длине конкретного месяца конкретного года: иначе
        // тридцатое февраля дошло бы до базы и уронило восстановление
        // ровно там, где мы обещали правдоподобность (F-5).
        var day = 1 + (int)stream.Next((ulong)DateTime.DaysInMonth(year, month));

        return $"{year:D4}-{month:D2}-{day:D2}";
    }
}

/// <summary>
/// Направляет запрос первому генератору, который поддерживает тип.
///
/// Порядок важен: структурные идентификаторы идут раньше каталога, потому что
/// у них форма жёстче и подмена шаблоном сломала бы контрольную сумму.
/// </summary>
public sealed class CompositeRenderer : IValueRenderer
{
    private readonly IReadOnlyList<IValueRenderer> _renderers;

    public CompositeRenderer(params IValueRenderer[] renderers)
    {
        ArgumentNullException.ThrowIfNull(renderers);
        if (renderers.Length == 0) throw new ArgumentException("Пустой набор генераторов", nameof(renderers));

        _renderers = renderers;
    }

    public bool Supports(SemanticTypeId type) => _renderers.Any(r => r.Supports(type));

    public string Render(SemanticTypeId type, ulong index)
    {
        foreach (var renderer in _renderers)
        {
            if (renderer.Supports(type)) return renderer.Render(type, index);
        }

        throw new InvalidOperationException($"Нет генератора для типа {type}");
    }
}
