# Minimal Clean Architecture + Test Layers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split the server side into Domain / Application / Infrastructure / Server (host) with enforced dependency direction, and add E2E BDD (Reqnroll + Playwright), k6 load and BenchmarkDotNet layers next to the xunit suite — no product behavior change.

**Architecture:** `Shared` becomes `Domain` (wire contract, BCL only); handlers/router move to `Application`; Cloudflare Access options/validator move to `Infrastructure`; `Server` becomes the composition root (`AddApplication()`, `AddInfrastructure(configuration)`). NetArchTest rules in the xunit suite enforce the direction. E2E starts the real server in-process on Kestrel and drives headless Chromium; k6 speaks the SignalR JSON protocol over WebSocket; BenchmarkDotNet measures envelope, router and token-validation hot paths. Only xunit runs in CI.

**Tech Stack:** .NET 10, xunit.v3 4.0.1 (MTP), NetArchTest.Rules 1.3.2, Reqnroll.xunit.v3 3.3.4, Microsoft.Playwright 1.63.0, Microsoft.AspNetCore.Mvc.Testing 10.0.12, k6 (JS), BenchmarkDotNet 0.15.8.

**Spec:** [docs/superpowers/specs/2026-10-02-clean-arch-and-test-layers-design.md](../specs/2026-10-02-clean-arch-and-test-layers-design.md)

## Global Constraints

- Target framework `net10.0`; package versions exactly as in this plan (latest stable at writing time).
- Dependency direction: Domain (BCL only) ← Application ← Infrastructure ← Server; Client → Domain only.
- No product behavior change; the `Envelope` JSON shape (camelCase) is unchanged.
- All files UTF-8 **without BOM**. Search with `rg`, never `find`.
- C# style: tabs, no `this.` prefix, private fields `_camelCase`, sorted usings; StyleCop errors fail the build (applies to every project incl. E2E and Benchmarks) — fix code, never relax `StyleCop.ruleset`.
- Everything in English. No Claude/AI attribution in commits.
- CI (`.github/workflows/ci.yml`) stays unchanged: it builds the whole solution and runs only `tests/AiChromeProxy.Tests` with the 85% line-coverage gate.
- E2E and load tests run against Development with `CloudflareAccess:Enabled=false`; never weaken production auth for tests.
- Gate command used throughout (after `dotnet build -c Release`): `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total`.

## File Structure

```
src/AiChromeProxy.Domain/            (renamed from Shared) Envelope, MessageTypes
src/AiChromeProxy.Application/       DependencyInjection.AddApplication(); Transport/{IEnvelopeHandler, EnvelopeRouter, PingHandler}
src/AiChromeProxy.Infrastructure/    DependencyInjection.AddInfrastructure(); Security/{CloudflareAccessOptions, CloudflareAccessTokenValidator}
src/AiChromeProxy.Server/            Program (composition root), Transport/TransportHub, Security/CloudflareAccessMiddleware
src/AiChromeProxy.Client/            references Domain only; Home.razor gains data-testid attributes
tests/AiChromeProxy.Tests/           Domain/ Application/ Infrastructure/ Server/ Architecture/ + FixedTimeProvider.cs
tests/AiChromeProxy.E2E/             Reqnroll + Playwright: Features/, Hooks/, StepDefinitions/
tests/load/                          lib/signalr.js, smoke.js, load.js (k6)
benchmarks/AiChromeProxy.Benchmarks/ Program + 3 benchmark classes
docs/testing.md                      how to run every layer
```

---

### Task 1: Clean-architecture restructure + architecture tests

**Files:**
- Move (git mv): `src/AiChromeProxy.Shared` → `src/AiChromeProxy.Domain`; Transport classes → `src/AiChromeProxy.Application/Transport/`; Security options/validator → `src/AiChromeProxy.Infrastructure/Security/`; tests into `Domain/`, `Application/`, `Infrastructure/`, `Server/` folders
- Create: `src/AiChromeProxy.Application/{AiChromeProxy.Application.csproj, DependencyInjection.cs}`, `src/AiChromeProxy.Infrastructure/{AiChromeProxy.Infrastructure.csproj, DependencyInjection.cs}`, `tests/AiChromeProxy.Tests/Architecture/LayerDependencyTests.cs`
- Modify: `AiChromeProxy.slnx`, Server/Client/Tests csproj files, `Program.cs`, `TransportHub.cs`, `CloudflareAccessMiddleware.cs`, Client `ITransport.cs`, `SignalRTransport.cs`, `Home.razor` (usings/namespaces only)

**Interfaces:**
- Produces: namespaces `AiChromeProxy.Domain` (`Envelope`, `MessageTypes`), `AiChromeProxy.Application` (`DependencyInjection.AddApplication(this IServiceCollection)`), `AiChromeProxy.Application.Transport` (`IEnvelopeHandler`, `EnvelopeRouter`, `PingHandler`), `AiChromeProxy.Infrastructure` (`DependencyInjection.AddInfrastructure(this IServiceCollection, IConfiguration)`), `AiChromeProxy.Infrastructure.Security` (`CloudflareAccessOptions`, `CloudflareAccessTokenValidator`). Public `Program` unchanged. Tests namespaces: `AiChromeProxy.Tests.{Domain,Application,Infrastructure,Server,Architecture}`.

- [ ] **Step 1: Move files with history**

From the repo root (Git Bash):

```bash
git mv src/AiChromeProxy.Shared src/AiChromeProxy.Domain
mkdir -p src/AiChromeProxy.Application/Transport src/AiChromeProxy.Infrastructure/Security tests/AiChromeProxy.Tests/Domain tests/AiChromeProxy.Tests/Application tests/AiChromeProxy.Tests/Infrastructure tests/AiChromeProxy.Tests/Server tests/AiChromeProxy.Tests/Architecture
git mv src/AiChromeProxy.Domain/AiChromeProxy.Shared.csproj src/AiChromeProxy.Domain/AiChromeProxy.Domain.csproj
git mv src/AiChromeProxy.Server/Transport/EnvelopeRouter.cs src/AiChromeProxy.Application/Transport/EnvelopeRouter.cs
git mv src/AiChromeProxy.Server/Transport/IEnvelopeHandler.cs src/AiChromeProxy.Application/Transport/IEnvelopeHandler.cs
git mv src/AiChromeProxy.Server/Transport/PingHandler.cs src/AiChromeProxy.Application/Transport/PingHandler.cs
git mv src/AiChromeProxy.Server/Security/CloudflareAccessOptions.cs src/AiChromeProxy.Infrastructure/Security/CloudflareAccessOptions.cs
git mv src/AiChromeProxy.Server/Security/CloudflareAccessTokenValidator.cs src/AiChromeProxy.Infrastructure/Security/CloudflareAccessTokenValidator.cs
git mv tests/AiChromeProxy.Tests/EnvelopeTests.cs tests/AiChromeProxy.Tests/Domain/EnvelopeTests.cs
git mv tests/AiChromeProxy.Tests/Transport/EnvelopeRouterTests.cs tests/AiChromeProxy.Tests/Application/EnvelopeRouterTests.cs
git mv tests/AiChromeProxy.Tests/Transport/PingHandlerTests.cs tests/AiChromeProxy.Tests/Application/PingHandlerTests.cs
git mv tests/AiChromeProxy.Tests/Security/CloudflareAccessOptionsTests.cs tests/AiChromeProxy.Tests/Infrastructure/CloudflareAccessOptionsTests.cs
git mv tests/AiChromeProxy.Tests/Security/TestAccessIssuer.cs tests/AiChromeProxy.Tests/Infrastructure/TestAccessIssuer.cs
git mv tests/AiChromeProxy.Tests/Security/CloudflareAccessMiddlewareTests.cs tests/AiChromeProxy.Tests/Server/CloudflareAccessMiddlewareTests.cs
git mv tests/AiChromeProxy.Tests/Transport/TransportHubTests.cs tests/AiChromeProxy.Tests/Server/TransportHubTests.cs
```

- [ ] **Step 2: Write final source contents**

Every file below gets exactly this content (moved files only change namespaces/usings; new files are new).

Write the full content of `src/AiChromeProxy.Domain/Envelope.cs`:

```csharp
using System.Text.Json;

namespace AiChromeProxy.Domain;

/// <summary>Single wire message for every feature; routed by <see cref="Type"/>.</summary>
public sealed record Envelope(string Type, JsonElement Payload, string? CorrelationId = null)
{
	public static Envelope Create<T>(string type, T payload, string? correlationId = null) =>
		new(type, JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web), correlationId);
}
```

Write the full content of `src/AiChromeProxy.Domain/MessageTypes.cs`:

```csharp
namespace AiChromeProxy.Domain;

public static class MessageTypes
{
	public const string Ping = "ping";
	public const string Pong = "pong";
	public const string Error = "error";
}
```

Write the full content of `src/AiChromeProxy.Application/AiChromeProxy.Application.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.12" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\AiChromeProxy.Domain\AiChromeProxy.Domain.csproj" />
  </ItemGroup>

</Project>
```

Write the full content of `src/AiChromeProxy.Application/DependencyInjection.cs`:

```csharp
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
```

Write the full content of `src/AiChromeProxy.Application/Transport/IEnvelopeHandler.cs`:

```csharp
using AiChromeProxy.Domain;

namespace AiChromeProxy.Application.Transport;

public interface IEnvelopeHandler
{
	string Type { get; }

	Task<Envelope?> HandleAsync(Envelope request, CancellationToken ct);
}
```

Write the full content of `src/AiChromeProxy.Application/Transport/EnvelopeRouter.cs`:

```csharp
using AiChromeProxy.Domain;

namespace AiChromeProxy.Application.Transport;

public sealed class EnvelopeRouter
{
	private readonly Dictionary<string, IEnvelopeHandler> _handlers = new(StringComparer.Ordinal);

	public EnvelopeRouter(IEnumerable<IEnvelopeHandler> handlers)
	{
		foreach (var handler in handlers)
		{
			if (!_handlers.TryAdd(handler.Type, handler))
			{
				throw new InvalidOperationException($"Duplicate envelope handler for type '{handler.Type}'.");
			}
		}
	}

	public Task<Envelope?> RouteAsync(Envelope request, CancellationToken ct)
	{
		if (_handlers.TryGetValue(request.Type, out var handler))
		{
			return handler.HandleAsync(request, ct);
		}

		var error = Envelope.Create(MessageTypes.Error, new { code = "unknown_type", type = request.Type }, request.CorrelationId);
		return Task.FromResult<Envelope?>(error);
	}
}
```

Write the full content of `src/AiChromeProxy.Application/Transport/PingHandler.cs`:

