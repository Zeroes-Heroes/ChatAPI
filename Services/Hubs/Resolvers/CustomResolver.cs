using MessagePack;
using MessagePack.Formatters;
using Services.Hubs.Formatters;

namespace Services.Hubs.Resolvers;

public class CustomResolver : IFormatterResolver
{
    public static readonly IFormatterResolver Instance = new CustomResolver();

    public CustomResolver() { }

    public IMessagePackFormatter<T> GetFormatter<T>()
    {
        if (typeof(T) == typeof(DateTime) || typeof(T) == typeof(DateTime?))
        {
            return (IMessagePackFormatter<T>)new CustomDateTimeFormatter();
        }

        if (typeof(T).IsEnum)
        {
            return (IMessagePackFormatter<T>)Activator.CreateInstance(typeof(CustomEnumFormatter<>).MakeGenericType(typeof(T)));
        }

        return null;
    }
}
