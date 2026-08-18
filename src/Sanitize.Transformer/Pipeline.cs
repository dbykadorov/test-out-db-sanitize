using System.Threading.Channels;

namespace Sanitize.Transformer;

/// <summary>
/// Конвейер трансформера: чтение потока, пул обработчиков, буфер
/// переупорядочения, запись потока.
///
/// Пул нужен потому, что потабличного параллелизма Greenmask при перекосе
/// не хватает: замер на прототипе дал ускорение 1,34 на пяти воркерах, потому
/// что вся работа сидела в одной большой таблице.
///
/// Буфер переупорядочения нужен потому, что канал `Cmd` ждёт ровно одну строку
/// на выходе на каждую строку на входе, в том же порядке. Без него пул выдавал
/// бы перемешанный поток, и соответствие строк потерялось бы.
/// </summary>
public sealed class Pipeline
{
    private readonly RowProcessor _processor;
    private readonly int _workers;
    private readonly int _window;

    public Pipeline(RowProcessor processor, int workers)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _workers = workers > 0 ? workers : Environment.ProcessorCount;

        // Окно - сколько строк может быть в работе одновременно. Оно ограничивает
        // и буфер переупорядочения: без него медленная первая строка позволила бы
        // всем последующим накопиться в памяти, и «плоская память» превратилась
        // бы в размер таблицы.
        _window = _workers * 64;
    }

    public async Task<long> RunAsync(TextReader input, TextWriter output, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        // Отмена связанная: сбой любой стадии обязан снять остальные, иначе
        // читатель навсегда повиснет на полном канале, а прогон - на ожидании.
        using var failure = CancellationTokenSource.CreateLinkedTokenSource(token);

        var incoming = Channel.CreateBounded<Item>(
            new BoundedChannelOptions(_workers * 4) { SingleWriter = true });

        var done = Channel.CreateBounded<Item>(
            new BoundedChannelOptions(_workers * 4) { SingleReader = true });

        // Разрешения выдаёт читатель, возвращает писатель - и только после того,
        // как строка ушла в поток. Так число строк в работе ограничено окном.
        using var window = new SemaphoreSlim(_window, _window);

        // Каждая стадия запускается отдельной задачей намеренно.
        //
        // Прямой вызов работал бы только на бумаге: у синхронного источника
        // (а Console.In именно такой - его ReadLineAsync возвращает уже
        // завершённую задачу) стадия чтения выполнилась бы синхронно
        // до первого настоящего ожидания, и обработчики с писателем даже
        // не начались бы. Канал `Cmd` шлёт следующую строку только после
        // ответа на предыдущую, поэтому это не задержка, а взаимная
        // блокировка на первой же строке.
        var reader = Task.Run(() => Guard(
            () => ReadAsync(input, incoming.Writer, window, failure.Token),
            incoming.Writer, failure));

        var workers = Enumerable.Range(0, _workers)
            .Select(_ => Task.Run(() => Guard(
                () => WorkAsync(incoming.Reader, done.Writer, failure.Token),
                done.Writer, failure)))
            .ToArray();

        var writer = Task.Run(() => GuardWriter(
            () => WriteInOrderAsync(done.Reader, output, window, failure.Token), failure));

        // Ждём все стадии, а не только писателя: иначе первая же ошибка
        // осталась бы незамеченной, а прогон - подвисшим.
        await Task.WhenAll(new[] { reader }.Concat(workers)).ConfigureAwait(false);
        done.Writer.TryComplete();

        return await writer.ConfigureAwait(false);
    }

    private readonly record struct Item(long Index, string Line);

    /// <summary>
    /// Закрывает свой канал и снимает остальные стадии при любом исходе.
    /// Без этого сбой обработчика оставлял бы читателя ждать вечно.
    /// </summary>
    private static async Task Guard(Func<Task> stage, ChannelWriter<Item> owned,
        CancellationTokenSource failure)
    {
        try
        {
            await stage().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            owned.TryComplete(error);
            failure.Cancel();
            throw;
        }
    }

    private static async Task<long> GuardWriter(Func<Task<long>> stage, CancellationTokenSource failure)
    {
        try
        {
            return await stage().ConfigureAwait(false);
        }
        catch
        {
            failure.Cancel();
            throw;
        }
    }

    private static async Task ReadAsync(
        TextReader input,
        ChannelWriter<Item> outgoing,
        SemaphoreSlim window,
        CancellationToken token)
    {
        var index = 0L;

        while (await input.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                // Пустая строка в канале - признак рассинхронизации, а не повод
                // тихо пропустить: канал ждёт ответ на каждую строку, и молчаливое
                // выбрасывание сместило бы всё дальнейшее соответствие.
                throw new InvalidDataException(
                    $"Пустая строка в канале на позиции {index}: поток рассинхронизирован");
            }

            await window.WaitAsync(token).ConfigureAwait(false);
            await outgoing.WriteAsync(new Item(index++, line), token).ConfigureAwait(false);
        }

        outgoing.TryComplete();
    }

    private async Task WorkAsync(
        ChannelReader<Item> incoming,
        ChannelWriter<Item> outgoing,
        CancellationToken token)
    {
        await foreach (var item in incoming.ReadAllAsync(token).ConfigureAwait(false))
        {
            var processed = _processor.Process(item.Line);
            await outgoing.WriteAsync(new Item(item.Index, processed), token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Собирает результаты в исходном порядке и возвращает разрешения окна
    /// по мере записи непрерывного начала потока.
    /// </summary>
    private static async Task<long> WriteInOrderAsync(
        ChannelReader<Item> done,
        TextWriter output,
        SemaphoreSlim window,
        CancellationToken token)
    {
        var pending = new Dictionary<long, string>();
        var next = 0L;
        var written = 0L;

        await foreach (var item in done.ReadAllAsync(token).ConfigureAwait(false))
        {
            pending[item.Index] = item.Line;

            var released = 0;
            while (pending.Remove(next, out var ready))
            {
                await output.WriteLineAsync(ready.AsMemory(), token).ConfigureAwait(false);
                next++;
                written++;
                released++;
            }

            if (released > 0)
            {
                // Поток сбрасывается сразу: Greenmask ждёт ответ на каждую строку,
                // и буферизация на нашей стороне обернулась бы таймаутом обмена.
                await output.FlushAsync(token).ConfigureAwait(false);
                window.Release(released);
            }
        }

        if (pending.Count > 0)
        {
            throw new InvalidOperationException(
                $"В буфере переупорядочения остались строки ({pending.Count}): " +
                "канал получил бы меньше строк, чем отдал.");
        }

        return written;
    }
}