```csharp
using AiChromeProxy.Domain;

namespace AiChromeProxy.Application.Transport;

public sealed class PingHandler(TimeProvider time) : IEnvelopeHandler
{
	public string Type => MessageTypes.Ping;

	public Task<Envelope?> HandleAsync(Envelope request, CancellationToken ct)
	{
		var pong = Envelope.Create(MessageTypes.Pong, new { serverTime = time.GetUtcNow().ToString("O") }, request.CorrelationId);
		return Task.FromResult<Envelope?>(pong);
	}
}
```

Write the full content of `src/AiChromeProxy.Infrastructure/AiChromeProxy.Infrastructure.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <!-- Hosting, Options, Configuration and HttpClientFactory abstractions, versioned with the runtime. -->
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.IdentityModel.JsonWebTokens" Version="8.23.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\AiChromeProxy.Application\AiChromeProxy.Application.csproj" />
    <ProjectReference Include="..\AiChromeProxy.Domain\AiChromeProxy.Domain.csproj" />
  </ItemGroup>

</Project>
```

Write the full content of `src/AiChromeProxy.Infrastructure/DependencyInjection.cs`:

```csharp
using AiChromeProxy.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AiChromeProxy.Infrastructure;

public static class DependencyInjection
{
	/// <summary>Registers the Cloudflare Access options (section <c>CloudflareAccess</c>), the JWKS HttpClient and the token validator.</summary>
	public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
	{
		services.Configure<CloudflareAccessOptions>(configuration.GetSection(CloudflareAccessOptions.Section));
		services.AddHttpClient(CloudflareAccessTokenValidator.JwksHttpClient);
		services.AddSingleton<CloudflareAccessTokenValidator>();
		return services;
	}
}
```

Write the full content of `src/AiChromeProxy.Infrastructure/Security/CloudflareAccessOptions.cs`:

```csharp
using Microsoft.Extensions.Hosting;

namespace AiChromeProxy.Infrastructure.Security;

public sealed class CloudflareAccessOptions
{
	public const string Section = "CloudflareAccess";

	public bool Enabled { get; set; } = true;

	public string TeamDomain { get; set; } = string.Empty;

	public string Audience { get; set; } = string.Empty;

	/// <summary>Fail closed: outside Development the check must be on and fully configured.</summary>
	public void Validate(IHostEnvironment env)
	{
		if (!Enabled)
		{
			if (!env.IsDevelopment())
			{
				throw new InvalidOperationException("CloudflareAccess:Enabled=false is allowed only in Development.");
			}

			return;
		}

		if (string.IsNullOrWhiteSpace(TeamDomain) || string.IsNullOrWhiteSpace(Audience))
		{
			throw new InvalidOperationException("CloudflareAccess:TeamDomain and CloudflareAccess:Audience must be set.");
		}
	}
}
```

Write the full content of `src/AiChromeProxy.Infrastructure/Security/CloudflareAccessTokenValidator.cs`:

```csharp
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AiChromeProxy.Infrastructure.Security;

/// <summary>Validates Cloudflare Access JWTs against the team JWKS.</summary>
public sealed class CloudflareAccessTokenValidator(
	IHttpClientFactory httpFactory,
	IOptions<CloudflareAccessOptions> options,
	TimeProvider time)
{
	public const string JwksHttpClient = "cf-access-jwks";

	private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(1);
	private static readonly TimeSpan JwksTimeout = TimeSpan.FromSeconds(10);

	private readonly JsonWebTokenHandler _handler = new();
	private readonly SemaphoreSlim _refreshLock = new(1, 1);
	private IList<SecurityKey> _keys = [];
	private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

	public async Task<bool> ValidateAsync(string token, CancellationToken ct)
	{
		if (_keys.Count == 0)
		{
			await RefreshKeysAsync(ct);
		}

		var result = await _handler.ValidateTokenAsync(token, Parameters());
		if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException && await RefreshKeysAsync(ct))
		{
			result = await _handler.ValidateTokenAsync(token, Parameters());
		}

		return result.IsValid;
	}

	private TokenValidationParameters Parameters()
	{
		var o = options.Value;
		return new TokenValidationParameters
		{
			ValidIssuer = $"https://{o.TeamDomain}",
			ValidAudience = o.Audience,
			IssuerSigningKeys = _keys,
			ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
			ClockSkew = TimeSpan.FromMinutes(1),
			LifetimeValidator = (notBefore, expires, _, p) =>
			{
				var now = time.GetUtcNow().UtcDateTime;
				return (notBefore is null || notBefore.Value - p.ClockSkew <= now)
					&& expires is not null && now <= expires.Value + p.ClockSkew;
			},
		};
	}

	/// <returns>True when keys were (re)loaded.</returns>
	private async Task<bool> RefreshKeysAsync(CancellationToken ct)
	{
		await _refreshLock.WaitAsync(ct);
		try
		{
			var now = time.GetUtcNow();
			if (now - _lastRefresh < MinRefreshInterval)
			{
				return false;
			}

			_lastRefresh = now;
			var url = $"https://{options.Value.TeamDomain}/cdn-cgi/access/certs";
			var client = httpFactory.CreateClient(JwksHttpClient);
			client.Timeout = JwksTimeout;
			// Not the caller's token: _lastRefresh is already set, so a cancelled fetch would leave keys empty for a minute.
			var json = await client.GetStringAsync(url, CancellationToken.None);
			_keys = new JsonWebKeySet(json).GetSigningKeys();
			return true;
		}
		finally
		{
			_refreshLock.Release();
		}
	}
}
```

Write the full content of `src/AiChromeProxy.Server/AiChromeProxy.Server.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <ItemGroup>
    <ProjectReference Include="..\AiChromeProxy.Application\AiChromeProxy.Application.csproj" />
    <ProjectReference Include="..\AiChromeProxy.Client\AiChromeProxy.Client.csproj" />
    <ProjectReference Include="..\AiChromeProxy.Domain\AiChromeProxy.Domain.csproj" />
    <ProjectReference Include="..\AiChromeProxy.Infrastructure\AiChromeProxy.Infrastructure.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly.Server" Version="10.0.12" />
  </ItemGroup>

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

</Project>
```

Write the full content of `src/AiChromeProxy.Server/Program.cs`:

```csharp
using System.Net;
using AiChromeProxy.Application;
using AiChromeProxy.Infrastructure;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Server.Security;
using AiChromeProxy.Server.Transport;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

var port = builder.Configuration.GetValue("Server:Port", 5180);
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSignalR();

var app = builder.Build();

app.Services.GetRequiredService<IOptions<CloudflareAccessOptions>>().Value.Validate(app.Environment);

if (!app.Services.GetRequiredService<IOptions<CloudflareAccessOptions>>().Value.Enabled)
{
	app.Logger.LogWarning("Cloudflare Access check is DISABLED (Development only). Do not expose this server.");
}

app.UseMiddleware<CloudflareAccessMiddleware>();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.MapHub<TransportHub>(TransportHub.Path);
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Entry point; partial so WebApplicationFactory can reference it.</summary>
public partial class Program
{
}
```

Write the full content of `src/AiChromeProxy.Server/Security/CloudflareAccessMiddleware.cs`:

```csharp
using AiChromeProxy.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Server.Security;

/// <summary>Rejects every request (static files, hub, WebSocket upgrade) without a valid Access JWT.</summary>
public sealed class CloudflareAccessMiddleware(
	RequestDelegate next,
	IOptions<CloudflareAccessOptions> options,
	CloudflareAccessTokenValidator validator)
{
	public const string HeaderName = "Cf-Access-Jwt-Assertion";
	public const string CookieName = "CF_Authorization";

	public async Task InvokeAsync(HttpContext context)
	{
		if (!options.Value.Enabled)
		{
			await next(context);
			return;
		}

		string? token = context.Request.Headers[HeaderName];
		if (string.IsNullOrEmpty(token))
		{
			token = context.Request.Cookies[CookieName];
		}

		if (string.IsNullOrEmpty(token) || !await validator.ValidateAsync(token, context.RequestAborted))
		{
			context.Response.StatusCode = StatusCodes.Status401Unauthorized;
			return;
		}

		await next(context);
	}
}
```

Write the full content of `src/AiChromeProxy.Server/Transport/TransportHub.cs`:

```csharp
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using Microsoft.AspNetCore.SignalR;

namespace AiChromeProxy.Server.Transport;

public sealed class TransportHub(EnvelopeRouter router) : Hub
{
	public const string Path = "/hub";
	public const string ReceiveMethod = "Receive";

	public async Task Send(Envelope envelope)
	{
		var reply = await router.RouteAsync(envelope, Context.ConnectionAborted);
		if (reply is not null)
		{
			await Clients.Caller.SendAsync(ReceiveMethod, reply, Context.ConnectionAborted);
		}
	}
}
```

Write the full content of `src/AiChromeProxy.Client/AiChromeProxy.Client.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <OverrideHtmlAssetPlaceholders>true</OverrideHtmlAssetPlaceholders>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly" Version="10.0.12" />
    <PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly.DevServer" Version="10.0.12" PrivateAssets="all" />
    <PackageReference Include="Microsoft.AspNetCore.SignalR.Client" Version="10.0.12" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\AiChromeProxy.Domain\AiChromeProxy.Domain.csproj" />
  </ItemGroup>

</Project>
```

Write the full content of `src/AiChromeProxy.Client/Transport/ITransport.cs`:

```csharp
using AiChromeProxy.Domain;

namespace AiChromeProxy.Client.Transport;

public enum TransportState
{
	Disconnected,
	Connecting,
	Connected,
	Reconnecting,
}

/// <summary>Client side of the single envelope channel; SignalR today, WebRTC later.</summary>
public interface ITransport : IAsyncDisposable
{
	event Action<TransportState>? StateChanged;

	event Action<Envelope>? Received;

	TransportState State { get; }

	Task ConnectAsync(CancellationToken ct = default);

	Task SendAsync(Envelope envelope, CancellationToken ct = default);
}
```

Write the full content of `src/AiChromeProxy.Client/Transport/SignalRTransport.cs`:

```csharp
using AiChromeProxy.Domain;
using Microsoft.AspNetCore.SignalR.Client;

namespace AiChromeProxy.Client.Transport;

/// <summary><see cref="ITransport"/> over a SignalR hub exposing Send(Envelope) / Receive(Envelope).</summary>
public sealed class SignalRTransport : ITransport
{
	public const string SendMethod = "Send";
	public const string ReceiveMethod = "Receive";

	private readonly HubConnection _connection;

	/// <param name="connection">Built by the caller (URL, auto-reconnect, test handlers).</param>
	public SignalRTransport(HubConnection connection)
	{
		_connection = connection;
		_connection.On<Envelope>(ReceiveMethod, e => Received?.Invoke(e));
		_connection.Reconnecting += _ => SetState(TransportState.Reconnecting);
		_connection.Reconnected += _ => SetState(TransportState.Connected);
		_connection.Closed += _ => SetState(TransportState.Disconnected);
	}

	public event Action<TransportState>? StateChanged;

	public event Action<Envelope>? Received;

	public TransportState State { get; private set; } = TransportState.Disconnected;

	public async Task ConnectAsync(CancellationToken ct = default)
	{
		await SetState(TransportState.Connecting);
		try
		{
			await _connection.StartAsync(ct);
		}
		catch
		{
			await SetState(TransportState.Disconnected);
			throw;
		}

		await SetState(TransportState.Connected);
	}

	public Task SendAsync(Envelope envelope, CancellationToken ct = default) =>
		_connection.SendAsync(SendMethod, envelope, ct);

	public ValueTask DisposeAsync() => _connection.DisposeAsync();

	private Task SetState(TransportState state)
	{
		State = state;
		StateChanged?.Invoke(state);
		return Task.CompletedTask;
	}
}
```

