using System.Reflection;
using AiChromeProxy.Application;
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;
using NetArchTest.Rules;

namespace AiChromeProxy.Tests.Architecture;

public sealed class LayerDependencyTests
{
	private static readonly Assembly DomainAssembly = typeof(Envelope).Assembly;
	private static readonly Assembly ApplicationAssembly = typeof(DependencyInjection).Assembly;
	private static readonly Assembly InfrastructureAssembly = typeof(AiChromeProxy.Infrastructure.DependencyInjection).Assembly;
	private static readonly Assembly ServerAssembly = typeof(Program).Assembly;
	private static readonly Assembly ClientAssembly = typeof(ITransport).Assembly;
	private static readonly Assembly TrayAssembly = typeof(AiChromeProxy.Tray.App).Assembly;

	[Fact]
	public void Domain_IsPure()
	{
		AssertNoDependency(
			DomainAssembly,
			"AiChromeProxy.Application",
			"AiChromeProxy.Infrastructure",
			"AiChromeProxy.Server",
			"AiChromeProxy.Client",
			"Microsoft.AspNetCore");
	}

	[Fact]
	public void Application_IsFrameworkFree()
	{
		AssertNoDependency(
			ApplicationAssembly,
			"AiChromeProxy.Infrastructure",
			"AiChromeProxy.Server",
			"AiChromeProxy.Client",
			"Microsoft.AspNetCore",
			"Microsoft.IdentityModel");
	}

	[Fact]
	public void Infrastructure_DoesNotReachUp()
	{
		AssertNoDependency(InfrastructureAssembly, "AiChromeProxy.Server", "AiChromeProxy.Client", "Microsoft.AspNetCore");
	}

	[Fact]
	public void Client_TalksContractsOnly()
	{
		AssertNoDependency(ClientAssembly, "AiChromeProxy.Application", "AiChromeProxy.Infrastructure", "AiChromeProxy.Server");
	}

	[Fact]
	public void Server_DoesNotUseClientTypes()
	{
		AssertNoDependency(ServerAssembly, "AiChromeProxy.Client");
	}

	[Fact]
	public void Tray_UsesDomainAndInfrastructureOnly()
	{
		AssertNoDependency(TrayAssembly, "AiChromeProxy.Application", "AiChromeProxy.Server", "AiChromeProxy.Client", "Microsoft.AspNetCore");
	}

	/// <summary>Guards the rules above against passing vacuously (e.g. if the assembly reader stopped seeing references).</summary>
	[Fact]
	public void ReferencesAreVisible()
	{
		Assert.NotEmpty(Types.InAssembly(ServerAssembly).That().HaveDependencyOn("AiChromeProxy.Application").GetTypes());
		Assert.NotEmpty(Types.InAssembly(ServerAssembly).That().HaveDependencyOn("AiChromeProxy.Infrastructure").GetTypes());
		Assert.NotEmpty(Types.InAssembly(TrayAssembly).That().HaveDependencyOn("Avalonia").GetTypes());
	}

	private static void AssertNoDependency(Assembly assembly, params string[] forbidden)
	{
		var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();

		Assert.True(result.IsSuccessful, $"{assembly.GetName().Name} must not depend on {string.Join(", ", forbidden)}; violating types: {string.Join(", ", result.FailingTypeNames ?? [])}");
	}
}
