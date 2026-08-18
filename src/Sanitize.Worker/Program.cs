using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sanitize.Adapters.Postgres;
using Sanitize.Core.Classification;
using Sanitize.Core.Planning;
using Sanitize.Core.Policy;
using Sanitize.Core.Rendering;
using Sanitize.Core.Replacement;
using Sanitize.Core.Values;
using Sanitize.Worker;

// Воркер прогона: четыре стадии из раздела 5 архитектуры.
//
// Порядок обратный привычному «сначала API»: контур управления бесполезен
// без корректного прогона, а корректность прогона через API не проверяется.

var command = args.Length > 0 ? args[0] : "run";
var settings = RunSettings.FromEnvironment();

try
{
    return command switch
    {
        "run" => await RunOnceAsync(args).ConfigureAwait(false),
        "serve" => await ServeAsync().ConfigureAwait(false),
        _ => Usage(command)
    };
}
catch (Exception error)
{
    await Console.Error.WriteLineAsync($"прогон прерван: {error.Message}").ConfigureAwait(false);
    return 1;
}

int Usage(string unknown)
{
    Console.Error.WriteLine($"Неизвестная команда: {unknown}. Доступны: run, serve.");
    return 2;
}

async Task<int> RunOnceAsync(string[] arguments)
{
    var runId = arguments.Length > 1
        ? arguments[1]
        : "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");

    var requestedBy = arguments.Length > 2 ? arguments[2] : "оператор стенда";
    var sourceId = arguments.Length > 3 ? arguments[3] : "";
    var sinkId = arguments.Length > 4 ? arguments[4] : "";

    var startedAt = DateTime.UtcNow;
    var paths = new RunPaths(settings.WorkDirectory, settings.PublishDirectory, runId);
    var log = new RunLog(paths.Log);

    log.Write($"прогон {runId}: начало");

    // Источник и приёмник берутся по идентификатору из заявки. Строки
    // подключения живут только здесь, в плоскости данных: через контур
    // управления они не проходят никогда (раздел 2 архитектуры).
    var catalog = Catalog.Load(settings.CatalogPath);

    var source = catalog.SourceOf(sourceId.Length > 0 ? sourceId : catalog.Sources[0].Id);
    var sink = catalog.SinkOf(sinkId.Length > 0 ? sinkId : catalog.Sinks[0].Id);

    var schemas = source.Schemas.ToArray();
    var sourceDsn = source.Dsn;

    log.Write($"источник {source.Id} ({source.Title}), приёмник {sink.Id} ({sink.Title})");

    // Ветка DumpSource: чужой дамп разворачивается в промежуточную базу,
    // и дальше идёт ровно тот же конвейер. Иначе стадии анализа не по чему
    // считать агрегаты - по файлу дампа их не получить.
    if (source.IsDump)
    {
        if (settings.StagingDsn.Length == 0)
        {
            throw new InvalidOperationException(
                "Источник вида dump требует промежуточной базы: переменная SANITIZE_STAGING_DSN не задана");
        }

        var loader = new DumpLoader(settings.StagingDsn);
        var loaded = await loader.LoadAsync(source.Path, schemas, CancellationToken.None)
            .ConfigureAwait(false);

        if (!loaded.Success) throw new InvalidOperationException(loaded.Detail);

        log.Write($"{loaded.Detail}: {source.Path}");
        sourceDsn = settings.StagingDsn;
    }

    settings = settings with
    {
        SourceDsn = sourceDsn,
        TargetDsn = sink.Dsn,
        Schemas = schemas
    };

    // Артефакт модели читается вместе с отпечатком: доля значений из модели
    // в паспорте считается по нему, а не по обещанию.
    var artifactJson = await File.ReadAllTextAsync(settings.ArtifactPath).ConfigureAwait(false);
    var artifactFingerprint = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(artifactJson)));

    var artifact = ModelArtifact.FromJson(artifactJson, artifactFingerprint);
    log.Write($"артефакт модели {artifact.Version}, отпечаток {artifactFingerprint[..16]}");

    // Стадия 1: анализ.
    var analysis = new AnalysisStage(settings, log);
    var analysed = await analysis.RunAsync(artifact, CancellationToken.None).ConfigureAwait(false);

    await File.WriteAllTextAsync(paths.Policy, analysed.Policy.ToJson()).ConfigureAwait(false);

    var problems = analysed.Policy.Problems();
    if (problems.Count > 0)
    {
        // Прогон не начинается: политика с неутверждёнными колонками означает,
        // что решение о персональных данных приняла машина, а не человек.
        log.Write("политика не утверждена:");
        foreach (var problem in problems) log.Write("  " + problem);

        return 3;
    }

    // Стадия 2: план и словарь.
    var finiteDomains = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    foreach (var column in analysed.Schema)
        if (column.FiniteDomain.Count > 0)
            finiteDomains[column.Address.Qualified] = column.FiniteDomain;

    var plan = RunPlan.From(analysed.Policy, finiteDomains);
    log.Write($"план: таблиц {plan.Tables.Count}, доменов словаря {plan.Domains.Count}");

    var secret = await File.ReadAllBytesAsync(settings.SecretPath).ConfigureAwait(false);
    using var prf = new PseudoRandomFunction(secret);
    Array.Clear(secret);

    var renderer = RendererFor(artifact);

    var dictionaryStage = new DictionaryStage(settings, log);
    var domains = await dictionaryStage
        .BuildAsync(plan, paths, prf, renderer, CancellationToken.None).ConfigureAwait(false);

    // Стадия 3: перенос.
    var execution = new ExecutionStage(settings, log);
    execution.WritePlans(plan, analysed.Policy, artifact, paths);

    var dumpId = await execution.DumpAsync(plan, paths, CancellationToken.None).ConfigureAwait(false);
    await execution.RestoreAsync(paths, dumpId, CancellationToken.None).ConfigureAwait(false);

    // Стадия 4: проверка.
    var checks = new CheckStage(settings, log);
    var results = await checks
        .RunAsync(analysed.Policy, plan, analysed.Schema, CancellationToken.None).ConfigureAwait(false);

    await File.WriteAllTextAsync(paths.Checks,
        JsonSerializer.Serialize(results, RunPolicy.JsonOptions)).ConfigureAwait(false);

    var passport = PassportWriter.Build(
        paths, analysed.Policy, plan, domains, results, startedAt, requestedBy);

    PassportWriter.Save(passport, paths);

    if (passport.Publishable) PassportWriter.Publish(passport, paths);

    foreach (var result in results)
    {
        if (result.Passed) continue;

        log.Write((result.Blocking ? "ПРОВАЛ " : "замечание ") + result.Name + ": " + result.Detail);
    }

    log.Write(passport.Publishable
        ? $"прогон {runId}: артефакт пригоден к публикации"
        : $"прогон {runId}: артефакт НЕ публикуется");

    return passport.Publishable ? 0 : 4;
}

