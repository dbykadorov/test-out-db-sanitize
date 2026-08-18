using System.Buffers.Binary;
using System.Text;
using Sanitize.Core.Replacement;

namespace Sanitize.Dictionary;

/// <summary>
/// Пишет словарь прогона в формат, который читает
/// <see cref="MappedReplacementDictionary"/>.
///
/// Это шаг материализации из архитектуры: словарь строится множественной
/// операцией в рабочей базе, а сюда переносится готовым, чтобы трансформер
/// читал его без обращения к СУБД.
/// </summary>
public static class DictionaryWriter
{
    /// <summary>
    /// Записывает пары «ключ отображения - замена».
    ///
    /// Дубликаты ключей отвергаются: словарь один на прогон, и два разных
    /// значения для одного ключа означали бы, что F-7 уже нарушен на этапе
    /// построения, а не при подстановке.
    /// </summary>
    public static int Write(string path, IEnumerable<KeyValuePair<ReplacementKey, string>> entries)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(entries);

        var prepared = new List<(byte[] Key, byte[] Value)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (key, replacement) in entries)
        {
            var keyBytes = key.ToBytes();
            var fingerprint = Convert.ToHexString(keyBytes);

            if (!seen.Add(fingerprint))
                throw new ArgumentException($"Ключ встречается дважды: {key}", nameof(entries));

            prepared.Add((keyBytes, Encoding.UTF8.GetBytes(replacement)));
        }

        // Поиск в словаре двоичный, поэтому порядок обязателен. Сортировка
        // байтовая: она же используется при сравнении на чтении.
        prepared.Sort((a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));

        const long headerSize = 12;
        const long entrySize = 16;

        // Именно long: таблица на 200 млн записей занимает 3,2 ГБ,
        // и произведение в int переполнилось бы молча.
        var tableSize = (long)prepared.Count * entrySize;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(MappedReplacementDictionary.Magic);

        // Число записей пишется в четыре байта: формат рассчитан на объёмы
        // до двух миллиардов ключей, дальше нужна другая версия заголовка.
        if (prepared.Count > int.MaxValue)
        {
            throw new NotSupportedException(
                $"Словарь из {prepared.Count} записей не помещается в текущую версию формата");
        }

        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(count, prepared.Count);
        writer.Write(count);

        var dataOffset = headerSize + tableSize;

        Span<byte> entry = stackalloc byte[(int)entrySize];
        foreach (var (key, value) in prepared)
        {
            BinaryPrimitives.WriteInt64LittleEndian(entry[..8], dataOffset);
            BinaryPrimitives.WriteInt32LittleEndian(entry.Slice(8, 4), key.Length);
            BinaryPrimitives.WriteInt32LittleEndian(entry.Slice(12, 4), value.Length);
            writer.Write(entry);

            dataOffset += key.Length + value.Length;
        }

        foreach (var (key, value) in prepared)
        {
            writer.Write(key);
            writer.Write(value);
        }

        writer.Flush();
        return prepared.Count;
    }
}
