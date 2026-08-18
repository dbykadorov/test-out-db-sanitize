using Sanitize.Core.Rendering;
using Sanitize.Core.Validation;
using Sanitize.Core.Values;

namespace Sanitize.Core.Tests;

public class StructuredIdentifierTests
{
    private static readonly string[] Regions = { "77", "50", "78", "23" };
    private static readonly string[] Operators = { "916", "903", "925", "999" };
    private static readonly string[] Series = { "4510", "7801", "2304", "5002" };

    private static StructuredIdentifierRenderer Renderer() => new(Regions, Operators, Series);

    [Fact]
    public void Код_региона_и_оператора_обязаны_прийти_из_артефакта()
    {
        // Содержание берёт модель. Пустой или неверный словарь - это не повод
        // выдумать значение самим: F-6 требует происхождения, а не похожести.
        Assert.Throws<ArgumentException>(() =>
            new StructuredIdentifierRenderer(Array.Empty<string>(), Operators, Series));

        Assert.Throws<ArgumentException>(() =>
            new StructuredIdentifierRenderer(new[] { "7" }, Operators, Series));

        Assert.Throws<ArgumentException>(() =>
            new StructuredIdentifierRenderer(Regions, new[] { "9160" }, Series));

        Assert.Throws<ArgumentException>(() =>
            new StructuredIdentifierRenderer(Regions, Operators, new[] { "451" }));
    }

    [Fact]
    public void Порождённый_ИНН_физлица_проходит_проверку_контрольных_сумм()
    {
        for (ulong i = 0; i < 500; i++)
        {
            var inn = Renderer().Render(SemanticTypeId.Of("inn_person"), i);

            Assert.Equal(12, inn.Length);
            Assert.Contains(inn[..2], Regions);
            Assert.True(StructuredIdentifiers.IsValidInn(inn), $"индекс {i}: {inn}");
        }
    }

    [Fact]
    public void Порождённый_ИНН_юрлица_проходит_проверку()
    {
        for (ulong i = 0; i < 500; i++)
        {
            var inn = Renderer().Render(SemanticTypeId.Of("inn_legal"), i);

            Assert.Equal(10, inn.Length);
            Assert.Contains(inn[..2], Regions);
            Assert.True(StructuredIdentifiers.IsValidInn(inn), $"индекс {i}: {inn}");
        }
    }

    [Fact]
    public void Порождённый_СНИЛС_проходит_проверку()
    {
        for (ulong i = 0; i < 500; i++)
        {
            var snils = Renderer().Render(SemanticTypeId.Of("snils"), i);
            Assert.True(StructuredIdentifiers.IsValidSnils(snils), $"индекс {i}: {snils}");
        }
    }

    [Fact]
    public void Испорченная_контрольная_цифра_отвергается()
    {
        // Иначе валидатор был бы декоративным: он обязан ловить не только
        // мусор, но и правдоподобно выглядящую подделку.
        var inn = Renderer().Render(SemanticTypeId.Of("inn_person"), 1);
        var last = inn[^1];
        var broken = inn[..^1] + (char)('0' + (last - '0' + 1) % 10);

        Assert.False(StructuredIdentifiers.IsValidInn(broken));
    }

    [Fact]
    public void Значения_различаются_на_разных_индексах()
    {
        var renderer = Renderer();
        var values = Enumerable.Range(0, 1000)
            .Select(i => renderer.Render(SemanticTypeId.Of("inn_person"), (ulong)i))
            .ToHashSet();

        // Мощность пула не должна схлопываться: без потока независимых чисел
        // старшие биты индекса выбрасывались бы, и различных значений
        // оказалось бы кратно меньше.
        Assert.True(values.Count > 990, $"различных значений всего {values.Count}");
    }

    [Fact]
    public void Один_индекс_всегда_даёт_одно_значение()
    {
        var renderer = Renderer();
        var inn = SemanticTypeId.Of("inn_person");
        var snils = SemanticTypeId.Of("snils");

        Assert.Equal(renderer.Render(inn, 12345), renderer.Render(inn, 12345));
        Assert.Equal(renderer.Render(snils, 999), renderer.Render(snils, 999));
    }
}

public class CatalogueRendererTests
{
    private static readonly SemanticTypeId FullName = SemanticTypeId.Of("full_name");

    private static ComponentCatalogue Catalogue()
    {
        var components = new Dictionary<string, IReadOnlyList<string>>
        {
            ["last_name"] = new[] { "Ковалёв", "Тихонов", "Ерофеев", "Панкратова" },
            ["first_name"] = new[] { "Пётр", "Аркадий", "Лидия" },
            ["patronymic"] = new[] { "Игнатьевич", "Аскольдовна" }
        };

        var templates = new Dictionary<SemanticTypeId, IReadOnlyList<CompositionTemplate>>
        {
            [FullName] = new[] { new CompositionTemplate("{last_name} {first_name} {patronymic}") }
        };

        return new ComponentCatalogue(components, templates, "отпечаток-артефакта-модели");
    }

