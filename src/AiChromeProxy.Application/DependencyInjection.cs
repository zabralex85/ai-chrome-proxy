using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiChromeProxy.Application;

public static class DependencyInjection
{
	/// <summary>Registers the envelope router and its handlers (singletons). Needs an <see cref="IMirrorStore"/> and an <see cref="IProjectStore"/> (Infrastructure) and logging (the host).</summary>
	public static IServiceCollection AddApplication(this IServiceCollection services)
	{
		services.TryAddSingleton(TimeProvider.System);
		services.AddSingleton<IEnvelopeHandler, PingHandler>();
		services.AddSingleton<SyncSessions>();
		foreach (var type in SyncHandler.Types)
		{
			services.AddSingleton<IEnvelopeHandler>(sp => new SyncHandler(type, sp.GetRequiredService<SyncSessions>()));
		}

		services.AddSingleton<EnvelopeRouter>();
		return services;
	}
}