Write the full content of `src/AiChromeProxy.Client/Pages/Home.razor`:

```razor
@page "/"
@using System.Diagnostics
@using AiChromeProxy.Client.Transport
@using AiChromeProxy.Domain
@inject ITransport Transport
@implements IDisposable

<PageTitle>ai-chrome-proxy</PageTitle>

<h1>ai-chrome-proxy</h1>

<p>Connection: <strong>@Transport.State</strong></p>

<button @onclick="PingAsync" disabled="@(Transport.State != TransportState.Connected)">Ping</button>

@if (lastResult is not null)
{
	<p>@lastResult</p>
}

@code {
	private readonly Dictionary<string, Stopwatch> pending = new();
	private string? lastResult;

	protected override async Task OnInitializedAsync()
	{
		Transport.StateChanged += OnStateChanged;
		Transport.Received += OnReceived;
		if (Transport.State == TransportState.Disconnected)
		{
			try
			{
				await Transport.ConnectAsync();
			}
			catch (Exception ex)
			{
				lastResult = $"Connect failed: {ex.Message}";
			}
		}
	}

	public void Dispose()
	{
		Transport.StateChanged -= OnStateChanged;
		Transport.Received -= OnReceived;
	}

	private async Task PingAsync()
	{
		var id = Guid.NewGuid().ToString("N");
		pending[id] = Stopwatch.StartNew();
		await Transport.SendAsync(Envelope.Create(MessageTypes.Ping, new { }, id));
	}

	private void OnStateChanged(TransportState state) => InvokeAsync(StateHasChanged);

	private void OnReceived(Envelope envelope)
	{
		if (envelope.Type != MessageTypes.Pong || envelope.CorrelationId is null || !pending.Remove(envelope.CorrelationId, out var sw))
		{
			return;
		}

		var serverTime = envelope.Payload.GetProperty("serverTime").GetString();
		lastResult = $"Pong in {sw.ElapsedMilliseconds} ms, server time {serverTime}";
		InvokeAsync(StateHasChanged);
	}
}
```

Write the full content of `AiChromeProxy.slnx`:

```xml
<Solution>
  <Folder Name="/src/">
    <Project Path="src/AiChromeProxy.Application/AiChromeProxy.Application.csproj" />
    <Project Path="src/AiChromeProxy.Client/AiChromeProxy.Client.csproj" />
    <Project Path="src/AiChromeProxy.Domain/AiChromeProxy.Domain.csproj" />
    <Project Path="src/AiChromeProxy.Infrastructure/AiChromeProxy.Infrastructure.csproj" />
    <Project Path="src/AiChromeProxy.Server/AiChromeProxy.Server.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/AiChromeProxy.Tests/AiChromeProxy.Tests.csproj" />
  </Folder>
</Solution>
```

- [ ] **Step 3: Write final test project contents**

Write the full content of `tests/AiChromeProxy.Tests/AiChromeProxy.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.MTP" Version="10.1.0" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.12" />
    <PackageReference Include="Microsoft.AspNetCore.SignalR.Client" Version="10.0.12" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.10.1" />
    <PackageReference Include="NetArchTest.Rules" Version="1.3.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="4.0.0">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="xunit.v3" Version="4.0.1" />
  </ItemGroup>

  <ItemGroup>
    <None Update="testconfig.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\AiChromeProxy.Application\AiChromeProxy.Application.csproj" />
    <ProjectReference Include="..\..\src\AiChromeProxy.Client\AiChromeProxy.Client.csproj" />
    <ProjectReference Include="..\..\src\AiChromeProxy.Domain\AiChromeProxy.Domain.csproj" />
    <ProjectReference Include="..\..\src\AiChromeProxy.Infrastructure\AiChromeProxy.Infrastructure.csproj" />
    <ProjectReference Include="..\..\src\AiChromeProxy.Server\AiChromeProxy.Server.csproj" />
  </ItemGroup>

</Project>
```

Write the full content of `tests/AiChromeProxy.Tests/Domain/EnvelopeTests.cs`:

```csharp
using AiChromeProxy.Domain;

namespace AiChromeProxy.Tests.Domain;

public sealed class EnvelopeTests
{
	[Fact]
	public void Create_SerializesPayloadAndKeepsCorrelationId()
	{
		var envelope = Envelope.Create("t", new { a = 1 }, "c");

		Assert.Equal("t", envelope.Type);
		Assert.Equal(1, envelope.Payload.GetProperty("a").GetInt32());
		Assert.Equal("c", envelope.CorrelationId);
	}

	[Fact]
	public void Create_WithoutCorrelationId_LeavesItNull()
	{
		Assert.Null(Envelope.Create("t", new { }).CorrelationId);
	}

	[Fact]
	public void Create_PayloadWireShapeIsCamelCase()
	{
		var envelope = Envelope.Create("t", new WireDto("a.cs", 3));

		Assert.Equal("{\"filePath\":\"a.cs\",\"lineCount\":3}", envelope.Payload.GetRawText());
	}

	private sealed record WireDto(string FilePath, int LineCount);
}
```

Write the full content of `tests/AiChromeProxy.Tests/Application/EnvelopeRouterTests.cs`:

```csharp
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;

namespace AiChromeProxy.Tests.Application;

public sealed class EnvelopeRouterTests
{
	[Fact]
	public async Task KnownType_CallsHandler_ReturnsReply()
	{
		var router = new EnvelopeRouter([new EchoHandler("echo")]);

		var reply = await router.RouteAsync(Envelope.Create("echo", new { v = 1 }, "c1"), TestContext.Current.CancellationToken);

		Assert.NotNull(reply);
		Assert.Equal("echo-reply", reply.Type);
		Assert.Equal("c1", reply.CorrelationId);
	}

	[Fact]
	public async Task UnknownType_ReturnsErrorWithSameCorrelationId()
	{
		var router = new EnvelopeRouter([]);

		var reply = await router.RouteAsync(Envelope.Create("nope", new { }, "c2"), TestContext.Current.CancellationToken);

		Assert.NotNull(reply);
		Assert.Equal(MessageTypes.Error, reply.Type);
		Assert.Equal("c2", reply.CorrelationId);
		Assert.Equal("unknown_type", reply.Payload.GetProperty("code").GetString());
		Assert.Equal("nope", reply.Payload.GetProperty("type").GetString());
	}

	[Fact]
	public void DuplicateHandlerType_Throws()
	{
		var ex = Assert.Throws<InvalidOperationException>(() => new EnvelopeRouter([new EchoHandler("x"), new EchoHandler("x")]));

		Assert.Contains("'x'", ex.Message);
	}

	private sealed class EchoHandler(string type) : IEnvelopeHandler
	{
		public string Type => type;

		public Task<Envelope?> HandleAsync(Envelope request, CancellationToken ct) =>
			Task.FromResult<Envelope?>(Envelope.Create(type + "-reply", new { }, request.CorrelationId));
	}
}
```

Write the full content of `tests/AiChromeProxy.Tests/Application/PingHandlerTests.cs`:

```csharp
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;

namespace AiChromeProxy.Tests.Application;

public sealed class PingHandlerTests
{
	[Fact]
	public async Task Ping_ReturnsPongWithUtcServerTime()
	{
		var now = new DateTimeOffset(2026, 10, 2, 12, 30, 0, TimeSpan.Zero);
		var handler = new PingHandler(new FixedTimeProvider(now));

		var reply = await handler.HandleAsync(Envelope.Create(MessageTypes.Ping, new { }, "p1"), TestContext.Current.CancellationToken);

		Assert.NotNull(reply);
		Assert.Equal(MessageTypes.Pong, reply.Type);
		Assert.Equal("p1", reply.CorrelationId);
		var serverTime = DateTimeOffset.Parse(reply.Payload.GetProperty("serverTime").GetString()!);
		Assert.Equal(now, serverTime);
		Assert.Equal(TimeSpan.Zero, serverTime.Offset);
	}
}
```

Write the full content of `tests/AiChromeProxy.Tests/Infrastructure/CloudflareAccessOptionsTests.cs`:

```csharp
using AiChromeProxy.Infrastructure.Security;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AiChromeProxy.Tests.Infrastructure;

public sealed class CloudflareAccessOptionsTests
{
	[Theory]
	[InlineData("", "aud")]
	[InlineData("team.cloudflareaccess.com", "")]
	[InlineData(" ", " ")]
	public void Production_MissingConfig_Throws(string team, string aud)
	{
		var options = new CloudflareAccessOptions { TeamDomain = team, Audience = aud };

		var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(new Env(Environments.Production)));

		Assert.Contains("TeamDomain", ex.Message);
	}

	[Fact]
	public void Production_Disabled_Throws()
	{
		var options = new CloudflareAccessOptions { Enabled = false };

		var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(new Env(Environments.Production)));

		Assert.Contains("Development", ex.Message);
	}

	[Fact]
	public void Development_Disabled_Ok()
	{
		new CloudflareAccessOptions { Enabled = false }.Validate(new Env(Environments.Development));
	}

	[Fact]
	public void Production_FullConfig_Ok()
	{
		new CloudflareAccessOptions { TeamDomain = "t.cloudflareaccess.com", Audience = "a" }.Validate(new Env(Environments.Production));
	}

	private sealed class Env(string name) : IHostEnvironment
	{
		public string EnvironmentName { get; set; } = name;

		public string ApplicationName { get; set; } = "test";

		public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

		public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
	}
}
```

Write the full content of `tests/AiChromeProxy.Tests/Infrastructure/TestAccessIssuer.cs`:

