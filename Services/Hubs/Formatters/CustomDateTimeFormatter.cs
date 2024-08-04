using MessagePack;
using MessagePack.Formatters;
using System.Globalization;

namespace Services.Hubs.Formatters;

public class CustomDateTimeFormatter : IMessagePackFormatter<DateTime>
{
    private const string DateFormat = "ddd, dd MMM yyyy HH:mm:ss 'GMT'";

    public void Serialize(ref MessagePackWriter writer, DateTime value, MessagePackSerializerOptions options)
    {
        writer.Write(value.ToString(DateFormat, CultureInfo.InvariantCulture));
    }

    public DateTime Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        var dateString = reader.ReadString();
        return DateTime.ParseExact(dateString, DateFormat, CultureInfo.InvariantCulture);
    }
}
