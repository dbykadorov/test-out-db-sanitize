using Sanitize.Core.Values;

namespace Sanitize.Core.Tests;

/// <summary>
/// Канонизация - ключ отображения по F-7. Ошибка здесь не видна на глаз:
/// замены останутся правдоподобными, просто одно значение получит две разные
/// замены в разных колонках.
/// </summary>
public class CanonicalValueTests
{
    [Fact]
    public void Целое_и_его_строковая_запись_дают_один_ключ()
    {
        // Ровно тот случай, ради которого канонизация и введена: без неё
        // внешний ключ типа integer и его текстовая копия разъехались бы.
        var asInteger = CanonicalValue.From("0042", CanonicalKind.Integer);
        var asText = CanonicalValue.From("42", CanonicalKind.Integer);

        Assert.Equal(asText.Key, asInteger.Key);
        Assert.Equal("42", asInteger.Key);
    }

    [Theory]
    [InlineData("1.500", "1.5")]
    [InlineData("1.5000000", "1.5")]
    [InlineData("0.10", "0.1")]
    [InlineData("2.0", "2")]
    public void Незначащие_нули_дробной_части_не_влияют_на_ключ(string raw, string expected)
    {
        Assert.Equal(expected, CanonicalValue.From(raw, CanonicalKind.Decimal).Key);
    }

    [Fact]
    public void Регистр_почты_приводится_к_нижнему_а_у_прочих_строк_сохраняется()
    {
        Assert.Equal("ivan@example.ru",
            CanonicalValue.From("Ivan@Example.RU", CanonicalKind.EmailAddress).Key);

        Assert.Equal("Иванов",
            CanonicalValue.From("Иванов", CanonicalKind.Text).Key);
        Assert.NotEqual(
            CanonicalValue.From("иванов", CanonicalKind.Text).Key,
            CanonicalValue.From("Иванов", CanonicalKind.Text).Key);
    }

    [Fact]
    public void Концевые_пробелы_отбрасываются_а_ведущие_нет()
    {
        Assert.Equal("Иванов", CanonicalValue.From("Иванов   ", CanonicalKind.Text).Key);

        // Ведущий пробел значим: он часть значения, и его удаление склеило бы
        // два разных значения в одно.
        Assert.Equal(" Иванов", CanonicalValue.From(" Иванов", CanonicalKind.Text).Key);
    }

    [Fact]
    public void Отсутствующее_значение_и_пустая_строка_ключами_не_являются()
    {
        Assert.False(CanonicalValue.From(null, CanonicalKind.Text).IsKey);
        Assert.False(CanonicalValue.From("", CanonicalKind.Text).IsKey);
        Assert.False(CanonicalValue.From("   ", CanonicalKind.Text).IsKey);
        Assert.False(CanonicalValue.From(null, CanonicalKind.Integer).IsKey);
    }

    [Fact]
    public void Метки_времени_приводятся_к_UTC()
    {
        var moscow = CanonicalValue.From("2026-08-18T15:00:00+03:00", CanonicalKind.Timestamp);
        var utc = CanonicalValue.From("2026-08-18T12:00:00Z", CanonicalKind.Timestamp);

        Assert.Equal(utc.Key, moscow.Key);
    }

    [Theory]
    [InlineData("t", "true")]
    [InlineData("TRUE", "true")]
    [InlineData("1", "true")]
    [InlineData("f", "false")]
    [InlineData("0", "false")]
    public void Логические_значения_сводятся_к_двум_записям(string raw, string expected)
    {
        Assert.Equal(expected, CanonicalValue.From(raw, CanonicalKind.Boolean).Key);
    }

    [Fact]
    public void Двоичные_данные_приводятся_к_нижнему_регистру_без_префикса()
    {
        var withPrefix = CanonicalValue.From(@"\xDEADBEEF", CanonicalKind.Binary);
        var without = CanonicalValue.From("deadbeef", CanonicalKind.Binary);

        Assert.Equal(without.Key, withPrefix.Key);
    }

    [Fact]
    public void Значение_неверного_вида_валит_прогон_а_не_молча_проходит()
    {
        // Тихо пропустить нераспознанное значение означало бы оставить ПДн
        // в выгрузке. Лучше остановиться.
        Assert.Throws<FormatException>(() =>
            CanonicalValue.From("не число", CanonicalKind.Integer));
    }
}
