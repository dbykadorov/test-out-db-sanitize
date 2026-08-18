using System.Buffers.Binary;
using System.Text;
using Sanitize.Core.Values;

namespace Sanitize.Core.Replacement;

/// <summary>
/// Ключ отображения по F-7: пара «каноническое значение, метка исключения».
///
/// Метка пуста для обычных значений и равна семантическому типу только для
/// утверждённых исключений Р-9 - когда одна и та же строка принадлежит разным
/// типам (фамилия «Садовая» и улица «Садовая») и единая замена нарушила бы
/// правдоподобность. Без подтверждения заказчика перечень исключений пуст,
/// и работает буквальное прочтение задания.
/// </summary>
public readonly record struct ReplacementKey
{
    public string Value { get; }
    public string ExceptionLabel { get; }

    private ReplacementKey(string value, string label)
    {
        Value = value;
        ExceptionLabel = label;
    }

    public static ReplacementKey For(CanonicalValue value) =>
        new(Require(value), string.Empty);

    public static ReplacementKey ForException(CanonicalValue value, SemanticTypeId type) =>
        new(Require(value), type.Value);

    private static string Require(CanonicalValue value) =>
        value.IsKey
            ? value.Key
            : throw new ArgumentException("Значение не является ключом отображения", nameof(value));

    /// <summary>
    /// Двоичное представление пары с длиной каждой части.
    ///
    /// Склейка через разделитель здесь недопустима: обычное значение
    /// «A|LastName» совпало бы с исключением для значения «A» и типа
    /// «LastName», и два разных ключа получили бы одну замену. Длина
    /// впереди делает кодирование однозначным.
    /// </summary>
    public byte[] ToBytes()
    {
        var value = Encoding.UTF8.GetBytes(Value);
        var label = Encoding.UTF8.GetBytes(ExceptionLabel);

        var buffer = new byte[8 + value.Length + label.Length];
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(0, 4), value.Length);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(4, 4), label.Length);
        value.CopyTo(buffer.AsSpan(8));
        label.CopyTo(buffer.AsSpan(8 + value.Length));

        return buffer;
    }

    public override string ToString() =>
        ExceptionLabel.Length == 0 ? Value : $"{Value} [{ExceptionLabel}]";
}

/// <summary>
/// Словарь замен прогона. Один на прогон, а не по одному на домен: иначе
/// значение, встретившееся в двух доменах, получило бы две разные замены
/// и нарушило F-7.
///
/// Реализация живёт в оболочке (дисковое хранилище рядом с трансформером);
/// ядро знает только про поиск по ключу.
///
/// Реализация обязана быть потокобезопасной на чтение: резолвер вызывается
/// из пула обработчиков.
/// </summary>
public interface IReplacementDictionary
{
    /// <summary>
    /// Ищет замену. Возвращает false, если ключа в словаре нет - тогда замену
    /// вычисляет функция без состояния.
    /// </summary>
    bool TryLookup(ReplacementKey key, out string replacement);
}

/// <summary>
/// Рендерит замену по индексу. Тип влияет только на изображение значения:
/// на выбор индекса он не влияет (F-7), а правдоподобность обеспечивает (F-5).
///
/// Реализация обязана быть потокобезопасной и детерминированной: одинаковые
/// тип и индекс всегда дают одинаковую строку.
/// </summary>
public interface IValueRenderer
{
    bool Supports(SemanticTypeId type);

    string Render(SemanticTypeId type, ulong index);
}

/// <summary>
/// Определяет канонический тип значения.
///
/// Нужен потому, что тип нельзя брать из места вызова: одно значение,
/// встреченное в колонке фамилий и в колонке улиц, отрендерилось бы двумя
/// разными способами, и F-7 нарушился бы при одинаковом индексе. Канонический
/// тип вычисляется один раз на значение - на стадии плана, когда видны все
/// домены сразу.
/// </summary>
public interface ICanonicalTypeIndex
{
    SemanticTypeId TypeOf(CanonicalValue value, SemanticTypeId columnType);
}

/// <summary>
/// Канонический тип из плана: для значений, встреченных в нескольких доменах
/// разных типов, тип задан явно; для остальных берётся тип колонки.
/// </summary>
public sealed class PlanCanonicalTypeIndex : ICanonicalTypeIndex
{
    private readonly IReadOnlyDictionary<string, SemanticTypeId> _crossDomain;

    public PlanCanonicalTypeIndex(IReadOnlyDictionary<string, SemanticTypeId>? crossDomain = null) =>
        _crossDomain = crossDomain is null
            ? new Dictionary<string, SemanticTypeId>(StringComparer.Ordinal)
            : new Dictionary<string, SemanticTypeId>(crossDomain, StringComparer.Ordinal);

    public SemanticTypeId TypeOf(CanonicalValue value, SemanticTypeId columnType) =>
        value.IsKey && _crossDomain.TryGetValue(value.Key, out var type) ? type : columnType;
}