```csharp
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AiChromeProxy.Tests.Infrastructure;

/// <summary>Plays the Cloudflare Access side in tests: RSA key, JWKS document, signed tokens.</summary>
public sealed class TestAccessIssuer
{
	public const string TeamDomain = "test-team.cloudflareaccess.com";
	public const string Audience = "test-aud";

	private readonly RsaSecurityKey _key;

	public TestAccessIssuer(string keyId = "kid-1")
	{
		_key = new RsaSecurityKey(RSA.Create(2048)) { KeyId = keyId };
	}

	public string Jwks()
	{
		var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(_key);
		return JsonSerializer.Serialize(new { keys = new[] { new { kty = jwk.Kty, kid = jwk.Kid, use = "sig", alg = "RS256", n = jwk.N, e = jwk.E } } });
	}

	public string Token(
		string issuer = "https://" + TeamDomain,
		string audience = Audience,
		DateTime? expires = null,
		DateTime? notBefore = null,
		bool omitExpiry = false)
	{
		var exp = expires ?? DateTime.UtcNow.AddMinutes(10);
		return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
		{
			Issuer = issuer,
			Audience = audience,
			NotBefore = notBefore ?? exp.AddMinutes(-20),
			IssuedAt = notBefore ?? exp.AddMinutes(-20),
			Expires = omitExpiry ? null : exp,
			Claims = new Dictionary<string, object> { ["email"] = "user@example.com" },
			SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
		});
	}

	/// <summary>Token signed with a symmetric key (HS256) but carrying the right iss/aud/kid.</summary>
	public string HmacToken(string keyId = "kid-1") =>
		new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
		{
			Issuer = "https://" + TeamDomain,
			Audience = Audience,
			Expires = DateTime.UtcNow.AddMinutes(10),
			SigningCredentials = new SigningCredentials(
				new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = keyId },
				SecurityAlgorithms.HmacSha256),
		});

	/// <summary>Unsigned token: <c>alg=none</c> header, valid-looking claims, empty signature.</summary>
	public string UnsignedToken(string keyId = "kid-1")
	{
		var header = Base64UrlEncoder.Encode($"{{\"alg\":\"none\",\"typ\":\"JWT\",\"kid\":\"{keyId}\"}}");
		var exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
		var payload = Base64UrlEncoder.Encode($"{{\"iss\":\"https://{TeamDomain}\",\"aud\":\"{Audience}\",\"exp\":{exp}}}");
		return $"{header}.{payload}.";
	}

	/// <summary>HttpMessageHandler serving this issuer's JWKS; counts requests.</summary>
	public JwksHandler Handler() => new(this);

	public sealed class JwksHandler(TestAccessIssuer issuer) : HttpMessageHandler
	{
		public int Requests { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests++;
			Assert.Equal($"https://{TeamDomain}/cdn-cgi/access/certs", request.RequestUri!.ToString());
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(issuer.Jwks()) });
		}
	}
}
```

Write the full content of `tests/AiChromeProxy.Tests/Server/CloudflareAccessMiddlewareTests.cs`:

```csharp
using System.Net;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Server.Security;
using AiChromeProxy.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Tests.Server;

public sealed class CloudflareAccessMiddlewareTests
{
	private readonly TestAccessIssuer _issuer = new();

	[Fact]
	public async Task ValidHeaderToken_Passes()
	{
		var status = await RunAsync(ctx => ctx.Request.Headers[CloudflareAccessMiddleware.HeaderName] = _issuer.Token());

		Assert.Equal(StatusCodes.Status204NoContent, status);
	}

	[Fact]
	public async Task ValidCookieToken_Passes()
	{
		var status = await RunAsync(ctx => ctx.Request.Headers.Cookie = $"{CloudflareAccessMiddleware.CookieName}={_issuer.Token()}");

		Assert.Equal(StatusCodes.Status204NoContent, status);
	}

	[Fact]
	public async Task MissingToken_401()
	{
		Assert.Equal(StatusCodes.Status401Unauthorized, await RunAsync(_ => { }));
	}

	[Fact]
	public async Task WrongAudience_401()
	{
		var status = await RunAsync(ctx => ctx.Request.Headers[CloudflareAccessMiddleware.HeaderName] = _issuer.Token(audience: "other"));

		Assert.Equal(StatusCodes.Status401Unauthorized, status);
	}

	[Fact]
	public async Task WrongIssuer_401()
	{
		var status = await RunAsync(ctx => ctx.Request.Headers[CloudflareAccessMiddleware.HeaderName] = _issuer.Token(issuer: "https://evil.cloudflareaccess.com"));

		Assert.Equal(StatusCodes.Status401Unauthorized, status);
	}

	[Fact]
	public async Task Expired_401()
	{
		var token = _issuer.Token(expires: DateTime.UtcNow.AddMinutes(-5));

		var status = await RunAsync(ctx => ctx.Request.Headers[CloudflareAccessMiddleware.HeaderName] = token);

		Assert.Equal(StatusCodes.Status401Unauthorized, status);
	}

	[Fact]
	public async Task NoExpiry_401()
	{
		var token = _issuer.Token(omitExpiry: true);

		var status = await RunAsync(ctx => ctx.Request.Headers[CloudflareAccessMiddleware.HeaderName] = token);

		Assert.Equal(StatusCodes.Status401Unauthorized, status);
	}

	[Fact]
	public async Task NotYetValid_401()
	{
		var token = _issuer.Token(notBefore: DateTime.UtcNow.AddMinutes(10));

		var status = await RunAsync(ctx => ctx.Request.Headers[CloudflareAccessMiddleware.HeaderName] = token);

		Assert.Equal(StatusCodes.Status401Unauthorized, status);
	}

	[Fact]
	public async Task HmacSignedWithMatchingKid_401()
	{
		var token = _issuer.HmacToken();

		var status = await RunAsync(ctx => ctx.Request.Headers[CloudflareAccessMiddleware.HeaderName] = token);

		Assert.Equal(StatusCodes.Status401Unauthorized, status);
	}

	[Fact]
	public async Task UnsignedAlgNone_401()
	{
		var token = _issuer.UnsignedToken();

		var status = await RunAsync(ctx => ctx.Request.Headers[CloudflareAccessMiddleware.HeaderName] = token);

		Assert.Equal(StatusCodes.Status401Unauthorized, status);
	}

	[Fact]
	public async Task ForeignKey_401()
	{
		var foreign = new TestAccessIssuer("kid-1");

		var status = await RunAsync(ctx => ctx.Request.Headers[CloudflareAccessMiddleware.HeaderName] = foreign.Token());

		Assert.Equal(StatusCodes.Status401Unauthorized, status);
	}

	[Fact]
	public async Task UnknownKid_RefetchesJwksAtMostOncePerMinute()
	{
		var stranger = new TestAccessIssuer("kid-unknown");
		var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
		var jwks = _issuer.Handler();
		var (validator, _) = Build(jwks, time, enabled: true);
		var ct = TestContext.Current.CancellationToken;

		Assert.True(await validator.ValidateAsync(_issuer.Token(), ct));
		Assert.False(await validator.ValidateAsync(stranger.Token(), ct));
		Assert.False(await validator.ValidateAsync(stranger.Token(), ct));
		Assert.Equal(1, jwks.Requests);

		time.Now += TimeSpan.FromMinutes(2);
		Assert.False(await validator.ValidateAsync(stranger.Token(), ct));
		Assert.Equal(2, jwks.Requests);
	}

	[Fact]
	public async Task JwksEndpointFailing_FetchedAtMostOncePerMinute()
	{
		var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
		var stub = new StubHandler(HttpStatusCode.InternalServerError, "boom");
		var (validator, _) = Build(stub, time, enabled: true);
		var ct = TestContext.Current.CancellationToken;

		await Assert.ThrowsAnyAsync<HttpRequestException>(() => validator.ValidateAsync(_issuer.Token(), ct));
		Assert.False(await validator.ValidateAsync(_issuer.Token(), ct));
		Assert.Equal(1, stub.Requests);

		time.Now += TimeSpan.FromMinutes(2);
		await Assert.ThrowsAnyAsync<HttpRequestException>(() => validator.ValidateAsync(_issuer.Token(), ct));
		Assert.Equal(2, stub.Requests);
	}

	[Fact]
	public async Task EmptyJwks_FetchedAtMostOncePerMinute()
	{
		var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
		var stub = new StubHandler(HttpStatusCode.OK, "{\"keys\":[]}");
		var (validator, _) = Build(stub, time, enabled: true);
		var ct = TestContext.Current.CancellationToken;

		Assert.False(await validator.ValidateAsync(_issuer.Token(), ct));
		Assert.False(await validator.ValidateAsync(_issuer.Token(), ct));
		Assert.Equal(1, stub.Requests);
	}

	[Fact]
	public async Task CallerCancelledDuringColdFetch_DoesNotPoisonKeyCache()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var handler = new GatedHandler(_issuer.Jwks(), gate.Task, entered);
		var (validator, _) = Build(handler, TimeProvider.System, enabled: true);
		using var cts = new CancellationTokenSource();

		var first = validator.ValidateAsync(_issuer.Token(), cts.Token);
		await entered.Task;
		await cts.CancelAsync();
		gate.SetResult();
		try
		{
			await first;
		}
		catch (OperationCanceledException)
		{
		}

		Assert.True(await validator.ValidateAsync(_issuer.Token(), TestContext.Current.CancellationToken));
		Assert.Equal(1, handler.Requests);
	}

	[Fact]
	public async Task Disabled_PassesWithoutToken()
	{
		var (validator, options) = Build(_issuer.Handler(), TimeProvider.System, enabled: false);
		var ctx = new DefaultHttpContext();
		var middleware = new CloudflareAccessMiddleware(Ok, options, validator);

		await middleware.InvokeAsync(ctx);

		Assert.Equal(StatusCodes.Status204NoContent, ctx.Response.StatusCode);
	}

	private static (CloudflareAccessTokenValidator Validator, IOptions<CloudflareAccessOptions> Options) Build(
		HttpMessageHandler jwks, TimeProvider time, bool enabled)
	{
		var services = new ServiceCollection();
		services.AddHttpClient(CloudflareAccessTokenValidator.JwksHttpClient).ConfigurePrimaryHttpMessageHandler(() => jwks);
		var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
		var options = Options.Create(new CloudflareAccessOptions
		{
			Enabled = enabled,
			TeamDomain = TestAccessIssuer.TeamDomain,
			Audience = TestAccessIssuer.Audience,
		});
		return (new CloudflareAccessTokenValidator(factory, options, time), options);
	}

	private static Task Ok(HttpContext context)
	{
		context.Response.StatusCode = StatusCodes.Status204NoContent;
		return Task.CompletedTask;
	}

	private async Task<int> RunAsync(Action<HttpContext> arrange)
	{
		var (validator, options) = Build(_issuer.Handler(), TimeProvider.System, enabled: true);
		var ctx = new DefaultHttpContext();
		arrange(ctx);
		var middleware = new CloudflareAccessMiddleware(Ok, options, validator);

		await middleware.InvokeAsync(ctx);

		return ctx.Response.StatusCode;
	}

	private sealed class GatedHandler(string body, Task gate, TaskCompletionSource entered) : HttpMessageHandler
	{
		public int Requests { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests++;
			entered.TrySetResult();
			await gate.WaitAsync(cancellationToken);
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
		}
	}

	private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
	{
		public int Requests { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests++;
			return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
		}
	}
}
```

