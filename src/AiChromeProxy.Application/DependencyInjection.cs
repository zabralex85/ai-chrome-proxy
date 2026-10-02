using AiChromeProxy.Application.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiChromeProxy.Application;

public static class DependencyInjection
{
	/// <summary>Registers the envelope router and its handlers (singletons).</summary>
	public static IServiceCollection AddApplication(this IServiceCollection services)
	{
		services.TryAddSingleton(TimeProvider.System);
		services.AddSingleton<IEnvelopeHandler, PingHandler>();
		services.AddSingleton<EnvelopeRouter>();
		return services;
	}
}
