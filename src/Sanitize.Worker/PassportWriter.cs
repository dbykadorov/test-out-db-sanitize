using System.Text;
using System.Text.Json;
using Sanitize.Core.Planning;
using Sanitize.Core.Policy;

namespace Sanitize.Worker;

/// <summary>
/// Паспорт выгрузки: машиночитаемый документ, сопровождающий артефакт.
///
/// Это то, что делает остаточный риск управляемым: получатель видит, что
/// именно ему передали, и подтверждает принятие. Поэтому здесь перечисляются
/// не только заменённые колонки, но и НЕзаменённые - с причинами.
/// </summary>
public sealed record Passport
{
    public required string RunId { get; init; }
    public required string StartedAtUtc { get; init; }
    public required string FinishedAtUtc { get; init; }
    public required string PolicyVersion { get; init; }
    public required string SchemaFingerprint { get; init; }
    public required string ArtifactFingerprint { get; init; }
    public required string RequestedBy { get; init; }

    public required IReadOnlyList<PassportColumn> Replaced { get; init; }
    public required IReadOnlyList<PassportColumn> Untouched { get; init; }
    public required IReadOnlyList<DomainReport> Domains { get; init; }
    public required IReadOnlyList<CheckResult> Checks { get; init; }
    public required IReadOnlyList<Departure> Departures { get; init; }
    public required IReadOnlyList<string> AcceptedRisks { get; init; }

    /// <summary>Доля различных замен, происходящих из артефактов модели (F-6).</summary>
    public required ModelOriginShare ModelOrigin { get; init; }

    public required bool Publishable { get; init; }
}