Write the full content of `tests/AiChromeProxy.Tests/Server/TransportHubTests.cs`:

```csharp
using System.Net;
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Server.Security;
using AiChromeProxy.Server.Transport;
using AiChromeProxy.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiChromeProxy.Tests.Server;

/// <summary>End-to-end: real Server pipeline (Access check on) + real SignalRTransport over WebSocket.</summary>
public sealed class TransportHubTests : IAsyncDisposable
{
	private readonly TestAccessIssuer _issuer = new();
	private readonly WebApplicationFactory<Program> _factory;

	public TransportHubTests()
	{
		_factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment(Environments.Production);
			b.UseStaticWebAssets();
			b.UseSetting("CloudflareAccess:TeamDomain", TestAccessIssuer.TeamDomain);
			b.UseSetting("CloudflareAccess:Audience", TestAccessIssuer.Audience);
			b.ConfigureServices(s => s.AddHttpClient(CloudflareAccessTokenValidator.JwksHttpClient)
				.ConfigurePrimaryHttpMessageHandler(() => _issuer.Handler()));
		});
	}

	[Fact]
	public async Task Ping_OverWebSocket_ReturnsPong()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var transport = new SignalRTransport(Connection(_issuer.Token()));
		var states = new List<TransportState>();
		transport.StateChanged += states.Add;
		var pong = new TaskCompletionSource<Envelope>();
		transport.Received += e => pong.TrySetResult(e);

		await transport.ConnectAsync(ct);
		await transport.SendAsync(Envelope.Create(MessageTypes.Ping, new { }, "rt-1"), ct);
		var reply = await pong.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

		Assert.Equal(TransportState.Connected, transport.State);
		Assert.Equal([TransportState.Connecting, TransportState.Connected], states);
		Assert.Equal(MessageTypes.Pong, reply.Type);
		Assert.Equal("rt-1", reply.CorrelationId);
	}

	[Fact]
	public async Task NoToken_ConnectionRejected()
	{
		await using var transport = new SignalRTransport(Connection(token: null));

		var ex = await Assert.ThrowsAnyAsync<Exception>(() => transport.ConnectAsync(TestContext.Current.CancellationToken));
		Assert.Contains("401", ex.Message);
		Assert.Equal(TransportState.Disconnected, transport.State);
	}

	[Theory]
	[InlineData("GET", "/")]
	[InlineData("GET", "/index.html")]
	[InlineData("GET", "/_framework/blazor.webassembly.js")]
	[InlineData("GET", "/css/app.css")]
	[InlineData("GET", "/some/client/route")]
	[InlineData("POST", "/hub/negotiate?negotiateVersion=1")]
	public async Task NoToken_EveryEntryPoint_401(string method, string path)
	{
		using var client = _factory.CreateClient();
		using var request = new HttpRequestMessage(new HttpMethod(method), path);

		using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task StaticFile_WithToken_200()
	{
		using var client = _factory.CreateClient();
		using var request = new HttpRequestMessage(HttpMethod.Get, "/css/app.css");
		request.Headers.Add(CloudflareAccessMiddleware.HeaderName, _issuer.Token());

		using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
	}

	[Fact]
	public void Production_WithoutAccessConfig_FailsToStart()
	{
		// Explicit empty values: the machine running the tests may have real CloudflareAccess__* env vars set.
		using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment(Environments.Production);
			b.UseSetting("CloudflareAccess:TeamDomain", string.Empty);
			b.UseSetting("CloudflareAccess:Audience", string.Empty);
		});

		var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

		Assert.Contains("CloudflareAccess", ex.ToString());
	}

	public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

	private HubConnection Connection(string? token)
	{
		var server = _factory.Server;
		return new HubConnectionBuilder()
			.WithUrl(new Uri(server.BaseAddress, TransportHub.Path), o =>
			{
				o.Transports = HttpTransportType.WebSockets;
				o.SkipNegotiation = true;
				o.WebSocketFactory = async (ctx, ct) =>
				{
					var ws = server.CreateWebSocketClient();
					if (token is not null)
					{
						ws.ConfigureRequest = r => r.Headers[CloudflareAccessMiddleware.HeaderName] = token;
					}

					return await ws.ConnectAsync(ctx.Uri, ct);
				};
			})
			.Build();
	}
}
```

- [ ] **Step 4: Build and run the existing suite**

Run: `dotnet build -c Release` → Expected: `0 Warning(s)`, `0 Error(s)`.
Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total` → Expected: `total: 39, failed: 0`, exit code 0 (behavior unchanged).
Check nothing still references the old project: `rg -n "AiChromeProxy\.Shared" --glob '!docs/**'` → no matches.

- [ ] **Step 5: Write the architecture tests**

Create `tests/AiChromeProxy.Tests/Architecture/LayerDependencyTests.cs`:

```csharp
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
			"Microsoft.AspNetCore",
			"Microsoft.IdentityModel");
	}

	[Fact]
	public void Infrastructure_DoesNotReachUp()
	{
		AssertNoDependency(InfrastructureAssembly, "AiChromeProxy.Server", "AiChromeProxy.Client");
	}

	[Fact]
	public void Client_TalksContractsOnly()
	{
		AssertNoDependency(ClientAssembly, "AiChromeProxy.Application", "AiChromeProxy.Infrastructure", "AiChromeProxy.Server");
	}

	/// <summary>Guards the rules above against passing vacuously (e.g. if the assembly reader stopped seeing references).</summary>
	[Fact]
	public void Server_DependsOnApplicationAndInfrastructure()
	{
		Assert.NotEmpty(Types.InAssembly(ServerAssembly).That().HaveDependencyOn("AiChromeProxy.Application").GetTypes());
		Assert.NotEmpty(Types.InAssembly(ServerAssembly).That().HaveDependencyOn("AiChromeProxy.Infrastructure").GetTypes());
	}

	private static void AssertNoDependency(Assembly assembly, params string[] forbidden)
	{
		var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();

		Assert.True(result.IsSuccessful, $"{assembly.GetName().Name} must not depend on {string.Join(", ", forbidden)}; violating types: {string.Join(", ", result.FailingTypeNames ?? [])}");
	}
}
```

- [ ] **Step 6: Prove the rules can fail (RED sanity check, not committed)**

Temporarily add `<ProjectReference Include="..\AiChromeProxy.Application\AiChromeProxy.Application.csproj" />` to `src/AiChromeProxy.Client/AiChromeProxy.Client.csproj` and create `src/AiChromeProxy.Client/Violation.cs`:

```csharp
namespace AiChromeProxy.Client;

internal static class Violation
{
	internal static Type Router() => typeof(AiChromeProxy.Application.Transport.EnvelopeRouter);
}
```

Run: `dotnet build -c Release` then `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total`.
Expected: FAIL — `Client_TalksContractsOnly` reports violating type `AiChromeProxy.Client.Violation`.
Then delete `Violation.cs` and remove the temporary ProjectReference.

- [ ] **Step 7: Green**

Run: `dotnet build -c Release` → `0 Error(s)`.
Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total` → Expected: `total: 44, failed: 0`, exit code 0.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "refactor: minimal clean architecture (Domain/Application/Infrastructure/Server) with architecture tests"
```

---

### Task 2: E2E BDD — Reqnroll + Playwright

**Files:**
- Create: `tests/AiChromeProxy.E2E/AiChromeProxy.E2E.csproj`, `Features/Connection.feature`, `Hooks/AppServer.cs`, `Hooks/BrowserHooks.cs`, `StepDefinitions/ConnectionSteps.cs`
- Modify: `.gitignore` (un-ignore the E2E folder), `AiChromeProxy.slnx`, `src/AiChromeProxy.Client/Pages/Home.razor` (`data-testid` attributes)

**Interfaces:**
- Consumes: `Program` (Server, `public partial`), config keys `CloudflareAccess:Enabled`, `Server:Port` (Task 1 unchanged behavior).
- Produces: `data-testid` values `connection-state`, `ping-button`, `ping-result` in `Home.razor`.

- [ ] **Step 1: Un-ignore the E2E project**

The Visual Studio template line `*.e2e` in `.gitignore` matches the folder `AiChromeProxy.E2E` on case-insensitive file systems and silently hides the whole project. Directly after the `*.e2e` line add:

```gitignore
# ...but not the E2E test project (matches *.e2e on case-insensitive file systems)
!tests/AiChromeProxy.E2E/
```

- [ ] **Step 2: Test ids in the UI**

Write the full content of `src/AiChromeProxy.Client/Pages/Home.razor`:

```razor
@page "/"
@using System.Diagnostics
@using AiChromeProxy.Client.Transport
@using AiChromeProxy.Domain
@inject ITransport Transport
@implements IDisposable

<PageTitle>ai-chrome-proxy</PageTitle>

<h1>ai-chrome-proxy</h1>

<p>Connection: <strong data-testid="connection-state">@Transport.State</strong></p>

<button data-testid="ping-button" @onclick="PingAsync" disabled="@(Transport.State != TransportState.Connected)">Ping</button>

@if (lastResult is not null)
{
	<p data-testid="ping-result">@lastResult</p>
}

@code {
	private readonly Dictionary<string, Stopwatch> pending = new();
	private string? lastResult;

	protected override async Task OnInitializedAsync()
	{
		Transport.StateChanged += OnStateChanged;
		Transport.Received += OnReceived;
		if (Transport.State == TransportState.Disconnected)
		{
			try
			{
				await Transport.ConnectAsync();
			}
			catch (Exception ex)
			{
				lastResult = $"Connect failed: {ex.Message}";
			}
		}
	}

	public void Dispose()
	{
		Transport.StateChanged -= OnStateChanged;
		Transport.Received -= OnReceived;
	}

	private async Task PingAsync()
	{
		var id = Guid.NewGuid().ToString("N");
		pending[id] = Stopwatch.StartNew();
		await Transport.SendAsync(Envelope.Create(MessageTypes.Ping, new { }, id));
	}

	private void OnStateChanged(TransportState state) => InvokeAsync(StateHasChanged);

	private void OnReceived(Envelope envelope)
	{
		if (envelope.Type != MessageTypes.Pong || envelope.CorrelationId is null || !pending.Remove(envelope.CorrelationId, out var sw))
		{
			return;
		}

		var serverTime = envelope.Payload.GetProperty("serverTime").GetString();
		lastResult = $"Pong in {sw.ElapsedMilliseconds} ms, server time {serverTime}";
		InvokeAsync(StateHasChanged);
	}
}
```

- [ ] **Step 3: E2E project**

Create `tests/AiChromeProxy.E2E/AiChromeProxy.E2E.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <!-- Reqnroll code-behind (*.feature.cs) goes to obj/, out of the source tree and git. -->
    <ReqnrollUseIntermediateOutputPathForCodeBehind>true</ReqnrollUseIntermediateOutputPathForCodeBehind>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\AiChromeProxy.Server\AiChromeProxy.Server.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.12" />
    <PackageReference Include="Microsoft.Playwright" Version="1.63.0" />
    <PackageReference Include="Reqnroll.xunit.v3" Version="3.3.4" />
    <PackageReference Include="xunit.v3" Version="4.0.1" />
  </ItemGroup>

