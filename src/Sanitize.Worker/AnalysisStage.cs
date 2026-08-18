using System.Text.Json;
using System.Text.Json.Nodes;
using Sanitize.Adapters.Postgres;
using Sanitize.Core.Classification;
using Sanitize.Core.Policy;
using Sanitize.Detection.Presidio;

namespace Sanitize.Worker;

/// <summary>Решение человека по колонке, которую разметка отдала на утверждение.</summary>
public sealed record Approval
{
    public required string Column { get; init; }
    public required bool Sensitive { get; init; }
    public string SemanticType { get; init; } = "unknown";
    public ColumnMode? Mode { get; init; }

    /// <summary>
    /// Механизм замены. Не задан - остаётся тот, что выбрал планировщик.
    ///
    /// Человек решает, ЧТО это за колонка; чем гарантировать свойство замены -
    /// вопрос не решения, а расчёта. Требовать этого от утверждающего значило
    /// бы перекладывать на него ответственность за F-10.
    /// </summary>
    public ReplacementStrategy? Strategy { get; init; }
    public string Reason { get; init; } = "";
    public required string AcceptedBy { get; init; }
}

/// <summary>
/// Стадия 1: интроспекция схемы, признаки колонок, разметка.
///
/// Результат - политика, привязанная к отпечатку схемы. Всё, что ниже порога
/// уверенности, помечается как ждущее решения человека и без этого решения
/// прогон не начинается.
/// </summary>
public sealed class AnalysisStage
{
    private readonly RunSettings _settings;
    private readonly RunLog _log;

    public AnalysisStage(RunSettings settings, RunLog log)
    {
        _settings = settings;
        _log = log;
    }

    public sealed record Result(
        RunPolicy Policy,
        IReadOnlyList<ColumnDescription> Schema,
        string SchemaFingerprint,
        bool DetectorAvailable);

    public async Task<Result> RunAsync(ModelArtifact artifact, CancellationToken token)
    {
        var introspector = new SchemaIntrospector(_settings.SourceDsn, _settings.Schemas);
        var schema = await introspector.DescribeAsync(token).ConfigureAwait(false);
        var fingerprint = SchemaIntrospector.FingerprintOf(schema);

        _log.Write($"схема: колонок {schema.Count}, отпечаток {fingerprint[..16]}");

        var probe = new PostgresProbe(_settings.SourceDsn);
        var classifier = new ColumnClassifier(artifact);

        using var detector = _settings.PresidioUrl.Length > 0
            ? new PresidioDetector(_settings.PresidioUrl)
            : null;

        var detectorReady = detector is not null &&
                            await detector.IsAvailableAsync(token).ConfigureAwait(false);

        if (detector is not null && !detectorReady)
        {
            // Молча продолжать нельзя: без детектора текстовые колонки уйдут
            // человеку как неизмеренные, и это должно быть видно, а не выясняться
            // потом по числу утверждений.
            _log.Write("детектор ПДн недоступен: текстовые колонки уйдут на утверждение");
        }

        var columns = new List<PolicyColumn>(schema.Count);

        foreach (var column in schema)
        {
            var features = await probe
                .ProfileAsync(column, _settings.SampleSize, token).ConfigureAwait(false);

            if (IsDocument(column))
            {
                columns.Add(await DocumentColumnAsync(
                    features, classifier, probe, column, token).ConfigureAwait(false));

                continue;
            }

            if (detectorReady && LooksTextual(features))
            {
                var share = await detector!
                    .HitShareAsync(Head(features.Sample, 40), _settings.Language, token)
                    .ConfigureAwait(false);

                features = features with { TextDetectorHitShare = share };
            }

            columns.Add(classifier.Classify(features));
        }

        var policy = new RunPolicy
        {
            Version = artifact.Version,
            SchemaFingerprint = fingerprint,
            NameMode = "strict",
            ArtifactFingerprint = artifact.Fingerprint,
            Columns = columns
        };

        policy = ApplyApprovals(policy);

        var sensitive = 0;
        var pending = 0;
        foreach (var column in policy.Columns)
        {
            if (column.Sensitive) sensitive++;
            if (column.NeedsApproval) pending++;
        }

        _log.Write($"разметка: чувствительных {sensitive}, ждут решения человека {pending}");

        return new Result(policy, schema, fingerprint, detectorReady);
    }

    private static bool IsDocument(ColumnDescription column) =>
        column.DataType.Contains("json", StringComparison.OrdinalIgnoreCase);

    private static bool LooksTextual(ColumnFeatures features) =>
        features.DataType.Contains("text", StringComparison.OrdinalIgnoreCase) ||
        features.AverageLength >= 40;

    private static IReadOnlyList<string> Head(IReadOnlyList<string> values, int count)
    {
        if (values.Count <= count) return values;

        var head = new List<string>(count);
        for (var i = 0; i < count; i++) head.Add(values[i]);
        return head;
    }

