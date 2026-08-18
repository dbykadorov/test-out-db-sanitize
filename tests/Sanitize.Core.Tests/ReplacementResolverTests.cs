using System.Collections.Concurrent;
using System.Text;
using Sanitize.Core.Replacement;
using Sanitize.Core.Values;

namespace Sanitize.Core.Tests;

public class ReplacementResolverTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("секрет-прогона-не-короче-32-байт!!");

    private static readonly SemanticTypeId LastName = SemanticTypeId.Of("last_name");
    private static readonly SemanticTypeId Street = SemanticTypeId.Of("street");

    private sealed class FakeDictionary : IReplacementDictionary
    {
        private readonly ConcurrentDictionary<string, string> _entries = new(StringComparer.Ordinal);

        public void Add(ReplacementKey key, string replacement) =>
            _entries[Convert.ToHexString(key.ToBytes())] = replacement;

        public bool TryLookup(ReplacementKey key, out string replacement) =>
            _entries.TryGetValue(Convert.ToHexString(key.ToBytes()), out replacement!);
    }

    /// <summary>Рендерер, у которого результат зависит и от типа, и от индекса.</summary>
    private sealed class FakeRenderer : IValueRenderer
    {
        private readonly HashSet<SemanticTypeId> _supported;

        public FakeRenderer(params SemanticTypeId[] supported) => _supported = supported.ToHashSet();

        public bool Supports(SemanticTypeId type) => _supported.Contains(type);

        public string Render(SemanticTypeId type, ulong index) => $"{type}-{index % 1000}";
    }

    private static ReplacementResolver Build(
        FakeDictionary dictionary,
        PseudoRandomFunction prf,
        IValueRenderer renderer,
        ICanonicalTypeIndex? typeIndex = null,
        IEnumerable<string>? exceptions = null) =>
        new(dictionary, prf, renderer, typeIndex ?? new PlanCanonicalTypeIndex(), exceptions);

    [Fact]
    public void Словарь_главнее_функции()
    {
        // Ради этого правила словарь и один на прогон: значение, попавшее
        // и в словарный домен, и в бесстатусный, обязано дать одну замену.
        var value = CanonicalValue.From("Иванов", CanonicalKind.Text);
        var dictionary = new FakeDictionary();
        dictionary.Add(ReplacementKey.For(value), "Петров");

        using var prf = new PseudoRandomFunction(Secret);
        var resolver = Build(dictionary, prf, new FakeRenderer(LastName));

        var result = resolver.Resolve(value, LastName);

        Assert.Equal("Петров", result.Value);
        Assert.Equal(ReplacementOrigin.Dictionary, result.Origin);
    }

    [Fact]
    public void Одно_значение_в_колонках_разных_типов_даёт_одну_замену()
    {
        // Главная проверка F-7 на бессловарном пути. Совпадения индекса мало:
        // если тип брать из места вызова, «Садовая» отрендерится фамилией
        // в одной колонке и улицей в другой, и сквозная замена развалится.
        var value = CanonicalValue.From("Садовая", CanonicalKind.Text);

        var typeIndex = new PlanCanonicalTypeIndex(new Dictionary<string, SemanticTypeId>
        {
            ["Садовая"] = Street
        });

        using var prf = new PseudoRandomFunction(Secret);
        var resolver = Build(new FakeDictionary(), prf, new FakeRenderer(LastName, Street), typeIndex);

        var inNameColumn = resolver.Resolve(value, LastName);
        var inStreetColumn = resolver.Resolve(value, Street);

        Assert.Equal(inStreetColumn.Value, inNameColumn.Value);
    }

    [Fact]
    public void Чего_нет_в_словаре_считает_функция()
    {
        var value = CanonicalValue.From("Сидоров", CanonicalKind.Text);
        using var prf = new PseudoRandomFunction(Secret);
        var resolver = Build(new FakeDictionary(), prf, new FakeRenderer(LastName));

        var result = resolver.Resolve(value, LastName);

        Assert.Equal(ReplacementOrigin.Function, result.Origin);
        Assert.StartsWith("last_name-", result.Value);
    }

    [Fact]
    public void Исключение_Р9_разводит_замены_по_типам()
    {
        // Осознанное отступление от буквы задания: «Садовая» как фамилия
        // и «Садовая» как улица - разные замены, иначе улица превратится
        // в фамилию и нарушится F-5.
        var value = CanonicalValue.From("Садовая", CanonicalKind.Text);

        using var prf = new PseudoRandomFunction(Secret);

        // Перечень исключений хранится отпечатками, а не значениями: иначе он
        // сам вынес бы персональные данные в политику и в паспорт.
        var fingerprints = new[] { prf.Fingerprint(value.Key) };

        var dictionary = new FakeDictionary();
        dictionary.Add(ReplacementKey.ForException(value, LastName), "Полевая-фамилия");
        dictionary.Add(ReplacementKey.ForException(value, Street), "Лесная");

        // Тип берётся из колонки: для исключений это и есть смысл отступления.
        var resolver = Build(dictionary, prf, new FakeRenderer(LastName, Street),
            new PlanCanonicalTypeIndex(), fingerprints);

        Assert.Equal("Полевая-фамилия", resolver.Resolve(value, LastName).Value);
        Assert.Equal("Лесная", resolver.Resolve(value, Street).Value);
    }

    [Fact]
    public void Исключение_Р9_переигрывает_канонический_тип()
    {
        // Исключение существует ровно потому, что значение принадлежит разным
        // типам. Свести его к каноническому типу значило бы отменить исключение:
        // обе колонки снова получили бы одну замену. Пустой индекс типов
        // этот дефект скрывал бы.
        var value = CanonicalValue.From("Садовая", CanonicalKind.Text);

        using var prf = new PseudoRandomFunction(Secret);
        var fingerprints = new[] { prf.Fingerprint(value.Key) };

        // План свёл бы значение к одному типу - для исключения это неверно.
        var typeIndex = new PlanCanonicalTypeIndex(new Dictionary<string, SemanticTypeId>
        {
            ["Садовая"] = Street
        });

        var resolver = Build(new FakeDictionary(), prf, new FakeRenderer(LastName, Street),
            typeIndex, fingerprints);

        var asLastName = resolver.Resolve(value, LastName);
        var asStreet = resolver.Resolve(value, Street);

        Assert.StartsWith("last_name-", asLastName.Value);
        Assert.StartsWith("street-", asStreet.Value);
        Assert.NotEqual(asLastName.Value, asStreet.Value);
    }

    [Fact]
    public void Исключение_меняет_и_индекс_а_не_только_рендеринг()
    {
        var value = CanonicalValue.From("Садовая", CanonicalKind.Text);
        using var prf = new PseudoRandomFunction(Secret);

        var plain = prf.IndexOf(ReplacementKey.For(value));
        var asLastName = prf.IndexOf(ReplacementKey.ForException(value, LastName));
        var asStreet = prf.IndexOf(ReplacementKey.ForException(value, Street));

        Assert.NotEqual(plain, asLastName);
        Assert.NotEqual(asLastName, asStreet);
    }

    [Fact]
    public void Кодирование_ключа_однозначно()
    {
        // Склейка через разделитель дала бы одинаковый ключ для обычного
        // значения «A|last_name» и для исключения «A» типа «last_name».
        var ambiguous = CanonicalValue.From("A|last_name", CanonicalKind.Text);
        var plain = CanonicalValue.From("A", CanonicalKind.Text);

        var first = ReplacementKey.For(ambiguous).ToBytes();
        var second = ReplacementKey.ForException(plain, LastName).ToBytes();

        Assert.False(first.SequenceEqual(second));
    }

    [Fact]
    public void Без_подтверждённых_исключений_работает_буквальное_прочтение()
    {
        var value = CanonicalValue.From("Садовая", CanonicalKind.Text);
        var dictionary = new FakeDictionary();
        dictionary.Add(ReplacementKey.For(value), "Лесная");

        using var prf = new PseudoRandomFunction(Secret);
        var resolver = Build(dictionary, prf, new FakeRenderer(LastName, Street));

        Assert.Equal("Лесная", resolver.Resolve(value, LastName).Value);
        Assert.Equal("Лесная", resolver.Resolve(value, Street).Value);
    }

    [Fact]
    public void У_неизменяемого_значения_замены_нет_вовсе()
    {
        using var prf = new PseudoRandomFunction(Secret);
        var resolver = Build(new FakeDictionary(), prf, new FakeRenderer());

        var result = resolver.Resolve(CanonicalValue.NotAKey, LastName);

        Assert.Equal(ReplacementOrigin.Unchanged, result.Origin);

        // Пустая строка вместо исходного значения затёрла бы данные, поэтому
        // прочитать замену нельзя - вызывающий обязан оставить исходное.
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Отсутствие_генератора_валит_прогон_а_не_оставляет_исходное()
    {
        var value = CanonicalValue.From("Иванов", CanonicalKind.Text);
        using var prf = new PseudoRandomFunction(Secret);
        var resolver = Build(new FakeDictionary(), prf, new FakeRenderer());

        Assert.Throws<InvalidOperationException>(() => resolver.Resolve(value, LastName));
    }

    [Fact]
    public void Изменение_набора_исключений_снаружи_не_влияет_на_прогон()
    {
        var value = CanonicalValue.From("Садовая", CanonicalKind.Text);
        using var prf = new PseudoRandomFunction(Secret);

        var mutable = new List<string>();
        var resolver = Build(new FakeDictionary(), prf, new FakeRenderer(LastName), null, mutable);

        mutable.Add(prf.Fingerprint(value.Key));

        // Резолвер снял копию при создании: иначе одно значение разошлось бы
        // по разным строкам в середине прогона.
        var result = resolver.Resolve(value, LastName);
        Assert.Equal(ReplacementOrigin.Function, result.Origin);
        Assert.Equal($"last_name-{prf.IndexOf(ReplacementKey.For(value)) % 1000}", result.Value);
    }

    [Fact]
    public void Параллельные_обработчики_дают_один_результат()
    {
        // Трансформер вызывает резолвер из пула. Расхождение здесь нарушило бы
        // F-7 незаметно: значения остались бы правдоподобными.
        var values = Enumerable.Range(0, 200)
            .Select(i => CanonicalValue.From($"значение-{i}", CanonicalKind.Text))
            .ToArray();

        using var prf = new PseudoRandomFunction(Secret);
        var resolver = Build(new FakeDictionary(), prf, new FakeRenderer(LastName));

        var expected = values.Select(v => resolver.Resolve(v, LastName).Value).ToArray();
        var actual = new string[values.Length];

        Parallel.For(0, values.Length, i => actual[i] = resolver.Resolve(values[i], LastName).Value);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Короткий_секрет_отвергается()
    {
        Assert.Throws<ArgumentException>(() =>
            new PseudoRandomFunction(Encoding.UTF8.GetBytes("коротко")));
    }

    [Fact]
    public void Разные_секреты_дают_разные_индексы()
    {
        var key = ReplacementKey.For(CanonicalValue.From("Иванов", CanonicalKind.Text));
        using var first = new PseudoRandomFunction(Secret);
        using var second = new PseudoRandomFunction(Encoding.UTF8.GetBytes("другой-секрет-длиной-более-32-байт!"));

        Assert.NotEqual(first.IndexOf(key), second.IndexOf(key));
    }

    [Fact]
    public void Индекс_совпадает_с_зафиксированным_вектором()
    {
        // Закрепляет и алгоритм, и порядок байтов: без вектора переход
        // на платформу с другим порядком тихо изменил бы все замены.
        using var prf = new PseudoRandomFunction(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef"));
        var key = ReplacementKey.For(CanonicalValue.From("Иванов", CanonicalKind.Text));

        Assert.Equal(14043331200226716518UL, prf.IndexOf(key));
    }

    [Fact]
    public void После_освобождения_функция_падает_а_не_считает_на_мусоре()
    {
        var prf = new PseudoRandomFunction(Secret);
        var key = ReplacementKey.For(CanonicalValue.From("Иванов", CanonicalKind.Text));
        prf.Dispose();

        Assert.Throws<ObjectDisposedException>(() => prf.IndexOf(key));
    }
}