IValueRenderer RendererFor(ModelArtifact artifact)
{
    IReadOnlyList<string> Component(string name) =>
        artifact.Components.TryGetValue(name, out var values)
            ? values
            : throw new InvalidDataException($"В артефактах модели нет словаря {name}");

    var templates = new Dictionary<SemanticTypeId, IReadOnlyList<CompositionTemplate>>();

    foreach (var (type, patterns) in artifact.Templates)
    {
        var compiled = new List<CompositionTemplate>(patterns.Count);
        foreach (var pattern in patterns) compiled.Add(new CompositionTemplate(pattern));

        templates[SemanticTypeId.Of(type)] = compiled;
    }

    return new CompositeRenderer(
        new DateRenderer(Component(DateRenderer.BirthYearComponent)),
        new StructuredIdentifierRenderer(
            Component(StructuredIdentifierRenderer.InnRegionComponent),
            Component(StructuredIdentifierRenderer.PhoneOperatorComponent),
            Component(StructuredIdentifierRenderer.PassportSeriesComponent)),
        new CatalogueRenderer(new ComponentCatalogue(
            artifact.Components, templates, artifact.Fingerprint)));
}

/// <summary>
/// Долгоживущий режим: воркер сам берёт заявки из базы управления.
///
/// Направление выбрано намеренно: контур управления не обращается к воркеру
/// и не знает, где тот живёт. Обратно уходят только метаданные - статус,
/// паспорт, текст ошибки.
/// </summary>
async Task<int> ServeAsync()
{
    if (settings.ControlDsn.Length == 0)
        throw new InvalidOperationException("Режим serve требует переменной SANITIZE_CONTROL_DSN");

    var control = new ControlPlane(settings.ControlDsn);
    Console.WriteLine("воркер ждёт заявок в базе управления");

    var announced = DateTime.MinValue;

    while (true)
    {
        // Каталог перечитывается на каждом объявлении, а не при старте:
        // добавление источника не должно требовать перезапуска воркера.
        // Сбой чтения каталога не роняет воркер - иначе одна опечатка
        // в файле останавливала бы обслуживание уже зарегистрированных
        // ресурсов, - но и молчать о нём нельзя.
        if (DateTime.UtcNow - announced > TimeSpan.FromSeconds(15))
        {
            announced = DateTime.UtcNow;

            try
            {
                var known = Catalog.Load(settings.CatalogPath);

                await control.AnnounceAsync(
                    known.Sources.ToList().ConvertAll(s => (s.Id, s.Title, s.Kind)),
                    known.Sinks.ToList().ConvertAll(s => (s.Id, s.Title, s.Kind)),
                    CancellationToken.None).ConfigureAwait(false);

                // Пропущенные записи называются вслух: иначе опечатка в имени
                // переменной выглядела бы как «источник почему-то не появился».
                foreach (var note in known.Skipped)
                    Console.WriteLine($"источник каталога пропущен - {note}");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"каталог ресурсов не прочитан: {error.Message}");
            }
        }

        var request = await control.TakeAsync(CancellationToken.None).ConfigureAwait(false);

        if (request is null)
        {
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            continue;
        }

        var (runId, requestedBy, sourceId, sinkId) = request.Value;
        Console.WriteLine($"заявка {runId} от {requestedBy}: источник {sourceId}, приёмник {sinkId}");

        try
        {
            var code = await RunOnceAsync(
                new[] { "run", runId, requestedBy, request.Value.SourceId, request.Value.SinkId })
                .ConfigureAwait(false);
            var paths = Path.Combine(settings.WorkDirectory, runId, "passport.json");

            if (File.Exists(paths))
            {
                var passportJson = await File.ReadAllTextAsync(paths).ConfigureAwait(false);
                await control.FinishAsync(runId, code == 0, passportJson, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                // Паспорта нет - значит прогон не дошёл до конца. Пометить
                // заявку выполненной было бы неправдой.
                await control.FailAsync(runId, $"прогон завершился с кодом {code} без паспорта",
                    CancellationToken.None).ConfigureAwait(false);
            }

            Console.WriteLine($"заявка {runId}: код возврата {code}");
        }
        catch (Exception error)
        {
            await control.FailAsync(runId, error.Message, CancellationToken.None).ConfigureAwait(false);
            Console.Error.WriteLine($"заявка {runId}: прогон прерван: {error.Message}");
        }
    }
}
