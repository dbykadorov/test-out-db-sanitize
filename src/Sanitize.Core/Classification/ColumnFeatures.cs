using Sanitize.Core.Policy;

namespace Sanitize.Core.Classification;

/// <summary>
/// Признаки колонки, по которым принимается решение о её чувствительности.
///
/// Ровно этот набор уходит во внешнюю модель по белому списку (раздел 8):
/// числа, перечисления и шаблон формы. Значений здесь нет намеренно -
/// <see cref="Sample"/> живёт только внутри контура и в запрос к модели
/// не попадает. За это отвечает вызывающая сторона, и это проверяется
/// сохранённым журналом исходящих запросов.
/// </summary>
public sealed record ColumnFeatures
{
    public required ColumnAddress Address { get; init; }

    /// <summary>Тип источника так, как его назвал адаптер.</summary>
    public required string DataType { get; init; }

    /// <summary>Объявленная длина, если она есть у типа.</summary>
    public int? MaxLength { get; init; }

    /// <summary>Комментарий к колонке. Во внешнюю модель не уходит никогда.</summary>
    public string Comment { get; init; } = "";

    public bool IsUnique { get; init; }
    public bool IsPrimaryKey { get; init; }
    public bool IsForeignKey { get; init; }

    /// <summary>Значения ограничения-перечисления, если домен конечен (Р-3).</summary>
    public IReadOnlyList<string> FiniteDomain { get; init; } = Array.Empty<string>();

    public long Rows { get; init; }
    public long DistinctValues { get; init; }
    public double NullShare { get; init; }
    public double AverageLength { get; init; }

    /// <summary>
    /// Выборка значений. Остаётся внутри контура: по ней работают
    /// детерминированные валидаторы и локальный разбор текста.
    /// </summary>
    public IReadOnlyList<string> Sample { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Доля значений выборки, в которых локальный детектор нашёл персональные
    /// данные. Отрицательное значение означает «детектор не запускался».
    /// </summary>
    public double TextDetectorHitShare { get; init; } = -1;
}