/// <summary>Итог разрешения замены.</summary>
public readonly record struct Replacement
{
    public ReplacementOrigin Origin { get; }

    private readonly string _value;

    private Replacement(string value, ReplacementOrigin origin)
    {
        _value = value;
        Origin = origin;
    }

    /// <summary>
    /// Заменяющее значение. У неизменяемого значения его нет: вызывающий обязан
    /// оставить исходное, а не подставлять пустую строку.
    /// </summary>
    public string Value => Origin == ReplacementOrigin.Unchanged
        ? throw new InvalidOperationException(
            "Значение не подлежит замене: подставлять сюда пустую строку нельзя, " +
            "исходное значение остаётся как есть")
        : _value;

    public static Replacement Unchanged { get; } = new(string.Empty, ReplacementOrigin.Unchanged);

    public static Replacement FromDictionary(string value) => new(value, ReplacementOrigin.Dictionary);

    public static Replacement FromFunction(string value) => new(value, ReplacementOrigin.Function);
}

public enum ReplacementOrigin
{
    /// <summary>Значение не подлежит замене: отсутствующее или пустое.</summary>
    Unchanged,

    /// <summary>Замена взята из словаря прогона.</summary>
    Dictionary,

    /// <summary>Замена вычислена функцией без состояния.</summary>
    Function
}

/// <summary>
/// Разрешает замену для значения. Реализует правило «словарь главнее функции».
///
/// Два механизма сосуществуют, и порядок между ними обязателен: иначе значение,
/// встречающееся и в словарном домене, и в бесстатусном, получило бы две разные
/// замены и нарушило F-7. Правило: если ключ есть в словаре - берётся оттуда;
/// функция применяется только к тому, чего в словаре нет.
///
/// Экземпляр потокобезопасен при потокобезопасных словаре и генераторе.
/// </summary>
public sealed class ReplacementResolver
{
    private readonly IReplacementDictionary _dictionary;
    private readonly PseudoRandomFunction _prf;
    private readonly IValueRenderer _renderer;
    private readonly ICanonicalTypeIndex _typeIndex;
    private readonly HashSet<string> _exceptionFingerprints;

    /// <param name="exceptionFingerprints">
    /// Утверждённые исключения Р-9, заданные отпечатками значений с секретом
    /// прогона - не самими значениями. Пустой набор означает буквальное
    /// прочтение задания.
    /// </param>
    public ReplacementResolver(
        IReplacementDictionary dictionary,
        PseudoRandomFunction prf,
        IValueRenderer renderer,
        ICanonicalTypeIndex typeIndex,
        IEnumerable<string>? exceptionFingerprints = null)
    {
        _dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
        _prf = prf ?? throw new ArgumentNullException(nameof(prf));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _typeIndex = typeIndex ?? throw new ArgumentNullException(nameof(typeIndex));

        // Копия, а не ссылка: набор, изменённый снаружи во время прогона,
        // развёл бы замены одного значения по разным строкам.
        _exceptionFingerprints = exceptionFingerprints is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(exceptionFingerprints, StringComparer.Ordinal);
    }

    /// <param name="columnType">
    /// Тип, заявленный политикой для этой колонки. Итоговый тип может
    /// отличаться: для значения, встреченного в нескольких доменах, канонический
    /// тип задаёт план.
    /// </param>
    public Replacement Resolve(CanonicalValue value, SemanticTypeId columnType)
    {
        // Отсутствующее значение и пустая строка ключами не являются
        // и не заменяются вовсе (F-7).
        if (!value.IsKey) return Replacement.Unchanged;

        // Исключение Р-9 существует ровно потому, что значение принадлежит
        // РАЗНЫМ семантическим типам и обязано получить разные замены. Свести
        // его к каноническому типу значило бы отменить исключение: обе колонки
        // снова получили бы одну замену. Поэтому для исключений работает тип
        // колонки - контекст вхождения, - а канонический тип не применяется.
        var isException = _exceptionFingerprints.Count > 0 &&
                          _exceptionFingerprints.Contains(_prf.Fingerprint(value.Key));

        var renderType = isException ? columnType : _typeIndex.TypeOf(value, columnType);

        var key = isException
            ? ReplacementKey.ForException(value, columnType)
            : ReplacementKey.For(value);

        // Словарь главнее: он покрывает домены, где нужна биекция - ключи,
        // уникальные колонки, конечные домены с перестановкой. Конечный домен
        // отдельной ветки не имеет: беспорядок строит воркер, и его результат
        // лежит в том же словаре.
        if (_dictionary.TryLookup(key, out var fromDictionary))
            return Replacement.FromDictionary(fromDictionary);

        if (!_renderer.Supports(renderType))
        {
            throw new InvalidOperationException(
                $"Для типа {renderType} нет генератора, а значения нет в словаре. " +
                "Молча оставить исходное значение нельзя: это была бы незаменённая ПДн.");
        }

        // В индекс входит ключ целиком, вместе с меткой исключения: иначе
        // исключение Р-9 давало бы тот же индекс, что и обычное значение,
        // и разошлись бы только рендеринги.
        return Replacement.FromFunction(_renderer.Render(renderType, _prf.IndexOf(key)));
    }
}
