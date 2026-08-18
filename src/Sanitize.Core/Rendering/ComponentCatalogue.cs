using Sanitize.Core.Values;

namespace Sanitize.Core.Rendering;

/// <summary>
/// Шаблон сборки значения из компонентов.
///
/// Пример: «{last_name} {first_name} {patronymic}». Части в фигурных скобках -
/// имена словарей компонентов, всё остальное берётся дословно.
/// </summary>
public sealed record CompositionTemplate(string Pattern)
{
    /// <summary>Имена словарей, которые шаблон требует, в порядке появления.</summary>
    public IReadOnlyList<string> Components { get; } = Parse(Pattern);

    private static IReadOnlyList<string> Parse(string pattern)
    {
        ArgumentException.ThrowIfNullOrEmpty(pattern);

        var names = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '{')
            {
                if (depth > 0) throw new FormatException($"Вложенная скобка в шаблоне: {pattern}");
                depth++;
                start = i + 1;
            }
            else if (pattern[i] == '}')
            {
                if (depth == 0) throw new FormatException($"Лишняя закрывающая скобка: {pattern}");
                depth--;
                names.Add(pattern[start..i]);
            }
        }

        if (depth != 0) throw new FormatException($"Незакрытая скобка в шаблоне: {pattern}");
        if (names.Count == 0) throw new FormatException($"Шаблон без компонентов: {pattern}");

        return names;
    }
}

/// <summary>
/// Каталог артефактов, порождённых моделью: словари компонентов и шаблоны
/// сборки по семантическим типам.
///
/// Это и есть механика Р-4 и критерий F-6: **содержание** замен приходит
/// отсюда, из артефактов модели, а код только собирает значение по шаблону.
/// Мощность берётся комбинаторикой, а не длиной списка: пять тысяч фамилий
/// на пять тысяч имён на три тысячи отчеств дают порядок 10^11 различных ФИО.
///
/// Каталог - данные, а не код: новый семантический тип добавляется сюда,
/// а не в ядро (E-1).
/// </summary>
public sealed class ComponentCatalogue
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _components;
    private readonly IReadOnlyDictionary<SemanticTypeId, IReadOnlyList<CompositionTemplate>> _templates;

    /// <summary>Отпечаток артефакта, из которого построен каталог - для манифеста F-6.</summary>
    public string ArtifactFingerprint { get; }

    public ComponentCatalogue(
        IReadOnlyDictionary<string, IReadOnlyList<string>> components,
        IReadOnlyDictionary<SemanticTypeId, IReadOnlyList<CompositionTemplate>> templates,
        string artifactFingerprint)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentException.ThrowIfNullOrEmpty(artifactFingerprint);

        foreach (var (name, values) in components)
        {
            if (values.Count == 0)
                throw new ArgumentException($"Пустой словарь компонентов: {name}", nameof(components));

            if (values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            {
                // Повторы урезали бы мощность пула незаметно: комбинаторная
                // оценка считает списки различными.
                throw new ArgumentException($"Повторы в словаре компонентов: {name}", nameof(components));
            }
        }

        foreach (var (type, variants) in templates)
        {
            if (variants.Count == 0)
                throw new ArgumentException($"Тип {type} без шаблонов", nameof(templates));

            foreach (var component in variants.SelectMany(v => v.Components))
            {
                if (!components.ContainsKey(component))
                {
                    throw new ArgumentException(
                        $"Шаблон типа {type} требует словарь {component}, которого нет в каталоге",
                        nameof(templates));
                }
            }
        }

        _components = components;
        _templates = templates;
        ArtifactFingerprint = artifactFingerprint;
    }

    public bool Supports(SemanticTypeId type) => _templates.ContainsKey(type);

    /// <summary>
    /// Варианты шаблона для типа. Их несколько потому, что согласование -
    /// это содержание, а не форма: «Тихонов Лидия Игнатьевич» проходит любую
    /// формальную проверку и при этом мгновенно читается как подделка.
    /// Мужской и женский варианты со своими словарями компонентов живут
    /// в артефактах модели, а не в коде.
    /// </summary>
    public IReadOnlyList<CompositionTemplate> TemplatesOf(SemanticTypeId type) => _templates[type];

    public IReadOnlyList<string> ComponentValues(string name) => _components[name];

    /// <summary>
    /// Сколько различных значений даёт тип. Оценка комбинаторная, поэтому
    /// считается с насыщением: перемножение длин легко переполняет 64 бита,
    /// а планировщику важно лишь, хватает ли мощности домену.
    /// </summary>
    public ulong CardinalityOf(SemanticTypeId type)
    {
        var total = 0UL;

        foreach (var template in _templates[type])
        {
            var product = 1UL;

            foreach (var component in template.Components)
            {
                var count = (ulong)_components[component].Count;
                if (product > ulong.MaxValue / count) return ulong.MaxValue;
                product *= count;
            }

            if (total > ulong.MaxValue - product) return ulong.MaxValue;
            total += product;
        }

        return total;
    }
}
