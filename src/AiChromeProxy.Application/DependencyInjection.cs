using AiChromeProxy.Application.Chat;
using AiChromeProxy.Application.Projects;
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiChromeProxy.Application;

public static class DependencyInjection
{
	/// <summary>Registers the envelope router and its handlers (singletons). Needs an <see cref="IMirrorStore"/>, an <see cref="IProjectStore"/>, an <see cref="IChatStore"/> and an <see cref="IAgentRunner"/> (Infrastructure) and logging (the host); an <see cref="IApprovalEndpoint"/> (the Server) is optional.</summary>
	public static IServiceCollection AddApplication(this IServiceCollection services)
	{
		services.TryAddSingleton(TimeProvider.System);
		services.AddSingleton<IEnvelopeHandler, PingHandler>();
		services.AddSingleton<SyncSessions>();
		foreach (var type in SyncHandler.Types)
		{
			services.AddSingleton<IEnvelopeHandler>(sp => new SyncHandler(type, sp.GetRequiredService<SyncSessions>()));
		}

		foreach (var type in ProjectSettingsHandler.Types)
		{
			services.AddSingleton<IEnvelopeHandler>(sp => new ProjectSettingsHandler(type, sp.GetRequiredService<IProjectStore>(), sp.GetRequiredService<SyncSessions>()));
		}

		// The container disposes the service when the host stops, which stops the running agents.
		services.AddSingleton<PermissionBroker>();
		services.AddSingleton<ChatService>();
		foreach (var type in ChatHandler.Types)
		{
			services.AddSingleton<IEnvelopeHandler>(sp => new ChatHandler(type, sp.GetRequiredService<ChatService>()));
		}

		services.AddSingleton<EnvelopeRouter>();
		return services;
	}
}
