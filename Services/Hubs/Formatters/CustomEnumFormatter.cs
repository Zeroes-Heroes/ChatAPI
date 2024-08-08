using MessagePack;
using MessagePack.Formatters;

namespace Services.Hubs.Formatters;

public class CustomEnumFormatter<T> : IMessagePackFormatter<T> where T : Enum
{
    public void Serialize(ref MessagePackWriter writer, T value, MessagePackSerializerOptions options)
    {
        writer.Write(Convert.ToInt32(value));
    }

    public T Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        var intValue = reader.ReadInt32();
        return (T)Enum.ToObject(typeof(T), intValue);
    }
}