</Project>
```

Create `tests/AiChromeProxy.E2E/Features/Connection.feature`:

```gherkin
Feature: Connection
	The status page connects to the server hub over SignalR and round-trips a ping.

Scenario: Status page connects
	Given the server is running
	When I open the app
	Then the connection state is "Connected"

Scenario: Ping shows a round trip
	Given the app is connected
	When I click "Ping"
	Then I see "Pong in <n> ms" with a server time
```

Create `tests/AiChromeProxy.E2E/Hooks/AppServer.cs`:

```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiChromeProxy.E2E.Hooks;

/// <summary>The real Server on a Kestrel listener (random loopback port) so a real browser can reach it.</summary>
public sealed class AppServer : IAsyncDisposable
{
	private readonly WebApplicationFactory<Program> _factory;

	public AppServer()
	{
		_factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment(Environments.Development);
			b.UseSetting("CloudflareAccess:Enabled", "false");
			b.UseSetting("Server:Port", "0");
		});
		_factory.UseKestrel();
		_factory.StartServer();

		var addresses = _factory.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses;
		BaseUrl = addresses.Single();
	}

	public string BaseUrl { get; }

	public ValueTask DisposeAsync() => _factory.DisposeAsync();
}
```

Create `tests/AiChromeProxy.E2E/Hooks/BrowserHooks.cs`:

```csharp
using Microsoft.Playwright;
using Reqnroll;
using Reqnroll.BoDi;

namespace AiChromeProxy.E2E.Hooks;

/// <summary>One server and one browser per test run; a fresh browser context (and <see cref="IPage"/>) per scenario.</summary>
[Binding]
public sealed class BrowserHooks(IObjectContainer container)
{
	private static AppServer? _server;
	private static IPlaywright? _playwright;
	private static IBrowser? _browser;

	private IBrowserContext? _context;

	[BeforeTestRun]
	public static async Task StartAsync()
	{
		_server = new AppServer();
		_playwright = await Playwright.CreateAsync();
		_browser = await _playwright.Chromium.LaunchAsync(new() { Headless = Environment.GetEnvironmentVariable("HEADED") != "1" });
		Assertions.SetDefaultExpectTimeout(15_000);
	}

	[AfterTestRun]
	public static async Task StopAsync()
	{
		if (_browser is not null)
		{
			await _browser.DisposeAsync();
		}

		_playwright?.Dispose();
		if (_server is not null)
		{
			await _server.DisposeAsync();
		}
	}

	[BeforeScenario]
	public async Task OpenPageAsync()
	{
		_context = await _browser!.NewContextAsync(new() { BaseURL = _server!.BaseUrl });
		container.RegisterInstanceAs(await _context.NewPageAsync());
	}

	[AfterScenario]
	public async Task ClosePageAsync()
	{
		if (_context is not null)
		{
			await _context.DisposeAsync();
		}
	}
}
```

Create `tests/AiChromeProxy.E2E/StepDefinitions/ConnectionSteps.cs`:

```csharp
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Reqnroll;
using static Microsoft.Playwright.Assertions;

namespace AiChromeProxy.E2E.StepDefinitions;

[Binding]
public sealed partial class ConnectionSteps(IPage page)
{
	[Given("the server is running")]
	public async Task GivenTheServerIsRunningAsync()
	{
		await Expect(await page.APIRequest.GetAsync("/")).ToBeOKAsync();
	}

	[When("I open the app")]
	public async Task WhenIOpenTheAppAsync()
	{
		await page.GotoAsync("/");
	}

	[Then("the connection state is {string}")]
	public async Task ThenTheConnectionStateIsAsync(string state)
	{
		await Expect(page.GetByTestId("connection-state")).ToHaveTextAsync(state);
	}

	[Given("the app is connected")]
	public async Task GivenTheAppIsConnectedAsync()
	{
		await WhenIOpenTheAppAsync();
		await ThenTheConnectionStateIsAsync("Connected");
	}

	[When("I click \"Ping\"")]
	public async Task WhenIClickPingAsync()
	{
		await page.GetByTestId("ping-button").ClickAsync();
	}

	[Then("I see \"Pong in <n> ms\" with a server time")]
	public async Task ThenISeePongWithServerTimeAsync()
	{
		await Expect(page.GetByTestId("ping-result")).ToHaveTextAsync(PongText());
	}

	/// <summary>Home.razor: "Pong in {ms} ms, server time {DateTimeOffset UTC, round-trip format}".</summary>
	[GeneratedRegex(@"^Pong in \d+ ms, server time \d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+\+00:00$")]
	private static partial Regex PongText();
}
```

Write the full content of `AiChromeProxy.slnx`:

```xml
<Solution>
  <Folder Name="/src/">
    <Project Path="src/AiChromeProxy.Application/AiChromeProxy.Application.csproj" />
    <Project Path="src/AiChromeProxy.Client/AiChromeProxy.Client.csproj" />
    <Project Path="src/AiChromeProxy.Domain/AiChromeProxy.Domain.csproj" />
    <Project Path="src/AiChromeProxy.Infrastructure/AiChromeProxy.Infrastructure.csproj" />
    <Project Path="src/AiChromeProxy.Server/AiChromeProxy.Server.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/AiChromeProxy.E2E/AiChromeProxy.E2E.csproj" />
    <Project Path="tests/AiChromeProxy.Tests/AiChromeProxy.Tests.csproj" />
  </Folder>
</Solution>
```

Notes: `<OutputType>Exe</OutputType>` is required (xunit v3 test projects must be executable; this project does not reference Microsoft.NET.Test.Sdk). `ReqnrollUseIntermediateOutputPathForCodeBehind=true` keeps generated `*.feature.cs` in `obj/` (it is marked auto-generated, so StyleCop skips it).

- [ ] **Step 4: Build and install the browser (one-time)**

Run: `dotnet build -c Release` → `0 Warning(s)`, `0 Error(s)`.
Run: `dotnet build tests/AiChromeProxy.E2E` then
`powershell -ExecutionPolicy Bypass -File tests/AiChromeProxy.E2E/bin/Debug/net10.0/playwright.ps1 install chromium`.
If the download times out (hosts with a broken IPv6 route), use the IPv4-only preload:

```powershell
Set-Content -Encoding ascii "$env:TEMP\pw-ipv4only.cjs" "const d=require('dns'),l=d.promises.lookup;d.promises.lookup=(h,o)=>o&&o.family===6?Promise.resolve([]):l(h,o);"
$env:NODE_OPTIONS = "--require $env:TEMP\pw-ipv4only.cjs"
powershell -ExecutionPolicy Bypass -File tests/AiChromeProxy.E2E/bin/Debug/net10.0/playwright.ps1 install chromium
Remove-Item Env:NODE_OPTIONS
```

- [ ] **Step 5: Run E2E (RED first)**

Temporarily change the expected state in `Connection.feature` from `"Connected"` to `"Reconnecting"`.
Run: `dotnet test --project tests/AiChromeProxy.E2E` → Expected: FAIL with `Locator expected to have text 'Reconnecting'` (after the 15 s expect timeout). Restore `"Connected"`.

- [ ] **Step 6: Run E2E (GREEN)**

Run: `dotnet test --project tests/AiChromeProxy.E2E` → Expected: `total: 2, failed: 0` (headless).
Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total` → still `total: 44, failed: 0`, exit 0 (the E2E project is not part of the gate).

- [ ] **Step 7: Commit**

```bash
git add -A
git status --short   # tests/AiChromeProxy.E2E/ files must be listed
git commit -m "test: E2E BDD scenarios with Reqnroll and Playwright"
```

---

### Task 3: k6 load tests

**Files:**
- Create: `tests/load/lib/signalr.js`, `tests/load/smoke.js`, `tests/load/load.js`

**Interfaces:**
- Consumes: hub `/hub` (SignalR JSON protocol), `Send(Envelope)` / `Receive(Envelope)`, `ping` → `pong` with matching `correlationId`.

- [ ] **Step 1: Scripts**

Create `tests/load/lib/signalr.js`:

```javascript
// Minimal SignalR client for k6: JSON hub protocol over a WebSocket, no negotiate step.
// Wire format: every message is a JSON object followed by the record separator (0x1E).
import { check } from 'k6';
import { Rate, Trend } from 'k6/metrics';
import ws from 'k6/ws';

const RS = '\x1e';
const INVOCATION = 1;
const CLOSE = 7;

export const baseUrl = __ENV.BASE_URL || 'http://127.0.0.1:5180';

/** Ping sent -> matching pong received, in milliseconds. */
export const pongLatency = new Trend('pong_latency', true);

/** Share of pings that got no pong (plus failed connections). */
export const pingErrors = new Rate('ping_errors');

/**
 * Opens one hub connection, sends a `ping` Envelope every second for `seconds`,
 * waits up to 5 s for outstanding pongs, then closes.
 */
export function pingSession(seconds) {
	const url = `${baseUrl.replace(/^http/, 'ws')}/hub`;
	const pending = new Map();
	let seq = 0;
	let draining = false;

	const res = ws.connect(url, null, (socket) => {
		const sendPing = () => {
			if (draining) {
				return;
			}

			const correlationId = `${__VU}-${__ITER}-${seq++}`;
			pending.set(correlationId, Date.now());
			socket.send(JSON.stringify({
				type: INVOCATION,
				target: 'Send',
				arguments: [{ type: 'ping', payload: {}, correlationId }],
			}) + RS);
		};

		const onReceive = (envelope) => {
			const sentAt = pending.get(envelope.correlationId);
			if (sentAt === undefined) {
				return;
			}

			pending.delete(envelope.correlationId);
			pongLatency.add(Date.now() - sentAt);
			const ok = check(envelope, {
				'pong with serverTime': (e) => e.type === 'pong' && typeof e.payload.serverTime === 'string',
			});
			pingErrors.add(!ok);
			if (draining && pending.size === 0) {
				socket.close();
			}
		};

		socket.on('open', () => socket.send(JSON.stringify({ protocol: 'json', version: 1 }) + RS));

		socket.on('message', (data) => {
			for (const frame of data.split(RS)) {
				if (!frame) {
					continue;
				}

				const message = JSON.parse(frame);
				if (message.type === undefined) {
					// Handshake response: {} on success, {"error":"..."} on failure.
					if (check(message, { 'handshake accepted': (m) => !m.error })) {
						sendPing();
						socket.setInterval(sendPing, 1000);
					} else {
						socket.close();
					}
				} else if (message.type === INVOCATION && message.target === 'Receive') {
					onReceive(message.arguments[0]);
				} else if (message.type === CLOSE) {
					socket.close();
				}
			}
		});

		socket.setTimeout(() => {
			draining = true;
			if (pending.size === 0) {
				socket.close();
			}
		}, seconds * 1000);
		socket.setTimeout(() => socket.close(), (seconds + 5) * 1000);
	});

	const connected = check(res, { 'websocket upgraded (101)': (r) => r && r.status === 101 });
	if (!connected) {
		pingErrors.add(true);
	}

	for (let i = 0; i < pending.size; i++) {
		pingErrors.add(true);
	}
}
```

