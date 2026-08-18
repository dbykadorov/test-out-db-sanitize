using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Sanitize.Core.Replacement;

/// <summary>
/// Псевдослучайная функция замены: индекс = PRF(секрет, ключ отображения).
///
/// Ключ и контекст разделены намеренно (F-7): в вычисление входит ключ
/// отображения - каноническое значение плюс метка исключения Р-9, - и больше
/// ничего. Семантический тип нужен генератору для правдоподобного рендеринга,
/// но на выбор индекса не влияет.
///
/// Функция чистая: зависит только от секрета и ключа, локального состояния нет.
/// Отсюда следует, что параллельные обработчики дают одинаковый результат без
/// всякой синхронизации, и проверка консистентности сводится к тесту свойства,
/// а не к сканированию миллиардов строк.
///
/// Экземпляр потокобезопасен: используется статический <see cref="HMACSHA256.HashData(byte[], byte[])"/>,
/// который состояния не хранит.
/// </summary>
public sealed class PseudoRandomFunction : IDisposable
{
    private byte[]? _secret;

    /// <param name="secret">
    /// Секрет прогона. Хранится в защищённом хранилище внутри контура
    /// и получателю выгрузки не передаётся никогда (Р-1).
    /// </param>
    public PseudoRandomFunction(ReadOnlySpan<byte> secret)
    {
        if (secret.Length < 32)
        {
            throw new ArgumentException(
                "Секрет короче 32 байт: при детерминированной замене он единственное, " +
                "что отделяет получателя выгрузки от восстановления связи.",
                nameof(secret));
        }

        _secret = secret.ToArray();
    }

    /// <summary>
    /// Индекс замены для ключа отображения.
    ///
    /// Порядок байтов задан явно: без этого одинаковый HMAC давал бы разные
    /// индексы на платформах с разным порядком, и выгрузки перестали бы
    /// совпадать между стендами.
    /// </summary>
    public ulong IndexOf(ReplacementKey key)
    {
        var hash = HMACSHA256.HashData(Secret, key.ToBytes());
        return BinaryPrimitives.ReadUInt64BigEndian(hash);
    }

    /// <summary>
    /// Отпечаток значения с секретом прогона.
    ///
    /// Нужен, чтобы перечень исключений Р-9 хранился и в политике, и в паспорте
    /// БЕЗ раскрытия самих значений: иначе список исключений вынес бы реальные
    /// персональные данные в control plane и в документ, уходящий получателю.
    /// </summary>
    public string Fingerprint(string canonicalValue)
    {
        ArgumentNullException.ThrowIfNull(canonicalValue);

        var hash = HMACSHA256.HashData(Secret, Encoding.UTF8.GetBytes(canonicalValue));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private byte[] Secret => _secret
        ?? throw new ObjectDisposedException(nameof(PseudoRandomFunction),
            "Секрет уже стёрт. Освобождать функцию можно только после остановки пула обработчиков.");

    /// <summary>Затирает секрет в памяти. Дальнейшие вызовы падают, а не считают на мусоре.</summary>
    public void Dispose()
    {
        var secret = Interlocked.Exchange(ref _secret, null);
        if (secret is not null) CryptographicOperations.ZeroMemory(secret);
    }
}