    [Fact]
    public void Значение_собирается_из_компонентов_каталога()
    {
        var renderer = new CatalogueRenderer(Catalogue());
        var value = renderer.Render(FullName, 42);

        var parts = value.Split(' ');
        Assert.Equal(3, parts.Length);
        Assert.Contains(parts[0], new[] { "Ковалёв", "Тихонов", "Ерофеев", "Панкратова" });
        Assert.Contains(parts[1], new[] { "Пётр", "Аркадий", "Лидия" });
    }

    [Fact]
    public void Компоненты_выбираются_независимо()
    {
        // Один индекс на все компоненты жёстко связал бы фамилию с именем,
        // и мощность пула упала бы до длины одного словаря.
        var renderer = new CatalogueRenderer(Catalogue());

        var pairs = Enumerable.Range(0, 500)
            .Select(i => renderer.Render(FullName, (ulong)i))
            .Select(v => v.Split(' '))
            .Select(p => (p[0], p[1]))
            .ToHashSet();

        // Четыре фамилии на три имени дают двенадцать сочетаний; жёсткая связь
        // оставила бы не больше четырёх.
        Assert.True(pairs.Count > 4, $"сочетаний всего {pairs.Count}");
    }

    [Fact]
    public void Мощность_считается_произведением_словарей()
    {
        Assert.Equal(4UL * 3 * 2, Catalogue().CardinalityOf(FullName));
    }

    [Fact]
    public void Повторы_в_словаре_компонентов_отвергаются()
    {
        // Иначе комбинаторная оценка мощности врала бы, а пул оказался бы
        // беднее плана - ровно тот дефект, что уронил прототип на 20 тысячах строк.
        var components = new Dictionary<string, IReadOnlyList<string>>
        {
            ["last_name"] = new[] { "Ковалёв", "Ковалёв" }
        };

        Assert.Throws<ArgumentException>(() => new ComponentCatalogue(
            components,
            new Dictionary<SemanticTypeId, IReadOnlyList<CompositionTemplate>>(),
            "отпечаток"));
    }

    [Fact]
    public void Шаблон_без_нужного_словаря_отвергается()
    {
        var components = new Dictionary<string, IReadOnlyList<string>>
        {
            ["last_name"] = new[] { "Ковалёв" }
        };

        var templates = new Dictionary<SemanticTypeId, IReadOnlyList<CompositionTemplate>>
        {
            [FullName] = new[] { new CompositionTemplate("{last_name} {first_name}") }
        };

        Assert.Throws<ArgumentException>(() =>
            new ComponentCatalogue(components, templates, "отпечаток"));
    }

    [Fact]
    public void Незакрытая_скобка_в_шаблоне_отвергается()
    {
        Assert.Throws<FormatException>(() => new CompositionTemplate("{last_name"));
    }
}

public class ValueValidatorTests
{
    [Fact]
    public void Весь_пул_структурных_идентификаторов_проходит_проверку()
    {
        // Формальный уровень F-5: сто процентов значений пула проходят
        // валидатор типа, формата и контрольной суммы.
        var validator = ValueValidator.Default();
        var renderer = new StructuredIdentifierRenderer(
            new[] { "77", "50", "78" }, new[] { "916", "903", "925" },
            new[] { "4510", "7801", "2304" });

        foreach (var type in new[] { "inn_person", "inn_legal", "snils", "phone", "passport" })
        {
            var id = SemanticTypeId.Of(type);
            var pool = Enumerable.Range(0, 2000).Select(i => renderer.Render(id, (ulong)i));

            Assert.Empty(validator.Validate(id, pool));
        }
    }

    [Fact]
    public void Тип_без_валидатора_валит_проверку_а_не_считается_пройденным()
    {
        // Тип без проверки означает, что правдоподобность никто не измерял.
        var validator = ValueValidator.Default();

        Assert.Throws<InvalidOperationException>(() =>
            validator.Validate(SemanticTypeId.Of("неизвестный_тип"), new[] { "что-нибудь" }));
    }

    [Fact]
    public void Телефон_с_неверным_кодом_страны_отвергается()
    {
        // Ровно тот дефект, что был на прототипе: перекладка цифр превращала
        // +7 в +8, и приложение-потребитель переставало разбирать номер.
        var validator = ValueValidator.Default();
        var phone = SemanticTypeId.Of("phone");

        Assert.True(validator.IsValid(phone, "+79001234567"));
        Assert.False(validator.IsValid(phone, "+89001234567"));
        Assert.False(validator.IsValid(phone, "9001234567"));
    }

    [Fact]
    public void Значение_с_концевым_пробелом_отвергается()
    {
        var validator = ValueValidator.Default();
        Assert.False(validator.IsValid(SemanticTypeId.Of("last_name"), "Ковалёв "));
    }
}
