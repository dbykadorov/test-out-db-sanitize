using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sanitize.Core.Rendering;
using Sanitize.Core.Replacement;
using Sanitize.Core.Values;
using Sanitize.Dictionary;
using Sanitize.Transformer;

namespace Sanitize.Transformer.Tests;

/// <summary>
/// Проверяет контракт канала `Cmd`: одна строка на входе - одна на выходе,
/// в том же порядке, объект возвращается целиком.
///
/// Нарушение любого из трёх пунктов роняет прогон в середине, и заметно это
/// становится на объёме, а не на демонстрации.
/// </summary>
public class PipelineTests : IDisposable
{
    private readonly string _dictionaryPath =
        Path.Combine(Path.GetTempPath(), $"sandict-{Guid.NewGuid():N}.bin");

    private static readonly SemanticTypeId LastName = SemanticTypeId.Of("last_name");
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("секрет-прогона-не-короче-32-байт!!");

    private static TransformerPlan Plan(string dictionaryPath) => new()
    {
        RunId = "проверка",
        DictionaryPath = dictionaryPath,
        SecretPath = "не используется в тесте",
        Workers = 4,
        Columns = new[]
        {
            new ColumnPlan { Name = "surname", Kind = "Text", Type = "last_name" },
            new ColumnPlan { Name = "note", Kind = "Text", Type = "free_text", Mode = "TextWipe" }
        },
        Components = new Dictionary<string, IReadOnlyList<string>>
        {
            ["last_name"] = new[] { "Ковалёв", "Тихонов", "Ерофеев", "Панкратова" },
            ["free_text"] = new[] { "текст один", "текст два", "текст три" }
        },
        Templates = new Dictionary<string, IReadOnlyList<string>>
        {
            ["last_name"] = new[] { "{last_name}" },
            ["free_text"] = new[] { "{free_text}" }
        },
        ArtifactFingerprint = "отпечаток"
    };

    private RowProcessor BuildProcessor(out PseudoRandomFunction prf, out MappedReplacementDictionary dictionary)
    {
        DictionaryWriter.Write(_dictionaryPath, new Dictionary<ReplacementKey, string>
        {
            [ReplacementKey.For(CanonicalValue.From("Иванов", CanonicalKind.Text))] = "Ковалёв"
        });

        var plan = Plan(_dictionaryPath);

        prf = new PseudoRandomFunction(Secret);
        dictionary = new MappedReplacementDictionary(_dictionaryPath);

        var catalogue = new ComponentCatalogue(
            plan.Components,
            plan.Templates.ToDictionary(
                p => SemanticTypeId.Of(p.Key),
                p => (IReadOnlyList<CompositionTemplate>)p.Value.Select(v => new CompositionTemplate(v)).ToArray()),
            plan.ArtifactFingerprint);

        var renderer = new CompositeRenderer(
            new StructuredIdentifierRenderer(new[] { "77" }, new[] { "916" }, new[] { "4510" }),
            new CatalogueRenderer(catalogue));

        var resolver = new ReplacementResolver(dictionary, prf, renderer, new PlanCanonicalTypeIndex());

        return new RowProcessor(plan, resolver, renderer);
    }

    private static string Row(string? surname, string? note) =>
        new JsonObject
        {
            ["id"] = new JsonObject { ["d"] = "1", ["n"] = false },
            ["surname"] = new JsonObject { ["d"] = surname, ["n"] = surname is null },
            ["note"] = new JsonObject { ["d"] = note, ["n"] = note is null }
        }.ToJsonString();

    /// <summary>
    /// Ответ обязан прийти ДО того, как входной поток закроется.
    ///
    /// Канал `Cmd` синхронный: Greenmask отдаёт следующую строку только получив
    /// ответ на предыдущую. Конвейер, который отвечает лишь по концу потока,
    /// проходит все обычные тесты - там вход заканчивается сразу, - и намертво
    /// встаёт в настоящем прогоне на первой же строке. Ровно это и случилось
    /// с синхронным Console.In: чтение выполнялось до первого настоящего
    /// ожидания и не давало стартовать ни обработчикам, ни писателю.
    /// </summary>
    [Fact]
    public async Task Ответ_на_строку_приходит_до_закрытия_входного_потока()
    {
        var processor = BuildProcessor(out var prf, out var dictionary);
        using (prf)
        using (dictionary)
        {
            // Источник, который отдаёт одну строку и дальше молчит, не закрываясь, -
            // это и есть поведение канала между строками.
            using var input = new BlockingReader(Row("Иванов", "заметка") + "\n");
            var output = new SignallingWriter();

            var pipeline = new Pipeline(processor, workers: 4);

            // Прогон запускается отдельной задачей: у синхронного источника
            // сам вызов RunAsync не вернёт управление, и тест повис бы вместо
            // того, чтобы честно упасть по времени ожидания.
            var run = Task.Run(() => pipeline.RunAsync(input, output, CancellationToken.None));

            var answered = await output.FirstLine.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Contains("Ковалёв", answered);

            input.Close();
            await run;
        }
    }

    /// <summary>
    /// Ведёт себя как Console.In: отдаёт строку, а дальше БЛОКИРУЕТ вызывающий
    /// поток, возвращая уже завершённую задачу.
    ///
    /// Асинхронная заглушка здесь не годится: она сама отдаёт управление,
    /// и дефект, ради которого написан тест, не воспроизводится.
    /// </summary>
    private sealed class BlockingReader : TextReader
    {
        private readonly StringReader _head;
        private readonly ManualResetEventSlim _closed = new(false);
        private bool _headDrained;

