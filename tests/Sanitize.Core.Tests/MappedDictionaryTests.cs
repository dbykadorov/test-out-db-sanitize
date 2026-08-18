using Sanitize.Core.Replacement;
using Sanitize.Core.Values;
using Sanitize.Dictionary;

namespace Sanitize.Core.Tests;

public class MappedDictionaryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sandict-{Guid.NewGuid():N}.bin");

    private static ReplacementKey Key(string value) =>
        ReplacementKey.For(CanonicalValue.From(value, CanonicalKind.Text));

    [Fact]
    public void Записанное_читается_обратно()
    {
        var entries = new Dictionary<ReplacementKey, string>
        {
            [Key("Иванов")] = "Ковалёв",
            [Key("Петров")] = "Тихонов",
            [Key("Сидоров")] = "Ерофеев"
        };

        var written = DictionaryWriter.Write(_path, entries);
        Assert.Equal(3, written);

        using var dictionary = new MappedReplacementDictionary(_path);

        Assert.Equal(3, dictionary.Count);
        foreach (var (key, expected) in entries)
        {
            Assert.True(dictionary.TryLookup(key, out var actual), key.ToString());
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Отсутствующий_ключ_не_находится()
    {
        DictionaryWriter.Write(_path, new Dictionary<ReplacementKey, string> { [Key("Иванов")] = "Ковалёв" });

        using var dictionary = new MappedReplacementDictionary(_path);

        Assert.False(dictionary.TryLookup(Key("Отсутствует"), out _));
    }

    [Fact]
    public void Ключи_с_общим_началом_не_путаются()
    {
        // Двоичный поиск сравнивает байты: без учёта длины ключ «Иван» нашёлся
        // бы по запросу «Иванов» и подставил чужую замену.
        var entries = new Dictionary<ReplacementKey, string>
        {
            [Key("Иван")] = "короткий",
            [Key("Иванов")] = "длинный",
            [Key("Ивановский")] = "самый длинный"
        };

        DictionaryWriter.Write(_path, entries);
        using var dictionary = new MappedReplacementDictionary(_path);

        Assert.True(dictionary.TryLookup(Key("Иванов"), out var value));
        Assert.Equal("длинный", value);
    }

    [Fact]
    public void Исключение_и_обычное_значение_не_путаются()
    {
        var value = CanonicalValue.From("Садовая", CanonicalKind.Text);
        var entries = new Dictionary<ReplacementKey, string>
        {
            [ReplacementKey.For(value)] = "обычная",
            [ReplacementKey.ForException(value, SemanticTypeId.Of("street"))] = "как улица",
            [ReplacementKey.ForException(value, SemanticTypeId.Of("last_name"))] = "как фамилия"
        };

        DictionaryWriter.Write(_path, entries);
        using var dictionary = new MappedReplacementDictionary(_path);

        Assert.True(dictionary.TryLookup(ReplacementKey.ForException(value, SemanticTypeId.Of("street")), out var street));
        Assert.Equal("как улица", street);

        Assert.True(dictionary.TryLookup(ReplacementKey.For(value), out var plain));
        Assert.Equal("обычная", plain);
    }

    [Fact]
    public void Повтор_ключа_отвергается_при_записи()
    {
        // Два значения на один ключ означают, что F-7 нарушен уже при
        // построении словаря, а не при подстановке.
        var entries = new[]
        {
            new KeyValuePair<ReplacementKey, string>(Key("Иванов"), "первая"),
            new KeyValuePair<ReplacementKey, string>(Key("Иванов"), "вторая")
        };

        Assert.Throws<ArgumentException>(() => DictionaryWriter.Write(_path, entries));
    }

    [Fact]
    public void Большой_словарь_читается_целиком()
    {
        var entries = Enumerable.Range(0, 20_000)
            .ToDictionary(i => Key($"значение-{i}"), i => $"замена-{i}");

        DictionaryWriter.Write(_path, entries);
        using var dictionary = new MappedReplacementDictionary(_path);

        Assert.Equal(20_000, dictionary.Count);

        foreach (var i in new[] { 0, 1, 999, 10_000, 19_999 })
        {
            Assert.True(dictionary.TryLookup(Key($"значение-{i}"), out var value));
            Assert.Equal($"замена-{i}", value);
        }
    }

    [Fact]
    public void Чужой_файл_отвергается()
    {
        File.WriteAllText(_path, "это не словарь прогона, а случайный файл");

        Assert.Throws<InvalidDataException>(() => new MappedReplacementDictionary(_path));
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    /// <summary>
    /// Словарь читается из пула обработчиков, поэтому одновременный поиск
    /// обязан находить то же, что и последовательный.
    ///
    /// Тест написан по следам настоящего прогона: чтение через
    /// MemoryMappedViewAccessor теряло 92 ключа из 2000, и потерянные значения
    /// молча уходили на путь чистой функции. Ошибки не возникало ни одной -
    /// портились только данные, и заметно это становилось лишь по числу
    /// различных значений в результате.
    /// </summary>
    [Fact]
    public void Одновременный_поиск_находит_то_же_что_и_последовательный()
    {
        const int count = 4000;

        var entries = new List<KeyValuePair<ReplacementKey, string>>(count);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < count; i++)
        {
            // Ключи намеренно разной природы: латиница, кириллица и даты.
            // Сравнение байтовое, и ошибка в нём проявляется именно на границе
            // однобайтовых и многобайтовых символов.
            var raw = (i % 3) switch
            {
                0 => $"value-{i:D6}@example.com",
                1 => $"Фамилия-{i:D6}",
                _ => $"19{50 + i % 50:D2}-{1 + i % 12:D2}-{1 + i % 28:D2}-{i:D6}"
            };

            var key = ReplacementKey.For(CanonicalValue.From(raw, CanonicalKind.Text));

            entries.Add(new KeyValuePair<ReplacementKey, string>(key, $"замена-{i:D6}"));
            expected[raw] = $"замена-{i:D6}";
        }

        DictionaryWriter.Write(_path, entries);

        using var dictionary = new MappedReplacementDictionary(_path);

        var misses = 0;
        var wrong = 0;

        Parallel.For(0, count * 4, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
        {
            var raw = expected.Keys.ElementAt(i % count);
            var key = ReplacementKey.For(CanonicalValue.From(raw, CanonicalKind.Text));

            if (!dictionary.TryLookup(key, out var found))
            {
                Interlocked.Increment(ref misses);
                return;
            }

            if (!string.Equals(found, expected[raw], StringComparison.Ordinal))
                Interlocked.Increment(ref wrong);
        });

        Assert.Equal(0, misses);
        Assert.Equal(0, wrong);
    }
}