public sealed record PassportColumn
{
    public required string Column { get; init; }
    public required string SemanticType { get; init; }
    public required string Mode { get; init; }
    public required string Strategy { get; init; }
    public required string Source { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>
/// Происхождение значений замен.
///
/// Считается точно, а не оценивается: каждый семантический тип относится
/// либо к содержанию из артефактов модели, либо к технической достройке.
/// Знаменатель тоже назван, чтобы долю нельзя было улучшить умолчанием.
/// </summary>
public sealed record ModelOriginShare
{
    public required int FromModel { get; init; }
    public required int TechnicalOnly { get; init; }
    public required IReadOnlyList<string> TechnicalTypes { get; init; }

    public double Share => FromModel + TechnicalOnly == 0
        ? 1
        : (double)FromModel / (FromModel + TechnicalOnly);
}

public static class PassportWriter
{
    /// <summary>
    /// Типы, у которых осмысленного содержания нет вовсе. СНИЛС не кодирует
    /// ни региона, ни ведомства, поэтому относить его к содержанию из модели
    /// было бы неправдой - он целиком техническая достройка.
    /// </summary>
    private static readonly HashSet<string> TechnicalTypes = new(StringComparer.Ordinal)
    {
        "snils"
    };

    public static Passport Build(
        RunPaths paths,
        RunPolicy policy,
        RunPlan plan,
        IReadOnlyList<DomainReport> domains,
        IReadOnlyList<CheckResult> checks,
        DateTime startedAtUtc,
        string requestedBy)
    {
        var replaced = new List<PassportColumn>();
        var untouched = new List<PassportColumn>();

        var fromModel = 0;
        var technical = 0;
        var technicalTypes = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var column in policy.Columns)
        {
            var entry = new PassportColumn
            {
                Column = column.Address.Qualified,
                SemanticType = column.SemanticType,
                Mode = column.Mode.ToString(),
                Strategy = column.Strategy.ToString(),
                Source = column.Source.ToString(),
                Reason = column.Reason
            };

            if (column.Strategy == ReplacementStrategy.None)
            {
                untouched.Add(entry);
                continue;
            }

            replaced.Add(entry);

            if (TechnicalTypes.Contains(column.SemanticType))
            {
                technical++;
                technicalTypes.Add(column.SemanticType);
            }
            else
            {
                fromModel++;
            }
        }

        var blocking = 0;
        foreach (var check in checks)
            if (!check.Passed && check.Blocking)
                blocking++;

        var risks = new List<string>
        {
            "Квази-идентификаторы не обобщаются (Р-2): сочетание незаменённых колонок " +
            "может выделять человека. Риск принят и не закрыт.",
            "Полнота обнаружения персональных данных в свободном тексте не равна ста " +
            "процентам ни при каком пороге. Гарантия по текстовым колонкам дана " +
            "построением - затиранием целиком, а не измерением."
        };

        foreach (var reason in plan.UntouchedReasons)
            risks.Add("Незаменённая чувствительная колонка: " + reason);

        return new Passport
        {
            RunId = paths.RunId,
            StartedAtUtc = startedAtUtc.ToString("O"),
            FinishedAtUtc = DateTime.UtcNow.ToString("O"),
            PolicyVersion = policy.Version,
            SchemaFingerprint = policy.SchemaFingerprint,
            ArtifactFingerprint = policy.ArtifactFingerprint,
            RequestedBy = requestedBy,
            Replaced = replaced,
            Untouched = untouched,
            Domains = domains,
            Checks = checks,
            Departures = policy.Departures,
            AcceptedRisks = risks,
            ModelOrigin = new ModelOriginShare
            {
                FromModel = fromModel,
                TechnicalOnly = technical,
                TechnicalTypes = technicalTypes.ToList()
            },
            Publishable = blocking == 0
        };
    }

    public static void Save(Passport passport, RunPaths paths)
    {
        File.WriteAllText(paths.PassportJson,
            JsonSerializer.Serialize(passport, RunPolicy.JsonOptions));

        File.WriteAllText(paths.PassportText, Render(passport));
    }

    /// <summary>
    /// Переносит артефакт и паспорт в область публикации.
    ///
    /// Копия, а не выдача доступа к рабочей зоне: там рядом со сдаваемым
    /// дампом лежит словарь соответствий, и права на чтение рабочей зоны
    /// означали бы права на словарь - то есть обратимость обезличивания
    /// (Р-1). Непригодный артефакт сюда не попадает вовсе: частично
    /// обезличенная база опаснее отсутствующей.
    /// </summary>
    public static void Publish(Passport passport, RunPaths paths)
    {
        if (!passport.Publishable)
            throw new InvalidOperationException("Непригодный артефакт не публикуется");

        Directory.CreateDirectory(paths.Publication);

        var dump = Path.Combine(paths.Publication, "dump");
        CopyTree(paths.Storage, dump);

        File.Copy(paths.PassportJson, Path.Combine(paths.Publication, "passport.json"), overwrite: true);
        File.Copy(paths.PassportText, Path.Combine(paths.Publication, "passport.md"), overwrite: true);

        // Права выставляются только там, где это имеет смысл. Контур
        // разворачивается в контейнерах Linux; на других платформах
        // разграничение обеспечивается иначе, и молча делать вид,
        // что оно есть, нельзя.
        if (OperatingSystem.IsLinux()) Relax(paths.Publication);
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);

        foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }

    /// <summary>
    /// Открывает область публикации на чтение сервису выдачи.
    ///
    /// Рабочая зона остаётся закрытой; послабление действует только здесь
    /// и только на чтение. Настоящий контур выдал бы вместо этого отдельные
    /// учётные данные - на стенде роль такого разграничения играют права
    /// файловой системы.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static void Relax(string root)
    {
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                   UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                   UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

        File.SetUnixFileMode(root, mode);

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            File.SetUnixFileMode(directory, mode);

        var readable = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                       UnixFileMode.GroupRead | UnixFileMode.OtherRead;

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetUnixFileMode(file, readable);
    }