        public BlockingReader(string head) => _head = new StringReader(head);

        public override void Close() => _closed.Set();

        private string? Next()
        {
            if (!_headDrained)
            {
                var line = _head.ReadLine();
                if (line is not null) return line;

                _headDrained = true;
            }

            _closed.Wait();
            return null;
        }

        public override string? ReadLine() => Next();

        public override Task<string?> ReadLineAsync() => Task.FromResult(Next());

        public override ValueTask<string?> ReadLineAsync(CancellationToken token) =>
            new(Next());
    }

    /// <summary>Сообщает о первой записанной строке, не дожидаясь конца прогона.</summary>
    private sealed class SignallingWriter : TextWriter
    {
        private readonly TaskCompletionSource<string> _first =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> FirstLine => _first.Task;

        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken token = default)
        {
            _first.TrySetResult(buffer.ToString());
            return Task.CompletedTask;
        }

        public override void WriteLine(string? value)
        {
            if (value is not null) _first.TrySetResult(value);
        }
    }

    [Fact]
    public async Task Строк_на_выходе_столько_же_сколько_на_входе_и_в_том_же_порядке()
    {
        var processor = BuildProcessor(out var prf, out var dictionary);
        using (prf)
        using (dictionary)
        {
            var input = new StringBuilder();
            const int count = 500;
            for (var i = 0; i < count; i++)
            {
                input.AppendLine(new JsonObject
                {
                    ["id"] = new JsonObject { ["d"] = i.ToString(), ["n"] = false },
                    ["surname"] = new JsonObject { ["d"] = $"Фамилия{i}", ["n"] = false },
                    ["note"] = new JsonObject { ["d"] = "заметка", ["n"] = false }
                }.ToJsonString());
            }

            var output = new StringWriter();
            var written = await new Pipeline(processor, workers: 8)
                .RunAsync(new StringReader(input.ToString()), output, CancellationToken.None);

            var lines = output.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(count, written);
            Assert.Equal(count, lines.Length);

            // Порядок: колонка id не заменяется, поэтому по ней видно,
            // не перемешал ли пул поток.
            for (var i = 0; i < count; i++)
            {
                var row = JsonNode.Parse(lines[i])!.AsObject();
                Assert.Equal(i.ToString(), row["id"]!["d"]!.GetValue<string>());
            }
        }
    }

    [Fact]
    public async Task Объект_возвращается_целиком_а_не_только_заменённые_колонки()
    {
        var processor = BuildProcessor(out var prf, out var dictionary);
        using (prf)
        using (dictionary)
        {
            var output = new StringWriter();
            await new Pipeline(processor, workers: 1)
                .RunAsync(new StringReader(Row("Иванов", "заметка") + "\n"), output, CancellationToken.None);

            var row = JsonNode.Parse(output.ToString().Trim())!.AsObject();

            Assert.True(row.ContainsKey("id"), "колонка без замены пропала из ответа");
            Assert.Equal("1", row["id"]!["d"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Значение_из_словаря_подставляется_а_текст_затирается()
    {
        var processor = BuildProcessor(out var prf, out var dictionary);
        using (prf)
        using (dictionary)
        {
            var output = new StringWriter();
            await new Pipeline(processor, workers: 1)
                .RunAsync(new StringReader(Row("Иванов", "паспорт 4509 123456") + "\n"),
                    output, CancellationToken.None);

            var row = JsonNode.Parse(output.ToString().Trim())!.AsObject();

            Assert.Equal("Ковалёв", row["surname"]!["d"]!.GetValue<string>());

            var note = row["note"]!["d"]!.GetValue<string>();
            Assert.DoesNotContain("4509", note);
            Assert.DoesNotContain("паспорт", note);
        }
    }

    [Fact]
    public async Task Отсутствующее_значение_остаётся_отсутствующим()
    {
        var processor = BuildProcessor(out var prf, out var dictionary);
        using (prf)
        using (dictionary)
        {
            var output = new StringWriter();
            await new Pipeline(processor, workers: 1)
                .RunAsync(new StringReader(Row(null, null) + "\n"), output, CancellationToken.None);

            var row = JsonNode.Parse(output.ToString().Trim())!.AsObject();

            Assert.True(row["surname"]!["n"]!.GetValue<bool>());
            Assert.Equal(2, processor.Stats.Unchanged);
        }
    }

    [Fact]
    public async Task Одно_значение_заменяется_одинаково_во_всех_строках()
    {
        // Сквозная замена (F-7) на уровне потока: параллельные обработчики
        // не должны расходиться.
        var processor = BuildProcessor(out var prf, out var dictionary);
        using (prf)
        using (dictionary)
        {
            var input = new StringBuilder();
            for (var i = 0; i < 300; i++) input.AppendLine(Row("Сидоров", "заметка"));

            var output = new StringWriter();
            await new Pipeline(processor, workers: 8)
                .RunAsync(new StringReader(input.ToString()), output, CancellationToken.None);

            var replacements = output.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => JsonNode.Parse(l)!["surname"]!["d"]!.GetValue<string>())
                .Distinct()
                .ToArray();

            Assert.Single(replacements);
            Assert.NotEqual("Сидоров", replacements[0]);
        }
    }

    public void Dispose()
    {
        if (File.Exists(_dictionaryPath)) File.Delete(_dictionaryPath);
    }
}
