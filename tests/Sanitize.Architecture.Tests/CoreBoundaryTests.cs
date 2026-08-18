using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Sanitize.Architecture.Tests;

/// <summary>
/// Проверяет границу ядра.
///
/// Граница объявлена так и меняется только вместе с этим тестом:
/// в ядре живут канонизация значений, функция замены, генераторы по
/// семантическим типам, словарь и биекция, беспорядок в конечных доменах,
/// правило «словарь главнее функции», модель политики и стадии конвейера.
/// Всё остальное - адаптеры: PostgreSQL, Greenmask, разбор текста, модель.
///
/// Утверждение звучит так: в Sanitize.Core не встречается ни SQL, ни имён
/// функций СУБД, ни клиентов внешних сервисов. Требование E-1 велит объявить
/// границу ДО внесения расширения, иначе проверка подгоняется под результат.
/// Здесь она проверяется статически, чтобы расхождение всплывало на сборке,
/// а не на ревью.
///
/// Проверок две по природе: по собранной сборке (фактический граф зависимостей,
/// обойти который нельзя) и по исходникам (лексика, которую граф не покажет,
/// потому что SQL - это строка, а не ссылка).
/// </summary>
public class CoreBoundaryTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string CoreDir = Path.Combine(RepoRoot, "src", "Sanitize.Core");

    /// <summary>Сборки, которые ядру разрешены: только платформа.</summary>
    private static readonly string[] AllowedAssemblies =
    {
        "System", "netstandard", "mscorlib"
    };

    private static readonly string[] ForbiddenPackagePrefixes =
    {
        "Npgsql", "Microsoft.Data", "System.Data.SqlClient", "MySql", "Dapper",
        "EntityFramework", "Microsoft.EntityFrameworkCore", "Microsoft.SemanticKernel",
        "Hangfire", "AWSSDK", "Minio", "VaultSharp", "Bogus", "Presidio", "RestSharp",
        "Refit", "Grpc"
    };

    /// <summary>
    /// Лексика, выдающая знание о СУБД или о внешнем вызове. Ищется целыми
    /// словами: подстрочный поиск ловил бы «selected» и подобное.
    /// </summary>
    private static readonly string[] ForbiddenTokens =
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "ALTER", "CREATE",
        "DROP", "COPY", "VACUUM", "GRANT", "REVOKE", "COMMIT", "ROLLBACK",
        "pg_export_snapshot", "information_schema", "pg_catalog",
        "NpgsqlConnection", "DbConnection", "IDbConnection", "DbCommand", "SqlConnection",
        "HttpClient", "HttpMessageInvoker", "HttpRequestMessage", "WebRequest", "Socket",
        "Kernel", "greenmask", "presidio"
    };

    [Fact]
    public void Ядро_не_ссылается_на_чужие_проекты()
    {
        var csproj = XDocument.Load(Path.Combine(CoreDir, "Sanitize.Core.csproj"));
        var refs = csproj.Descendants("ProjectReference")
                         .Select(e => e.Attribute("Include")?.Value ?? "")
                         .ToArray();

        Assert.True(refs.Length == 0,
            "Ядро замен обязано быть листом графа зависимостей. Найдены ссылки: "
            + string.Join(", ", refs));
    }

    [Fact]
    public void Ядро_не_тянет_пакеты_адаптеров_и_клиентов()
    {
        var csproj = XDocument.Load(Path.Combine(CoreDir, "Sanitize.Core.csproj"));
        var packages = csproj.Descendants("PackageReference")
                             .Select(e => e.Attribute("Include")?.Value ?? "")
                             .ToArray();

        var violations = packages
            .Where(p => ForbiddenPackagePrefixes.Any(f => p.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.True(violations.Length == 0,
            "В ядре появились пакеты, нарушающие контракт адаптера: "
            + string.Join(", ", violations));
    }

    /// <summary>
    /// Проверка по собранной сборке. В отличие от чтения csproj, здесь виден
    /// фактический граф: ссылка, добавленная импортом или из общих настроек,
    /// всё равно попадёт в метаданные.
    /// </summary>
    [Fact]
    public void Собранное_ядро_ссылается_только_на_платформу()
    {
        var assemblyPath = typeof(Core.Values.CanonicalValue).Assembly.Location;

        var referenced = AssemblyName.GetAssemblyName(assemblyPath) is not null
            ? Assembly.LoadFrom(assemblyPath).GetReferencedAssemblies()
            : Array.Empty<AssemblyName>();

        var violations = referenced
            .Select(a => a.Name ?? "")
            // Точное совпадение либо префикс с точкой: голый StartsWith("System")
            // пропустил бы стороннюю сборку с именем вроде SystemDataTools.
            .Where(name => !AllowedAssemblies.Any(p =>
                name.Equals(p, StringComparison.Ordinal) ||
                name.StartsWith(p + ".", StringComparison.Ordinal)))
            .ToArray();

        Assert.True(violations.Length == 0,
            "Собранное ядро ссылается на непозволенные сборки: " + string.Join(", ", violations));
    }

    [Fact]
    public void В_коде_ядра_нет_ни_SQL_ни_имён_функций_СУБД()
    {
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(CoreDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = StripComments(lines[i]);
                if (code.Length == 0) continue;

                foreach (var token in ForbiddenTokens)
                {
                    if (Regex.IsMatch(code, $@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase))
                        violations.Add($"{Path.GetRelativePath(RepoRoot, file)}:{i + 1}: {token}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Ядро узнало о внешнем мире, а не должно было:\n" + string.Join("\n", violations));
    }

    /// <summary>
    /// Убирает только комментарии; содержимое строковых литералов остаётся
    /// под проверкой.
    ///
    /// Комментарии исключены намеренно: объяснить, ПОЧЕМУ ядро не выполняет SQL,
    /// можно только назвав SQL по имени. Литералы, наоборот, исключать нельзя -
    /// именно в них и живёт SQL, а значит именно там его надо искать. Цена:
    /// сообщения об ошибках в ядре не вправе называть запрещённые слова.
    ///
    /// Разбор посимвольный с учётом литералов: наивный поиск «//» принимал бы
    /// за комментарий последовательность внутри строки и скрывал остаток строки
    /// от проверки.
    /// </summary>
    private static string StripComments(string line)
    {
        var result = new StringBuilder(line.Length);
        var inText = false;
        var escaped = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inText)
            {
                result.Append(c);
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') inText = false;
                continue;
            }

            if (c == '"') { inText = true; result.Append(c); continue; }

            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;

            result.Append(c);
        }

        return result.ToString().Trim();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Sanitize.sln")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Не найден корень репозитория");
    }
}
