using Microsoft.Extensions.DependencyInjection;

namespace Services.Utilities;

public static class ServiceLocator
{
	private static IServiceProvider _serviceProvider;

	public static void Configure(IServiceProvider serviceProvider)
	{
		_serviceProvider = serviceProvider;
	}

	public static T GetService<T>()
	{
		return _serviceProvider.GetService<T>();
	}
}