    /// <summary>
    /// Колонка-документ: разметка идёт по путям внутри документа.
    ///
    /// Каждый путь классифицируется теми же правилами, что и колонка: имя пути
    /// работает как имя колонки, значения по пути - как выборка. Так документ
    /// не требует отдельного набора правил, а новый путь не требует правки кода.
    /// </summary>
    private async Task<PolicyColumn> DocumentColumnAsync(
        ColumnFeatures features,
        ColumnClassifier classifier,
        PostgresProbe probe,
        ColumnDescription column,
        CancellationToken token)
    {
        var byPath = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var document in features.Sample)
        {
            JsonNode? parsed;

            try
            {
                parsed = JsonNode.Parse(document);
            }
            catch (JsonException)
            {
                // Документ, который не разбирается, - это не повод пропустить
                // колонку: она уйдёт человеку как неразмеченная.
                continue;
            }

            if (parsed is not null) Collect(parsed, "", byPath);
        }

        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = false;

        foreach (var (path, values) in byPath)
        {
            var leafName = path[(path.LastIndexOf('.') + 1)..];

            var verdict = classifier.Classify(new ColumnFeatures
            {
                Address = new ColumnAddress(column.Address.Schema, column.Address.Table, leafName),
                DataType = "text",
                Sample = values,
                Rows = features.Rows,
                AverageLength = AverageLength(values)
            });

            if (!verdict.Sensitive) continue;

            paths[path] = verdict.SemanticType;
            if (verdict.NeedsApproval) pending = true;
        }

        _log.Write($"{column.Address.Qualified}: документ, размечено путей {paths.Count} из {byPath.Count}");

        await Task.CompletedTask.ConfigureAwait(false);

        return new PolicyColumn
        {
            Schema = column.Address.Schema,
            Table = column.Address.Table,
            Column = column.Address.Column,
            DataType = column.DataType,
            Sensitive = true,
            SemanticType = "document",
            Mode = ColumnMode.Document,
            Strategy = ReplacementStrategy.Function,
            CanonicalKind = "Text",
            Confidence = paths.Count > 0 ? 0.9 : 0,
            Source = VerdictSource.ValueFormat,
            DocumentPaths = paths,
            NeedsApproval = pending
        };
    }

    private static double AverageLength(IReadOnlyList<string> values)
    {
        if (values.Count == 0) return 0;

        var total = 0L;
        foreach (var value in values) total += value.Length;
        return (double)total / values.Count;
    }

    private static void Collect(JsonNode node, string path, Dictionary<string, List<string>> byPath)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj)
                {
                    if (child is null) continue;
                    Collect(child, path.Length == 0 ? key : path + "." + key, byPath);
                }

                break;

            case JsonArray array:
                foreach (var child in array)
                {
                    if (child is not null) Collect(child, path, byPath);
                }

                break;

            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                if (!byPath.TryGetValue(path, out var values))
                {
                    values = new List<string>();
                    byPath[path] = values;
                }

                values.Add(value.GetValue<string>());
                break;
        }
    }

    /// <summary>
    /// Применяет решения человека. Это и есть «утверждение политики» из
    /// раздела 3 архитектуры: без него колонка с неуверенной разметкой
    /// останавливает прогон.
    /// </summary>
    private RunPolicy ApplyApprovals(RunPolicy policy)
    {
        if (_settings.ApprovalsPath.Length == 0 || !File.Exists(_settings.ApprovalsPath))
            return policy;

        var json = File.ReadAllText(_settings.ApprovalsPath);
        var approvals = JsonSerializer.Deserialize<List<Approval>>(json, RunPolicy.JsonOptions)
                        ?? new List<Approval>();

        var byColumn = new Dictionary<string, Approval>(StringComparer.Ordinal);
        foreach (var approval in approvals) byColumn[approval.Column] = approval;

        var columns = new List<PolicyColumn>(policy.Columns.Count);
        var applied = 0;

        foreach (var column in policy.Columns)
        {
            if (!byColumn.TryGetValue(column.Address.Qualified, out var approval))
            {
                columns.Add(column);
                continue;
            }

            applied++;

            var mode = approval.Mode ?? column.Mode;

            var strategy = approval.Sensitive
                ? approval.Strategy ?? column.Strategy
                : ReplacementStrategy.None;

            // Затирание текста словаря не требует: биекция там не нужна,
            // а словарь на миллионы разных текстов вырос бы до размера
            // самой колонки.
            if (mode is ColumnMode.TextWipe or ColumnMode.TextSpot &&
                strategy != ReplacementStrategy.None)
            {
                strategy = ReplacementStrategy.Function;
            }

            columns.Add(column with
            {
                Sensitive = approval.Sensitive,
                SemanticType = approval.Sensitive ? approval.SemanticType : "unknown",
                Mode = mode,
                Strategy = strategy,
                Reason = approval.Reason,
                Source = VerdictSource.Human,
                Confidence = 1,
                NeedsApproval = false
            });
        }

        _log.Write($"утверждение политики: применено решений человека {applied}");

        return policy with { Columns = columns };
    }
}