    private static string Render(Passport passport)
    {
        var text = new StringBuilder();

        text.AppendLine($"# Паспорт выгрузки {passport.RunId}");
        text.AppendLine();
        text.AppendLine(passport.Publishable
            ? "**Артефакт пригоден к публикации:** все блокирующие проверки пройдены."
            : "**Артефакт непригоден к публикации:** есть проваленные блокирующие проверки. " +
              "Частично обезличенная база опаснее отсутствующей, поэтому выдача запрещена.");
        text.AppendLine();

        text.AppendLine("| Поле | Значение |");
        text.AppendLine("|---|---|");
        text.AppendLine($"| Прогон | {passport.RunId} |");
        text.AppendLine($"| Начат | {passport.StartedAtUtc} |");
        text.AppendLine($"| Завершён | {passport.FinishedAtUtc} |");
        text.AppendLine($"| Заказан | {passport.RequestedBy} |");
        text.AppendLine($"| Версия политики | {passport.PolicyVersion} |");
        text.AppendLine($"| Отпечаток схемы | {passport.SchemaFingerprint[..16]} |");
        text.AppendLine($"| Отпечаток артефакта модели | {passport.ArtifactFingerprint[..16]} |");
        text.AppendLine();

        text.AppendLine("## Происхождение замен (F-6)");
        text.AppendLine();
        text.AppendLine($"Колонок с содержанием из артефактов модели: {passport.ModelOrigin.FromModel}. " +
                        $"Колонок с чисто технической достройкой: {passport.ModelOrigin.TechnicalOnly}. " +
                        $"Доля: {passport.ModelOrigin.Share * 100:F1}%.");

        if (passport.ModelOrigin.TechnicalTypes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Типы без осмысленного содержания названы явно, а не спрятаны " +
                            "в знаменателе: " + string.Join(", ", passport.ModelOrigin.TechnicalTypes) + ".");
        }

        text.AppendLine();
        text.AppendLine("## Заменённые колонки");
        text.AppendLine();
        text.AppendLine("| Колонка | Тип | Режим | Механизм | Источник вердикта |");
        text.AppendLine("|---|---|---|---|---|");

        foreach (var column in passport.Replaced)
        {
            text.AppendLine($"| {column.Column} | {column.SemanticType} | {column.Mode} | " +
                            $"{column.Strategy} | {column.Source} |");
        }

        text.AppendLine();
        text.AppendLine("## Незаменённые колонки");
        text.AppendLine();
        text.AppendLine("| Колонка | Причина |");
        text.AppendLine("|---|---|");

        foreach (var column in passport.Untouched)
            text.AppendLine($"| {column.Column} | {column.Reason} |");

        text.AppendLine();
        text.AppendLine("## Домены словаря");
        text.AppendLine();
        text.AppendLine("| Домен | Различных значений | Механизм |");
        text.AppendLine("|---|---|---|");

        foreach (var domain in passport.Domains)
            text.AppendLine($"| {domain.SemanticType} | {domain.DistinctValues} | {domain.Note} |");

        text.AppendLine();
        text.AppendLine("## Проверки");
        text.AppendLine();
        text.AppendLine("| Требование | Проверка | Итог | Подробности |");
        text.AppendLine("|---|---|---|---|");

        foreach (var check in passport.Checks)
        {
            var verdict = check.Passed
                ? "пройдена"
                : check.Blocking ? "**провалена**" : "не пройдена (неблокирующая)";

            text.AppendLine($"| {check.Requirement} | {check.Name} | {verdict} | {check.Detail} |");
        }

        if (passport.Departures.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Именованные отступления");
            text.AppendLine();
            text.AppendLine("| Требование | Отступление | Кто принял |");
            text.AppendLine("|---|---|---|");

            foreach (var departure in passport.Departures)
                text.AppendLine($"| {departure.Requirement} | {departure.What} | {departure.AcceptedBy} |");
        }

        text.AppendLine();
        text.AppendLine("## Принятые остаточные риски");
        text.AppendLine();

        foreach (var risk in passport.AcceptedRisks) text.AppendLine($"- {risk}");

        return text.ToString();
    }
}