Create `tests/load/smoke.js`:

```javascript
// Smoke: 2 connections, one ping per second each, 30 s.
// k6 run tests/load/smoke.js   (BASE_URL defaults to http://127.0.0.1:5180)
import { pingSession } from './lib/signalr.js';

export const options = {
	vus: 2,
	duration: '30s',
	thresholds: {
		pong_latency: ['p(95)<200'],
		checks: ['rate>0.99'],
	},
};

export default function () {
	pingSession(10);
}
```

Create `tests/load/load.js`:

```javascript
// Load: ramp 0 -> 200 concurrent connections over 1 min, hold 2 min, ramp down; one ping per second each.
// k6 run tests/load/load.js   (BASE_URL defaults to http://127.0.0.1:5180)
// DURATION_SCALE=0.1 shortens every stage proportionally for a quick check.
import { pingSession } from './lib/signalr.js';

const scale = Number(__ENV.DURATION_SCALE || 1);
const stage = (seconds) => `${Math.max(1, Math.round(seconds * scale))}s`;

export const options = {
	stages: [
		{ duration: stage(60), target: 200 },
		{ duration: stage(120), target: 200 },
		{ duration: stage(30), target: 0 },
	],
	thresholds: {
		pong_latency: ['p(95)<500'],
		ping_errors: ['rate<0.01'],
	},
};

export default function () {
	pingSession(10);
}
```

- [ ] **Step 2: Start a local server (terminal 1)**

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"; $env:CloudflareAccess__Enabled = "false"
dotnet run -c Release --project src/AiChromeProxy.Server --no-launch-profile
```

- [ ] **Step 3: Prove thresholds are real (RED)**

Run (terminal 2): `$env:BASE_URL = "http://127.0.0.1:5999"; k6 run tests/load/smoke.js; Remove-Item Env:BASE_URL`
Expected: thresholds fail, k6 exits with code 99.

- [ ] **Step 4: Smoke and short load (GREEN)**

Run: `k6 run tests/load/smoke.js` → Expected: all thresholds pass (`checks rate>0.99`, `pong_latency p(95)<200`), exit 0.
Run: `$env:DURATION_SCALE = "0.1"; k6 run tests/load/load.js; Remove-Item Env:DURATION_SCALE` → Expected: `ping_errors rate<0.01` and `pong_latency p(95)<500` pass, `vus_max` 200, exit 0.
Stop the server; verify nothing listens on 5180 (`netstat -ano | rg ':5180 .*LISTEN'` prints nothing).

- [ ] **Step 5: Commit**

```bash
git add tests/load
git commit -m "test: k6 smoke and load scripts over the SignalR JSON protocol"
```

---

### Task 4: BenchmarkDotNet

**Files:**
- Create: `benchmarks/AiChromeProxy.Benchmarks/{AiChromeProxy.Benchmarks.csproj, Program.cs, EnvelopeBenchmarks.cs, RouterBenchmarks.cs, TokenValidationBenchmarks.cs}`
- Modify: `AiChromeProxy.slnx`

**Interfaces:**
- Consumes: `Envelope.Create`, `EnvelopeRouter`, `PingHandler`, `CloudflareAccessTokenValidator`, `CloudflareAccessOptions` (Task 1).

Benchmark classes must be `public` and not `sealed` (BenchmarkDotNet requirement) — a deliberate exception to the codebase's `sealed` default.

- [ ] **Step 1: Project and benchmarks**

Create `benchmarks/AiChromeProxy.Benchmarks/AiChromeProxy.Benchmarks.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\AiChromeProxy.Application\AiChromeProxy.Application.csproj" />
    <ProjectReference Include="..\..\src\AiChromeProxy.Domain\AiChromeProxy.Domain.csproj" />
    <ProjectReference Include="..\..\src\AiChromeProxy.Infrastructure\AiChromeProxy.Infrastructure.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="BenchmarkDotNet" Version="0.15.8" />
  </ItemGroup>

</Project>
```

Create `benchmarks/AiChromeProxy.Benchmarks/Program.cs`:

```csharp
using BenchmarkDotNet.Running;

// dotnet run -c Release --project benchmarks/AiChromeProxy.Benchmarks -- --filter *
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
```

Create `benchmarks/AiChromeProxy.Benchmarks/EnvelopeBenchmarks.cs`:

```csharp
using System.Text.Json;
using AiChromeProxy.Domain;
using BenchmarkDotNet.Attributes;

namespace AiChromeProxy.Benchmarks;

[MemoryDiagnoser]
public class EnvelopeBenchmarks
{
	private static readonly SampleDto Dto = new("src/AiChromeProxy.Server/Program.cs", 42);

	private readonly Envelope _envelope = Envelope.Create("sample", Dto, "c1");

	[Benchmark]
	public Envelope Create() => Envelope.Create("sample", Dto, "c1");

	[Benchmark]
	public string Serialize() => JsonSerializer.Serialize(_envelope, JsonSerializerOptions.Web);

	public sealed record SampleDto(string FilePath, int LineCount);
}
```

Create `benchmarks/AiChromeProxy.Benchmarks/RouterBenchmarks.cs`:

```csharp
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using BenchmarkDotNet.Attributes;

namespace AiChromeProxy.Benchmarks;

[MemoryDiagnoser]
public class RouterBenchmarks
{
	private static readonly Envelope Ping = Envelope.Create(MessageTypes.Ping, new { }, "c1");
	private static readonly Envelope Unknown = Envelope.Create("unknown", new { }, "c2");

	private readonly EnvelopeRouter _router = new([new PingHandler(TimeProvider.System)]);

	[Benchmark]
	public Task<Envelope?> RoutePingAsync() => _router.RouteAsync(Ping, CancellationToken.None);

	[Benchmark]
	public Task<Envelope?> RouteUnknownAsync() => _router.RouteAsync(Unknown, CancellationToken.None);
}
```

Create `benchmarks/AiChromeProxy.Benchmarks/TokenValidationBenchmarks.cs`:

```csharp
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AiChromeProxy.Infrastructure.Security;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AiChromeProxy.Benchmarks;

/// <summary>Valid RS256 token, JWKS already cached: in-process RSA key and a stub JWKS handler, no network.</summary>
[MemoryDiagnoser]
public class TokenValidationBenchmarks
{
	private const string TeamDomain = "bench-team.cloudflareaccess.com";
	private const string Audience = "bench-aud";

	private CloudflareAccessTokenValidator _validator = null!;
	private string _token = string.Empty;

	[GlobalSetup]
	public void Setup()
	{
		var key = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "bench-kid" };
		var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(key);
		var jwks = JsonSerializer.Serialize(new { keys = new[] { new { kty = jwk.Kty, kid = jwk.Kid, use = "sig", alg = "RS256", n = jwk.N, e = jwk.E } } });

		var services = new ServiceCollection();
		services.AddHttpClient(CloudflareAccessTokenValidator.JwksHttpClient).ConfigurePrimaryHttpMessageHandler(() => new JwksHandler(jwks));
		var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
		var options = Options.Create(new CloudflareAccessOptions { TeamDomain = TeamDomain, Audience = Audience });
		_validator = new CloudflareAccessTokenValidator(factory, options, TimeProvider.System);

		_token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
		{
			Issuer = $"https://{TeamDomain}",
			Audience = Audience,
			Expires = DateTime.UtcNow.AddHours(1),
			SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
		});

		// Warms the key cache, so the benchmark measures validation only.
		if (!_validator.ValidateAsync(_token, CancellationToken.None).GetAwaiter().GetResult())
		{
			throw new InvalidOperationException("Benchmark token must be valid.");
		}
	}

	[Benchmark]
	public Task<bool> ValidateAsync() => _validator.ValidateAsync(_token, CancellationToken.None);

	private sealed class JwksHandler(string jwks) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(jwks) });
	}
}
```

Write the full content of `AiChromeProxy.slnx`:

```xml
<Solution>
  <Folder Name="/benchmarks/">
    <Project Path="benchmarks/AiChromeProxy.Benchmarks/AiChromeProxy.Benchmarks.csproj" />
  </Folder>
  <Folder Name="/src/">
    <Project Path="src/AiChromeProxy.Application/AiChromeProxy.Application.csproj" />
    <Project Path="src/AiChromeProxy.Client/AiChromeProxy.Client.csproj" />
    <Project Path="src/AiChromeProxy.Domain/AiChromeProxy.Domain.csproj" />
    <Project Path="src/AiChromeProxy.Infrastructure/AiChromeProxy.Infrastructure.csproj" />
    <Project Path="src/AiChromeProxy.Server/AiChromeProxy.Server.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/AiChromeProxy.E2E/AiChromeProxy.E2E.csproj" />
    <Project Path="tests/AiChromeProxy.Tests/AiChromeProxy.Tests.csproj" />
  </Folder>
</Solution>
```

- [ ] **Step 2: Build**

Run: `dotnet build -c Release` → `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 3: Run a short job**

