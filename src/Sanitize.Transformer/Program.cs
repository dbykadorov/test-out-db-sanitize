using System.Runtime.InteropServices;
using System.Text;
using Sanitize.Core.Rendering;
using Sanitize.Core.Policy;
using Sanitize.Core.Replacement;
using Sanitize.Core.Validation;
using Sanitize.Core.Values;
using Sanitize.Dictionary;
using Sanitize.Transformer;

// Трансформер замен: долгоживущий процесс, который Greenmask запускает через
// трансформер `Cmd` и с которым общается построчно через stdin и stdout.
//
// Секрет и словарь берутся по путям из плана, а не из аргументов запуска:
// аргументы видны в списке процессов (раздел 8 архитектуры).

var planPath = args.Length > 0
    ? args[0]
    : Environment.GetEnvironmentVariable("SANITIZE_PLAN")
      ?? throw new InvalidOperationException(
          "Не задан план трансформера: первым аргументом или переменной SANITIZE_PLAN");

var plan = TransformerPlan.Load(planPath);

var secret = await File.ReadAllBytesAsync(plan.SecretPath).ConfigureAwait(false);
using var prf = new PseudoRandomFunction(secret);
Array.Clear(secret);

using var dictionary = new MappedReplacementDictionary(plan.DictionaryPath);

var catalogue = new ComponentCatalogue(
    plan.Components,
    plan.Templates.ToDictionary(
        p => SemanticTypeId.Of(p.Key),
        p => (IReadOnlyList<CompositionTemplate>)p.Value.Select(v => new CompositionTemplate(v)).ToArray()),
    plan.ArtifactFingerprint);

// Коды регионов и операторов - тоже содержание, и тоже из артефактов модели
// (решение владельца от 2026-08-18). Их отсутствие останавливает прогон:
// выдумать их самим значило бы обойти F-6.
var renderer = new CompositeRenderer(
    new DateRenderer(
        plan.Components.TryGetValue(DateRenderer.BirthYearComponent, out var years)
            ? years
            : throw new InvalidDataException(
                $"В артефактах модели нет словаря {DateRenderer.BirthYearComponent}")),
    new StructuredIdentifierRenderer(
        plan.Components.TryGetValue(StructuredIdentifierRenderer.InnRegionComponent, out var regions)
            ? regions
            : throw new InvalidDataException(
                $"В артефактах модели нет словаря {StructuredIdentifierRenderer.InnRegionComponent}"),
        plan.Components.TryGetValue(StructuredIdentifierRenderer.PhoneOperatorComponent, out var operators)
            ? operators
            : throw new InvalidDataException(
                $"В артефактах модели нет словаря {StructuredIdentifierRenderer.PhoneOperatorComponent}"),
        plan.Components.TryGetValue(StructuredIdentifierRenderer.PassportSeriesComponent, out var series)
            ? series
            : throw new InvalidDataException(
                $"В артефактах модели нет словаря {StructuredIdentifierRenderer.PassportSeriesComponent}")),
    new CatalogueRenderer(catalogue));

var typeIndex = new PlanCanonicalTypeIndex(
    plan.CrossDomainTypes.ToDictionary(p => p.Key, p => SemanticTypeId.Of(p.Value)));

var resolver = new ReplacementResolver(dictionary, prf, renderer, typeIndex, plan.ExceptionFingerprints);
var processor = new RowProcessor(plan, resolver, renderer);

var cancellation = new CancellationTokenSource();

// Greenmask завершает процесс сигналом SIGTERM, а не Ctrl-C: CancelKeyPress
// его не ловит вовсе. Стандартное действие подавляется, иначе среда выполнения
// снимет процесс, не дав конвейеру дописать уже принятые строки, и канал
// получит меньше строк, чем отдал.
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    RequestStop();
});

using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
{
    context.Cancel = true;
    RequestStop();
});

// Отмена может прийти уже после освобождения источника: тогда просить нечего,
// и падать на выходе тем более незачем - код возврата ушёл бы ненулевым,
// а прогон считался бы сорванным.
void RequestStop()
{
    try
    {
        cancellation.Cancel();
    }
    catch (ObjectDisposedException)
    {
    }
}

