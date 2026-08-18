using System.Text.Json;
using System.Text.Json.Nodes;
using Sanitize.Core.Policy;
using Sanitize.Core.Rendering;
using Sanitize.Core.Replacement;
using Sanitize.Core.Values;

namespace Sanitize.Transformer;

/// <summary>
/// Обрабатывает одну строку в формате драйвера `json` трансформера `Cmd`.
///
/// Формат канала: объект, ключи - имена колонок, значение каждой - объект
/// с полями `d` (данные) и `n` (признак отсутствующего значения). Программа
/// обязана вернуть объект ЦЕЛИКОМ, изменив только нужные колонки: канал ждёт
/// ровно одну строку на выходе на каждую строку на входе.
///
/// Экземпляр потокобезопасен: состояния между строками нет, а зависимости
/// потокобезопасны по своим контрактам.
/// </summary>
public sealed class RowProcessor
{
    private readonly IReadOnlyDictionary<string, ColumnPlan> _columns;
    private readonly ReplacementResolver _resolver;
    private readonly IValueRenderer _renderer;
    private readonly SemanticTypeId _textType;

    public RowProcessor(TransformerPlan plan, ReplacementResolver resolver, IValueRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(plan);

        _columns = plan.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _textType = SemanticTypeId.Of("free_text");
    }

    /// <summary>Счётчики прогона. Значений данных в них не попадает (раздел 8).</summary>
    public sealed class Counters
    {
        private long _rows;
        private long _replaced;
        private long _unchanged;

        public long Rows => Interlocked.Read(ref _rows);
        public long Replaced => Interlocked.Read(ref _replaced);
        public long Unchanged => Interlocked.Read(ref _unchanged);

        internal void CountRow() => Interlocked.Increment(ref _rows);
        internal void CountReplaced() => Interlocked.Increment(ref _replaced);
        internal void CountUnchanged() => Interlocked.Increment(ref _unchanged);
    }

    public Counters Stats { get; } = new();

    public string Process(string line)
    {
        var row = JsonNode.Parse(line)?.AsObject()
                  ?? throw new InvalidDataException("Строка канала не является объектом");

        Stats.CountRow();

        foreach (var (name, plan) in _columns)
        {
            if (row[name] is not JsonObject cell) continue;

            var isNull = cell["n"]?.GetValue<bool>() ?? false;
            if (isNull)
            {
                // Отсутствующее значение ключом не является и не заменяется (F-7).
                Stats.CountUnchanged();
                continue;
            }

            var raw = cell["d"]?.GetValue<string>();

            var replaced = plan.ColumnMode switch
            {
                ColumnMode.Typed => ReplaceTyped(raw, plan),
                ColumnMode.TextWipe => WipeText(raw),
                ColumnMode.Document => ReplaceDocument(raw, plan),
                ColumnMode.TextSpot => throw new NotSupportedException(
                    "Точечная обработка текста включается решением владельца данных " +
                    "и в этом срезе не реализована: публиковать текст с неизмеренной " +
                    "полнотой нельзя."),
                _ => throw new InvalidOperationException($"Неизвестный режим колонки: {plan.Mode}")
            };

            if (replaced is null)
            {
                Stats.CountUnchanged();
                continue;
            }

            cell["d"] = replaced;
            Stats.CountReplaced();
        }

        return row.ToJsonString(SerializerOptions);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Возвращает null, если значение замене не подлежит.</summary>
    private string? ReplaceTyped(string? raw, ColumnPlan plan)
    {
        var canonical = CanonicalValue.From(raw, plan.CanonicalKind);
        var replacement = _resolver.Resolve(canonical, plan.SemanticType);

        return replacement.Origin == ReplacementOrigin.Unchanged ? null : replacement.Value;
    }

    /// <summary>
    /// Документ в чужом формате: структура остаётся, заменяются листья.
    ///
    /// Числа и логические значения не трогаются - они не бывают персональными
    /// данными сами по себе, а их подмена сломала бы отчёты потребителя.
    /// Строка по размеченному пути заменяется по своему типу; строка по пути,
    /// которого в разметке нет, затирается: неразмеченный путь означает,
    /// что его никто не смотрел, а не что он чист.
    /// </summary>
    private string? ReplaceDocument(string? raw, ColumnPlan plan)
    {
        if (string.IsNullOrEmpty(raw)) return null;

        var document = JsonNode.Parse(raw);
        if (document is null) return null;

        var changed = Walk(document, "", plan);
        return changed ? document.ToJsonString(SerializerOptions) : null;
    }

    private bool Walk(JsonNode node, string path, ColumnPlan plan)
    {
        var changed = false;

        switch (node)
        {
            case JsonObject obj:
                // Список ключей снимается заранее: замена значения по ключу
                // меняет коллекцию, и обход по живому перечислителю упал бы.
                var keys = new List<string>(obj.Count);
                foreach (var pair in obj) keys.Add(pair.Key);

                foreach (var key in keys)
                {
                    var child = obj[key];
                    if (child is null) continue;

                    var childPath = path.Length == 0 ? key : path + "." + key;

                    if (child is JsonValue)
                    {
                        var replaced = ReplaceLeaf(child, childPath, plan);
                        if (replaced is null) continue;

                        obj[key] = replaced;
                        changed = true;
                    }
                    else if (Walk(child, childPath, plan))
                    {
                        changed = true;
                    }
                }

                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var child = array[i];
                    if (child is null) continue;

                    // Индекс в путь не входит: разметка относится к полю,
                    // а не к порядковому номеру элемента.
                    if (child is JsonValue)
                    {
                        var replaced = ReplaceLeaf(child, path, plan);
                        if (replaced is null) continue;

                        array[i] = replaced;
                        changed = true;
                    }
                    else if (Walk(child, path, plan))
                    {
                        changed = true;
                    }
                }

                break;
        }

        return changed;
    }

    /// <summary>Возвращает null, если листу замена не нужна.</summary>
    private JsonNode? ReplaceLeaf(JsonNode leaf, string path, ColumnPlan plan)
    {
        if (leaf.GetValueKind() != System.Text.Json.JsonValueKind.String) return null;

        var raw = leaf.GetValue<string>();

        var type = plan.DocumentPaths.TryGetValue(path, out var name)
            ? SemanticTypeId.Of(name)
            : _textType;

        var canonical = CanonicalValue.From(raw, CanonicalKind.Text);
        if (!canonical.IsKey) return null;

        var replacement = _resolver.Resolve(canonical, type);

        return replacement.Origin == ReplacementOrigin.Unchanged
            ? null
            : JsonValue.Create(replacement.Value);
    }

    /// <summary>
    /// Умолчание архитектуры для свободного текста: содержимое заменяется
    /// целиком синтетическим текстом.
    ///
    /// Полнота обнаружения ПДн в тексте принципиально не равна ста процентам,
    /// поэтому здесь гарантия даётся по построению, а не измерением. Плата -
    /// потеря содержания текста для аналитики, и она названа в F-4a.
    /// </summary>
    private string? WipeText(string? raw)
    {
        var canonical = CanonicalValue.From(raw, CanonicalKind.Text);
        if (!canonical.IsKey) return null;

        // Затирание идёт через тот же резолвер, что и типизированные колонки.
        // Отдельный путь с несекретным хэшем давал бы одной и той же строке
        // разные замены в текстовой и в типизированной колонке - то есть
        // нарушал F-7 ровно там, где это труднее всего заметить.
        var replacement = _resolver.Resolve(canonical, _textType);

        return replacement.Origin == ReplacementOrigin.Unchanged ? null : replacement.Value;
    }
}
