using Sanitize.Core.Domains;
using Sanitize.Core.Values;

namespace Sanitize.Core.Tests;

public class DerangementBuilderTests
{
    private static readonly string[] RawDiagnoses =
        { "A00", "B01", "C02", "D03", "E04", "F05", "G06" };

    private static readonly CanonicalValue[] Diagnoses =
        RawDiagnoses.Select(v => CanonicalValue.From(v, CanonicalKind.Text)).ToArray();

    private static CanonicalValue[] Canon(params string[] values) =>
        values.Select(v => CanonicalValue.From(v, CanonicalKind.Text)).ToArray();

    [Fact]
    public void Ни_одно_значение_не_остаётся_собой()
    {
        // Значение, оставшееся собой, - это незаменённые персональные данные.
        var result = DerangementBuilder.Build(Diagnoses, seed: 12345);

        Assert.True(result.Ok);
        Assert.All(result.Mapping, pair => Assert.NotEqual(pair.Key, pair.Value));
    }

    [Fact]
    public void Замены_остаются_внутри_домена_и_не_повторяются()
    {
        // Иначе сломается ограничение схемы (F-11) или мощность колонки (F-10).
        var result = DerangementBuilder.Build(Diagnoses, seed: 999);

        Assert.True(result.Ok);
        Assert.All(result.Mapping.Values, value => Assert.Contains(value, RawDiagnoses));
        Assert.Equal(RawDiagnoses.Length, result.Mapping.Values.Distinct().Count());
    }

    [Fact]
    public void Один_и_тот_же_секрет_даёт_одну_и_ту_же_перестановку()
    {
        // Без этого рушится воспроизводимость O-1.
        var first = DerangementBuilder.Build(Diagnoses, seed: 42);
        var second = DerangementBuilder.Build(Diagnoses, seed: 42);

        Assert.Equal(first.Mapping, second.Mapping);
    }

    [Fact]
    public void Домен_из_одного_значения_останавливает_конвейер()
    {
        var result = DerangementBuilder.Build(Canon("единственное"), seed: 1);

        Assert.False(result.Ok);
        Assert.Equal(DerangementFailure.SingleValue, result.Failure);
        Assert.Throws<InvalidOperationException>(() => result.Mapping);
    }

    [Fact]
    public void Домен_из_двух_значений_не_считается_обезличенным()
    {
        // Единственный беспорядок - обмен, он полностью предсказуем
        // и разворачивается любым получателем.
        var result = DerangementBuilder.Build(Canon("да", "нет"), seed: 1);

        Assert.False(result.Ok);
        Assert.Equal(DerangementFailure.TwoValuesOnlySwap, result.Failure);
    }

    [Fact]
    public void Пересечение_доменов_решается_паросочетанием()
    {
        var values = Canon("A", "B", "C");
        var allowed = new Dictionary<string, IReadOnlySet<string>>
        {
            ["A"] = new HashSet<string> { "B", "C" },
            ["B"] = new HashSet<string> { "A", "C" },
            ["C"] = new HashSet<string> { "A", "B" }
        };

        var result = DerangementBuilder.BuildConstrained(values, allowed, seed: 7);

        Assert.True(result.Ok);
        Assert.All(result.Mapping, pair =>
        {
            Assert.NotEqual(pair.Key, pair.Value);
            Assert.Contains(pair.Value, allowed[pair.Key]);
        });
    }

    [Fact]
    public void Бедное_пересечение_останавливает_конвейер()
    {
        // Непустого пересечения недостаточно: замены должны существовать
        // одновременно для всех значений и быть попарно различными.
        var values = Canon("A", "B", "C");
        var allowed = new Dictionary<string, IReadOnlySet<string>>
        {
            ["A"] = new HashSet<string> { "B" },
            ["B"] = new HashSet<string> { "A" },
            ["C"] = new HashSet<string> { "A" }
        };

        var result = DerangementBuilder.BuildConstrained(values, allowed, seed: 7);

        Assert.False(result.Ok);
        Assert.Equal(DerangementFailure.NoPerfectMatching, result.Failure);
    }

    [Fact]
    public void Повторы_во_входном_домене_останавливают_конвейер()
    {
        // Перестановка строится по позициям: равные строки дали бы значению
        // замену самим собой, а отображение вышло бы короче домена.
        var result = DerangementBuilder.Build(Canon("A", "B", "A", "C"), seed: 1);

        Assert.False(result.Ok);
        Assert.Equal(DerangementFailure.DuplicateValues, result.Failure);
    }

    [Fact]
    public void Домен_из_двух_значений_отвергается_и_с_ограничениями()
    {
        var allowed = new Dictionary<string, IReadOnlySet<string>>
        {
            ["да"] = new HashSet<string> { "нет" },
            ["нет"] = new HashSet<string> { "да" }
        };

        var result = DerangementBuilder.BuildConstrained(Canon("да", "нет"), allowed, seed: 7);

        Assert.False(result.Ok);
        Assert.Equal(DerangementFailure.TwoValuesOnlySwap, result.Failure);
    }

    [Fact]
    public void Паросочетание_покрывает_домен_целиком_и_без_повторов()
    {
        var values = Canon("A", "B", "C", "D");
        var allowed = values.ToDictionary(
            v => v.Key,
            v => (IReadOnlySet<string>)values.Where(o => o.Key != v.Key).Select(o => o.Key).ToHashSet());

        var result = DerangementBuilder.BuildConstrained(values, allowed, seed: 7);

        Assert.True(result.Ok);
        Assert.Equal(values.Length, result.Mapping.Count);
        Assert.Equal(values.Length, result.Mapping.Values.Distinct().Count());
    }

