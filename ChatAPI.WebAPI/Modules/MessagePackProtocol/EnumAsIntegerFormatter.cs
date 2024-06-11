using MessagePack;
using MessagePack.Formatters;

namespace ChatAPI.WebAPI.Modules.MessagePackProtocol
{
	public class EnumAsIntegerFormatter<T> : IMessagePackFormatter<T>
	{
		public static readonly IMessagePackFormatter<T> Instance = typeof(T).IsEnum ? new EnumAsIntegerFormatter<T>() : null;

		public void Serialize(ref MessagePackWriter writer, T value, MessagePackSerializerOptions options)
		{
			writer.Write(Convert.ToInt32(value));
		}

		public T Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
		{
			return (T)Enum.ToObject(typeof(T), reader.ReadInt32());
		}
	}
}