Run: `dotnet run -c Release --project benchmarks/AiChromeProxy.Benchmarks -- --filter '*' --job short`
Expected: exit 0; summary table lists 5 benchmarks (`EnvelopeBenchmarks.Create`, `EnvelopeBenchmarks.Serialize`, `RouterBenchmarks.RoutePingAsync`, `RouterBenchmarks.RouteUnknownAsync`, `TokenValidationBenchmarks.ValidateAsync`). `BenchmarkDotNet.Artifacts/` is gitignored (`git status` shows no artifacts).

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "perf: BenchmarkDotNet suite for envelope, router and token validation"
```

---

### Task 5: Testing docs

**Files:**
- Create: `docs/testing.md`
- Modify: `CLAUDE.md`

- [ ] **Step 1: Docs**

Create `docs/testing.md`:

````markdown
# Testing

Four layers. Only the first runs in CI; the others are local tools.

| Layer | Location | What it covers | Runs |
|---|---|---|---|
| Unit + integration + architecture | `tests/AiChromeProxy.Tests` (`Domain/`, `Application/`, `Infrastructure/`, `Server/`, `Architecture/`) | Envelope contract, routing, handlers, Cloudflare Access validation and middleware, the real Server pipeline over SignalR (in-memory `TestServer`), layer dependency rules (NetArchTest) | CI + local, 85% line-coverage gate |
| E2E BDD | `tests/AiChromeProxy.E2E` — Reqnroll + Playwright | The real app in a real browser: the status page connects, Ping round-trips | local |
| Load | `tests/load` — k6 | Concurrent SignalR connections pinging the hub; ping→pong latency and error rate | local |
| Micro-benchmarks | `benchmarks/AiChromeProxy.Benchmarks` — BenchmarkDotNet | Hot paths: `Envelope.Create`/serialization, `EnvelopeRouter.RouteAsync`, Access token validation | local |

Commands below run from the repo root. Shell snippets are Windows PowerShell 5.1 (`pwsh` works the same).

## Unit + integration + architecture (xunit v3)

xunit v3 on Microsoft.Testing.Platform (opted in via `global.json`), coverage via `coverlet.MTP`.

```powershell
dotnet build -c Release
dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total
```

Gate: `>= 85%` line coverage. Gate failed — add tests, don't lower the threshold.

Architecture rules (`Architecture/LayerDependencyTests.cs`):

- Domain depends on nothing of ours and not on ASP.NET Core.
- Application does not depend on Infrastructure, Server, ASP.NET Core or `Microsoft.IdentityModel`.
- Infrastructure does not depend on Server or Client.
- Client depends on Domain only (not on Application, Infrastructure or Server).

Always pass `--project`: a bare `dotnet test` runs every test project in the solution, including E2E (which needs a browser).

## E2E (Reqnroll + Playwright)

`Features/Connection.feature` is bound by `StepDefinitions/ConnectionSteps.cs`. `Hooks/AppServer.cs` starts the real Server once per run via `WebApplicationFactory<Program>` on a Kestrel listener (`127.0.0.1`, random port, `Development`, `CloudflareAccess:Enabled=false`). `Hooks/BrowserHooks.cs` launches one Chromium per run and opens a fresh browser context per scenario. Selectors are `data-testid` attributes in `Home.razor`.

One-time setup (downloads Chromium to `%LOCALAPPDATA%\ms-playwright`; repeat after a `Microsoft.Playwright` upgrade):

```powershell
dotnet build tests/AiChromeProxy.E2E
powershell -ExecutionPolicy Bypass -File tests/AiChromeProxy.E2E/bin/Debug/net10.0/playwright.ps1 install chromium
```

If the download fails after about 5 s with `Request to https://cdn.playwright.dev/... timed out after 30000ms`, the machine has a non-working IPv6 route (Playwright's downloader tries IPv6 first and Node reports the failed attempt as a timeout). Make it resolve IPv4 only for this one command:

```powershell
Set-Content -Encoding ascii "$env:TEMP\pw-ipv4only.cjs" "const d=require('dns'),l=d.promises.lookup;d.promises.lookup=(h,o)=>o&&o.family===6?Promise.resolve([]):l(h,o);"
$env:NODE_OPTIONS = "--require $env:TEMP\pw-ipv4only.cjs"
powershell -ExecutionPolicy Bypass -File tests/AiChromeProxy.E2E/bin/Debug/net10.0/playwright.ps1 install chromium
Remove-Item Env:NODE_OPTIONS
```

Run (headless):

```powershell
dotnet test --project tests/AiChromeProxy.E2E
```

Watch it in a visible browser:

```powershell
$env:HEADED = "1"; dotnet test --project tests/AiChromeProxy.E2E; Remove-Item Env:HEADED
```

Reqnroll generates the `*.feature.cs` code-behind into `obj/` (`ReqnrollUseIntermediateOutputPathForCodeBehind`); it carries an `<auto-generated>` header, so StyleCop skips it.

## Load (k6)

One-time setup: `winget install k6 --source winget`.

`tests/load/lib/signalr.js` speaks the SignalR JSON hub protocol over a plain WebSocket to `/hub` (no negotiate): handshake, `Send` invocations carrying a `ping` Envelope, `Receive` invocations parsed for the matching `pong`. Custom metrics: `pong_latency` (Trend, ms) and `ping_errors` (Rate; unanswered pings and failed connections). Each VU holds one connection for 10 s, pings once per second, then reconnects.

| Script | Shape | Thresholds |
|---|---|---|
| `smoke.js` | 2 VUs, 30 s | `pong_latency p(95) < 200 ms`, `checks rate > 0.99` |
| `load.js` | 0 → 200 connections over 1 min, hold 2 min, ramp down 30 s | `pong_latency p(95) < 500 ms`, `ping_errors rate < 1%` |

Terminal 1 — the server (Development, Access disabled):

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"; $env:CloudflareAccess__Enabled = "false"
dotnet run -c Release --project src/AiChromeProxy.Server --no-launch-profile
```

Terminal 2:

```powershell
k6 run tests/load/smoke.js
k6 run tests/load/load.js
```

`BASE_URL` (default `http://127.0.0.1:5180`) points the scripts elsewhere. `DURATION_SCALE` scales the `load.js` stages for a quick check (`$env:DURATION_SCALE = "0.1"` runs about 21 s). k6 exits non-zero when a threshold fails. Stop the server with Ctrl+C.

## Micro-benchmarks (BenchmarkDotNet)

```powershell
dotnet run -c Release --project benchmarks/AiChromeProxy.Benchmarks -- --filter *
```

Classes (all `[MemoryDiagnoser]`): `EnvelopeBenchmarks` (`Create`, `Serialize`), `RouterBenchmarks` (`RoutePingAsync`, `RouteUnknownAsync`), `TokenValidationBenchmarks` (`ValidateAsync`: valid RS256 token, keys cached, stub JWKS handler, no network). Pick one with `--filter *Router*`; add `--job short` for a quick, less precise run. Results go to `BenchmarkDotNet.Artifacts/` (gitignored). No committed baselines.
````

Write the full content of `CLAUDE.md`:

````markdown
# ai-chrome-proxy

Web app: Chrome on a locked-down machine syncs a repo folder to your home server, where Claude Code works on the mirror; the UI is a VS Code-like navigator + chat with mermaid diagrams.

Architecture and decisions: [docs/superpowers/specs/2026-10-02-architecture-design.md](docs/superpowers/specs/2026-10-02-architecture-design.md).

Open source (Apache-2.0). Everything in the repo is in English. No personal paths, domains or secrets in committed files — make it configurable.

## Stack

- .NET 10 (LTS, latest stable) and latest stable packages, C#, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`.
- Minimal clean architecture (dependencies point inward; enforced by `tests/AiChromeProxy.Tests/Architecture`):
  - `src/AiChromeProxy.Domain` — wire contract (`Envelope`, `MessageTypes`), BCL only.
  - `src/AiChromeProxy.Application` → Domain — envelope routing and handlers; `AddApplication()`.
  - `src/AiChromeProxy.Infrastructure` → Application, Domain — Cloudflare Access options and token validator; `AddInfrastructure(IConfiguration)`.
  - `src/AiChromeProxy.Server` — ASP.NET Core host and composition root (hosts Client, one SignalR hub routing `Envelope` by `Type`, Access middleware).
  - `src/AiChromeProxy.Client` — Blazor WebAssembly → Domain only.
- Tests: `tests/AiChromeProxy.Tests` (xunit v3, CI), `tests/AiChromeProxy.E2E` (Reqnroll + Playwright), `tests/load` (k6), `benchmarks/AiChromeProxy.Benchmarks` (BenchmarkDotNet) — see [docs/testing.md](docs/testing.md).
- JS only where C# can't: `fsaccess.js` (File System Access API), Monaco, mermaid.

## Process (SDD)

1. Sub-project spec → `docs/superpowers/specs/YYYY-MM-DD-<topic>-design.md`.
2. Plan → `docs/superpowers/plans/YYYY-MM-DD-<topic>.md`.
3. Implementation — subagent-driven development from the plan, final review.

Sub-projects in order: skeleton+transport → Windows host → sync → Claude chat → code navigator → mobile app (`mobile/`, Flutter).

## Code conventions

- All files are UTF-8 without BOM (enforced via `.editorconfig`). In PowerShell 5.1 pass `-Encoding utf8` explicitly when writing files.
- Search with ripgrep (`rg`), not `find` / `git grep` / `Select-String` — the primary dev environment is Windows.
- Style — `.editorconfig` + `StyleCop.ruleset` + `stylecop.json` (tabs, CRLF, no `this.` prefix, private fields `_camelCase`). StyleCop is applied to all projects via `Directory.Build.props`; rules with `Action="Error"` fail the build.
- Interfaces prefixed with `I`; async methods suffixed `Async`, returning `Task`/`Task<T>`.
- Logging — `ILogger<T>` via DI.
- Pure logic (SyncEngine, manifest diff, hash-guard, path normalization, stream-json parser) has no browser/IO dependencies and is covered by xUnit.
- Never commit secrets: `appsettings.json` holds non-secret defaults only; per-machine values and secrets come from environment variables.

## Security

- The `Envelope` JSON shape (camelCase) is a public contract shared with the Flutter app — change it compatibly.

- Every path from the protocol is normalized and checked for escaping its root (mirror on the server, picked folder in the browser).
- Cloudflare Access is mandatory before exposing Server publicly: it runs `claude` with permission to edit files and execute commands.

## Tests and coverage

xunit v3 on Microsoft.Testing.Platform (opted in via `global.json`), coverage via `coverlet.MTP` (`tests/AiChromeProxy.Tests/testconfig.json`).

```bash
dotnet build -c Release
dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total
```

Gate: `>= 85%` line coverage (coverlet exits non-zero below it). Gate failed — add tests, don't lower the threshold. Test failed — fix it, don't disable it.

E2E, load and benchmarks run locally only — setup and commands in [docs/testing.md](docs/testing.md).

## PR workflow

CI (`.github/workflows/ci.yml`, added in the skeleton sub-project): build (Release) → test with coverage → 85% gate. A PR isn't mergeable until CI is green. Before saying "done", check `gh pr checks <pr-number>`.

## CodeGraph

The repo is indexed by CodeGraph (`.codegraph/`). To find or understand code, use it FIRST, then grep/Read:

- MCP `codegraph_explore` — symbol source + call paths in one call.
- Shell: `codegraph explore "<symbol names or question>"`.
````

- [ ] **Step 2: Verify**

Run: `dotnet build -c Release` → `0 Error(s)`; `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total` → `total: 44, failed: 0`, exit 0.
Check every command in `docs/testing.md` matches the ones used in Tasks 2–4.

- [ ] **Step 3: Commit**

```bash
git add docs/testing.md CLAUDE.md
git commit -m "docs: testing layers and local commands"
```