    [Fact]
    public void Единственное_паросочетание_отвергается_как_предсказуемое()
    {
        // Цикл A->B->C->A - единственное совершенное паросочетание без
        // неподвижных точек при этих ограничениях. Секрет в него не входит,
        // поэтому получатель восстанавливает замену по самим ограничениям.
        var values = Canon("A", "B", "C");
        var allowed = new Dictionary<string, IReadOnlySet<string>>
        {
            ["A"] = new HashSet<string> { "B" },
            ["B"] = new HashSet<string> { "C" },
            ["C"] = new HashSet<string> { "A" }
        };

        var result = DerangementBuilder.BuildConstrained(values, allowed, seed: 7);

        Assert.False(result.Ok);
        Assert.Equal(DerangementFailure.MatchingIsUnique, result.Failure);
    }

    [Fact]
    public void Паросочетание_зависит_от_секрета()
    {
        // Иначе замена не зависела бы от секрета вовсе, и Р-1 не выполнялся бы.
        var values = Canon("A", "B", "C", "D", "E", "F", "G", "H");
        var allowed = values.ToDictionary(
            v => v.Key,
            v => (IReadOnlySet<string>)values.Where(o => o.Key != v.Key).Select(o => o.Key).ToHashSet());

        var seen = new HashSet<string>();
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var result = DerangementBuilder.BuildConstrained(values, allowed, seed);
            Assert.True(result.Ok);
            seen.Add(string.Join(",", result.Mapping.OrderBy(p => p.Key).Select(p => $"{p.Key}>{p.Value}")));
        }

        Assert.True(seen.Count > 1, "паросочетание не зависит от секрета");
    }

    [Fact]
    public void Повторы_ищутся_по_канонической_форме_а_не_по_сырой_записи()
    {
        // «A» и «A » выглядят разными, но дают один ключ отображения:
        // без канонической проверки словарь потерял бы часть домена.
        var domain = new[]
        {
            CanonicalValue.From("A", CanonicalKind.Text),
            CanonicalValue.From("A ", CanonicalKind.Text),
            CanonicalValue.From("B", CanonicalKind.Text),
            CanonicalValue.From("C", CanonicalKind.Text)
        };

        var result = DerangementBuilder.Build(domain, seed: 1);

        Assert.False(result.Ok);
        Assert.Equal(DerangementFailure.DuplicateValues, result.Failure);
    }

    [Fact]
    public void Беспорядок_строится_на_домене_любого_размера_от_трёх()
    {
        for (var size = 3; size <= 64; size++)
        {
            var domain = Enumerable.Range(0, size)
                .Select(i => CanonicalValue.From($"код-{i}", CanonicalKind.Text)).ToArray();
            var result = DerangementBuilder.Build(domain, seed: (ulong)size * 7919);

            Assert.True(result.Ok, $"домен размера {size}");
            Assert.All(result.Mapping, pair => Assert.NotEqual(pair.Key, pair.Value));
            Assert.Equal(size, result.Mapping.Values.Distinct().Count());
        }
    }
}

public class SemanticTypeRegistryTests
{
    private static SemanticTypeRegistry Registry() => new(new Dictionary<SemanticTypeId, int>
    {
        [SemanticTypeId.Of("inn")] = 10,
        [SemanticTypeId.Of("phone")] = 20,
        [SemanticTypeId.Of("full_name")] = 30,
        [SemanticTypeId.Of("postal_address")] = 40
    });

    [Fact]
    public void Структурный_идентификатор_специфичнее_имени_и_адреса()
    {
        var chosen = Registry().MostSpecific(new[]
        {
            SemanticTypeId.Of("postal_address"),
            SemanticTypeId.Of("inn"),
            SemanticTypeId.Of("full_name")
        });

        Assert.Equal(SemanticTypeId.Of("inn"), chosen);
    }

    [Fact]
    public void Совпадающие_ранги_отвергаются_при_построении_реестра()
    {
        // Равные ранги сделали бы выбор зависимым от порядка перечисления,
        // а значит, недетерминированным - и одно значение рендерилось бы
        // по-разному в разных прогонах.
        Assert.Throws<ArgumentException>(() => new SemanticTypeRegistry(
            new Dictionary<SemanticTypeId, int>
            {
                [SemanticTypeId.Of("inn")] = 10,
                [SemanticTypeId.Of("phone")] = 10
            }));
    }

    [Fact]
    public void Незнакомые_типы_упорядочены_по_имени_а_не_по_порядку_перечисления()
    {
        var registry = Registry();

        var first = registry.MostSpecific(new[] { SemanticTypeId.Of("zeta"), SemanticTypeId.Of("alpha") });
        var second = registry.MostSpecific(new[] { SemanticTypeId.Of("alpha"), SemanticTypeId.Of("zeta") });

        Assert.Equal(first, second);
        Assert.Equal(SemanticTypeId.Of("alpha"), first);
    }

    [Fact]
    public void Пустой_набор_даёт_неизвестный_тип()
    {
        Assert.True(Registry().MostSpecific(Array.Empty<SemanticTypeId>()).IsUnknown);
    }

    [Fact]
    public void Новый_семантический_тип_добавляется_без_правки_ядра()
    {
        // Прямая проверка E-1 на семантических типах: тип объявляется данными,
        // а не перечислением внутри ядра.
        var extended = new SemanticTypeRegistry(new Dictionary<SemanticTypeId, int>
        {
            [SemanticTypeId.Of("inn")] = 10,
            [SemanticTypeId.Of("vehicle_plate")] = 15
        });

        Assert.True(extended.IsKnown(SemanticTypeId.Of("vehicle_plate")));
        Assert.Equal(SemanticTypeId.Of("inn"),
            extended.MostSpecific(new[] { SemanticTypeId.Of("vehicle_plate"), SemanticTypeId.Of("inn") }));
    }
}
