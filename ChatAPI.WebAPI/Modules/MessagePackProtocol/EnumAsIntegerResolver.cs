using ChatAPI.WebAPI.Extensions;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;

namespace ChatAPI.WebAPI.Modules.MessagePackProtocol
{
	public class EnumAsIntegerResolver : IFormatterResolver
	{
		public static readonly IFormatterResolver Instance = new EnumAsIntegerResolver();

		private EnumAsIntegerResolver() { }

		public IMessagePackFormatter<T> GetFormatter<T>()
		{
			return EnumAsIntegerFormatter<T>.Instance ?? StandardResolver.Instance.GetFormatter<T>();
		}
	}
}