// Проверка плана до первой строки: неверный тип или отсутствующий генератор,
// найденные в середине потока, означали бы, что часть строк уже ушла
// получателю, а прогон падает на середине.
var validator = ValueValidator.Default();
var preflight = new List<string>();

foreach (var column in plan.Columns)
{
    if (column.ColumnMode == ColumnMode.TextSpot)
    {
        preflight.Add($"колонка {column.Name}: точечная обработка текста в этом срезе не реализована");
        continue;
    }

    // У колонки с беспорядком в конечном домене генератора нет и быть не должно:
    // все значения приходят из словаря прогона, построенного по самому домену.
    // Значение, которого в словаре не окажется, уронит прогон громко - это
    // и есть нужное поведение, потому что тихая замена ушла бы за пределы
    // ограничения схемы.
    if (column.ReplacementStrategy == ReplacementStrategy.Derangement) continue;

    foreach (var type in TypesOf(column)) Check(column.Name, type);
}

IEnumerable<SemanticTypeId> TypesOf(ColumnPlan column)
{
    switch (column.ColumnMode)
    {
        case ColumnMode.TextWipe:
            yield return SemanticTypeId.Of("free_text");
            break;

        case ColumnMode.Document:
            // Неразмеченные пути затираются, поэтому тип свободного текста
            // нужен документу всегда, а не только при наличии таких путей.
            yield return SemanticTypeId.Of("free_text");
            foreach (var name in column.DocumentPaths.Values) yield return SemanticTypeId.Of(name);
            break;

        default:
            yield return column.SemanticType;
            break;
    }
}

void Check(string columnName, SemanticTypeId type)
{
    if (!renderer.Supports(type))
    {
        preflight.Add($"колонка {columnName}: нет генератора для типа {type}");
        return;
    }

    if (!validator.Covers(type))
    {
        preflight.Add($"колонка {columnName}: нет валидатора для типа {type} (F-5)");
        return;
    }

    // Пул проверяется выборкой на месте: сто процентов проверенных значений
    // пула дают сто процентов проверенных подстановок из него.
    var sample = Enumerable.Range(0, 256).Select(i => renderer.Render(type, (ulong)i));
    var bad = validator.Validate(type, sample);

    if (bad.Count > 0)
        preflight.Add($"колонка {columnName}: значения типа {type} не проходят проверку F-5");
}

if (preflight.Count > 0)
{
    await Console.Error.WriteLineAsync(
        $"прогон {plan.RunId}: план не принят:\n  " + string.Join("\n  ", preflight)).ConfigureAwait(false);
    return 2;
}

var pipeline = new Pipeline(processor, plan.Workers);

// Потоки открываются напрямую, а не через Console.In и Console.Out.
// Console.In синхронен по своей природе, а Console.Out на каждую строку
// сбрасывается сам - и то и другое здесь мешает: нам нужно настоящее
// асинхронное чтение и сброс ровно тогда, когда непрерывный кусок ответа
// готов целиком.
using var input = new StreamReader(
    Console.OpenStandardInput(), new UTF8Encoding(false), false, 64 * 1024);

using var output = new StreamWriter(
    Console.OpenStandardOutput(), new UTF8Encoding(false), 64 * 1024) { AutoFlush = false };

try
{
    var written = await pipeline.RunAsync(input, output, cancellation.Token)
        .ConfigureAwait(false);

    // Итоговая сводка идёт в поток ошибок: stdout занят данными, и любая
    // посторонняя строка там сломала бы соответствие строк.
    await Console.Error.WriteLineAsync(
        $"прогон {plan.RunId}: строк {processor.Stats.Rows}, " +
        $"заменено значений {processor.Stats.Replaced}, " +
        $"оставлено без замены {processor.Stats.Unchanged}, " +
        $"отдано строк {written}").ConfigureAwait(false);

    return 0;
}
catch (OperationCanceledException)
{
    await Console.Error.WriteLineAsync($"прогон {plan.RunId}: завершение по сигналу")
        .ConfigureAwait(false);
    return 0;
}
catch (Exception error)
{
    // Молча отдать необработанную строку нельзя: это была бы незаменённая ПДн.
    await Console.Error.WriteLineAsync($"прогон {plan.RunId}: сбой трансформера: {error.Message}")
        .ConfigureAwait(false);
    return 1;
}
finally
{
    cancellation.Dispose();
}
