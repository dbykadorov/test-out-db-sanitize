using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using Microsoft.Win32.SafeHandles;
using Sanitize.Core.Replacement;

namespace Sanitize.Dictionary;

/// <summary>
/// Словарь прогона на диске, читаемый через отображение в память.
///
/// Зачем именно так. Перенос выполняет Greenmask, соединения со словарём
/// в СУБД у трансформера нет, а запрос в базу на каждую строку убил бы
/// пропускную способность. Загрузить словарь в память тоже нельзя: на целевом
/// объёме это 200 млн записей, и требование P-3 держит потолок в 32 ГБ
/// на весь прогон.
///
/// Отображение в память снимает обе проблемы: страницы подтягивает ядро
/// операционной системы, процесс не растёт, поиск - двоичный по отсортированным
/// ключам.
///
/// Формат файла:
///   заголовок  - подпись (8 байт), число записей (4 байта)
///   таблица    - на запись 16 байт: смещение данных, длина ключа, длина замены
///   данные     - ключ и сразу за ним замена, запись за записью
/// Записи отсортированы по байтам ключа, поэтому поиск двоичный.
///
/// **Чтение идёт по указателю, а не через MemoryMappedViewAccessor.** Это
/// не оптимизация. Экземплярные методы аккессора документированы как
/// непотокобезопасные, а трансформер обращается к словарю из пула обработчиков.
/// Замерено на прогоне: 92 значения из 2000 не находились в словаре и тихо
/// уходили на путь чистой функции - то есть заменялись не тем, чем должны,
/// и уникальность колонки рассыпалась. Ошибки при этом не возникало ни одной.
/// Указатель берётся один раз и дальше используется только для чтения, поэтому
/// состояния, которое можно испортить одновременным доступом, не остаётся.
/// </summary>
public sealed unsafe class MappedReplacementDictionary : IReplacementDictionary, IDisposable
{
    internal static ReadOnlySpan<byte> Magic => "SANDICT1"u8;

    private const long HeaderSize = 12;
    private const long EntrySize = 16;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly SafeMemoryMappedViewHandle _handle;
    private readonly byte* _data;
    private readonly long _length;
    private readonly int _count;
    private bool _released;

    public MappedReplacementDictionary(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Словарь прогона не найден", path);

        if (info.Length < HeaderSize)
            throw new InvalidDataException($"Словарь короче заголовка: {path}");

        _length = info.Length;

        _file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, 0,
            MemoryMappedFileAccess.Read);

        _view = _file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        var handle = _view.SafeMemoryMappedViewHandle;
        _handle = handle;

        byte* pointer = null;
        handle.AcquirePointer(ref pointer);

        if (pointer is null)
        {
            handle.ReleasePointer();
            throw new InvalidDataException($"Не удалось отобразить словарь в память: {path}");
        }

        // Смещение отображения не обязано быть нулевым: ядро выравнивает
        // начало отображения по границе страницы, и без поправки все смещения
        // уехали бы.
        _data = pointer + _view.PointerOffset;

        var magic = new ReadOnlySpan<byte>(_data, 8);
        if (!magic.SequenceEqual(Magic))
        {
            Release();
            throw new InvalidDataException($"Не словарь прогона или другая версия формата: {path}");
        }

        _count = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(_data + 8, 4));

        if (_count < 0)
        {
            Release();
            throw new InvalidDataException($"Отрицательное число записей: {path}");
        }

        // Смещения считаются в long: на нормативных 200 млн записей таблица
        // длиннее двух гигабайт, и int переполнился бы молча. Здесь же
        // проверяется, что файл вмещает заявленную таблицу целиком - иначе
        // повреждение всплыло бы посреди прогона, после того как часть строк
        // уже ушла получателю.
        var required = HeaderSize + (long)_count * EntrySize;
        if (_length < required)
        {
            Release();
            throw new InvalidDataException(
                $"Словарь короче своей таблицы: записей {_count}, нужно байт {required}, есть {_length}");
        }
    }

    public int Count => _count;

    public bool TryLookup(ReplacementKey key, out string replacement)
    {
        var needle = key.ToBytes();

        var low = 0;
        var high = _count - 1;

        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var comparison = CompareKey(middle, needle);

            if (comparison == 0)
            {
                replacement = ReadReplacement(middle);
                return true;
            }

            if (comparison < 0) low = middle + 1;
            else high = middle - 1;
        }

        replacement = string.Empty;
        return false;
    }

    /// <summary>Запись таблицы: смещение данных, длина ключа, длина замены.</summary>
    private (long DataOffset, int KeyLength, int ValueLength) EntryAt(int entry)
    {
        var offset = HeaderSize + (long)entry * EntrySize;
        var table = new ReadOnlySpan<byte>(_data + offset, (int)EntrySize);

        var dataOffset = BinaryPrimitives.ReadInt64LittleEndian(table[..8]);
        var keyLength = BinaryPrimitives.ReadInt32LittleEndian(table.Slice(8, 4));
        var valueLength = BinaryPrimitives.ReadInt32LittleEndian(table.Slice(12, 4));

        // Проверка границ на каждой записи: испорченный файл иначе увёл бы
        // чтение за пределы отображения, а это уже не ошибка данных,
        // а падение процесса посреди прогона.
        if (dataOffset < 0 || keyLength < 0 || valueLength < 0 ||
            dataOffset + keyLength + valueLength > _length)
        {
            throw new InvalidDataException(
                $"Словарь повреждён: запись {entry} указывает за пределы файла");
        }

        return (dataOffset, keyLength, valueLength);
    }

    private int CompareKey(int entry, ReadOnlySpan<byte> needle)
    {
        var (dataOffset, keyLength, _) = EntryAt(entry);
        var stored = new ReadOnlySpan<byte>(_data + dataOffset, keyLength);

        return stored.SequenceCompareTo(needle);
    }

    private string ReadReplacement(int entry)
    {
        var (dataOffset, keyLength, valueLength) = EntryAt(entry);

        // Замена лежит сразу за ключом в том же блоке данных.
        var value = new ReadOnlySpan<byte>(_data + dataOffset + keyLength, valueLength);

        return System.Text.Encoding.UTF8.GetString(value);
    }

    private void Release()
    {
        if (_released) return;

        _released = true;
        _handle.ReleasePointer();
    }

    public void Dispose()
    {
        Release();

        _view.Dispose();
        _file.Dispose();
    }
}
