# Skeleton + Transport Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A runnable end-to-end skeleton — Chrome opens the Server through Cloudflare Access, sees "Connected", Ping shows round-trip time over the single envelope channel; CI green with an 85% line-coverage gate.

**Architecture:** ASP.NET Core Server hosts a Blazor WASM Client and exposes one SignalR hub (`/hub`) that routes `Envelope { Type, Payload, CorrelationId }` to `IEnvelopeHandler`s. The Client talks to it through `ITransport` (SignalR implementation). A middleware validates the Cloudflare Access JWT on every request; Kestrel binds to `127.0.0.1` only.

**Tech Stack:** .NET 10, ASP.NET Core + SignalR 10.0.12, Blazor WebAssembly, Microsoft.IdentityModel.JsonWebTokens 8.23.0, xunit.v3 4.0.1 on Microsoft.Testing.Platform, coverlet.MTP 10.1.0, StyleCop.Analyzers 1.2.0-beta.556, GitHub Actions (windows-latest).

**Spec:** [docs/superpowers/specs/2026-10-02-skeleton-transport-design.md](../specs/2026-10-02-skeleton-transport-design.md)

## Global Constraints

- Target framework `net10.0` everywhere; SDK pinned by `global.json` (`10.0.100`, `rollForward: latestFeature`).
- Package versions exactly as listed in this plan (latest stable at writing time).
- All files UTF-8 **without BOM**. Search with `rg`, never `find`.
- C# style: tabs, no `this.` prefix (StyleCop `SX1101` is an error), private fields `_camelCase`, usings sorted (`SA1210`). StyleCop errors fail the build — fix the code, never relax the ruleset (only `SA1412` is changed, in Task 1).
- Everything in English (code, comments, docs, commit messages). No Claude/AI attribution in commits.
- Kestrel listens on `127.0.0.1:<Server:Port>` (default `5180`) only.
- `CloudflareAccess:Enabled=false` honored only in `Development`; outside Development missing `TeamDomain`/`Audience` → Server refuses to start.
- Line coverage ≥ 85% (`**/Program.cs` and `**/*.razor` excluded).
- Test command used throughout: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build` (after `dotnet build -c Release`).

## File Structure

```
global.json                                         SDK pin + MTP test runner opt-in
Directory.Build.props                               StyleCop 1.2.0-beta.556 for all projects (modify)
StyleCop.ruleset                                    SA1412 -> None (modify)
coverlet.runsettings                                delete (VSTest-only)
AiChromeProxy.slnx
src/AiChromeProxy.Shared/
  Envelope.cs                                       wire record + Create<T>
  MessageTypes.cs                                   "ping" / "pong" / "error"
src/AiChromeProxy.Server/
  Program.cs                                        host wiring, loopback bind, fail-closed check
  appsettings.json / appsettings.Development.json   Server:Port, CloudflareAccess:*
  Transport/IEnvelopeHandler.cs                     handler contract
  Transport/EnvelopeRouter.cs                       Type -> handler, unknown_type error
  Transport/PingHandler.cs                          ping -> pong { serverTime }
  Transport/TransportHub.cs                         SignalR hub: Send(Envelope) -> Receive(Envelope)
  Security/CloudflareAccessOptions.cs               options + Validate(env)
  Security/CloudflareAccessTokenValidator.cs        JWKS fetch/cache + JWT validation
  Security/CloudflareAccessMiddleware.cs            401 without a valid token
src/AiChromeProxy.Client/
  Program.cs                                        DI: ITransport
  Transport/ITransport.cs                           TransportState + ITransport
  Transport/SignalRTransport.cs                     ITransport over HubConnection
  Pages/Home.razor                                  status + Ping
tests/AiChromeProxy.Tests/
  testconfig.json                                   coverlet.MTP settings
  FixedTimeProvider.cs                              controllable clock
  EnvelopeTests.cs
  Transport/EnvelopeRouterTests.cs
  Transport/PingHandlerTests.cs
  Transport/TransportHubTests.cs                    end-to-end over WebSocket
  Security/TestAccessIssuer.cs                      RSA key, JWKS, signed tokens, stub handler
  Security/CloudflareAccessMiddlewareTests.cs
  Security/CloudflareAccessOptionsTests.cs
.github/workflows/ci.yml
docs/setup/cloudflare.md
```

---

### Task 1: Tooling, solution scaffold, `Envelope`

**Files:**
- Create: `global.json`, `AiChromeProxy.slnx`, the four projects, `tests/AiChromeProxy.Tests/testconfig.json`, `src/AiChromeProxy.Shared/Envelope.cs`, `src/AiChromeProxy.Shared/MessageTypes.cs`, `tests/AiChromeProxy.Tests/EnvelopeTests.cs`
- Modify: `Directory.Build.props`, `StyleCop.ruleset`, `src/AiChromeProxy.Client/Program.cs` (template)
- Delete: `coverlet.runsettings`, template `Class1.cs`, `UnitTest1.cs`

**Interfaces:**
- Produces: `AiChromeProxy.Shared.Envelope(string Type, JsonElement Payload, string? CorrelationId = null)` with `static Envelope Create<T>(string type, T payload, string? correlationId = null)`; `AiChromeProxy.Shared.MessageTypes.Ping|Pong|Error`.

- [ ] **Step 1: Tooling files**

Create `global.json`:

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestFeature"
  },
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
```

Replace `Directory.Build.props`:

```xml
<Project>
<!-- StyleCop Analyzers configuration -->
  <PropertyGroup>
    <CodeAnalysisRuleSet>$(MSBuildThisFileDirectory)StyleCop.ruleset</CodeAnalysisRuleSet>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="StyleCop.Analyzers" Version="1.2.0-beta.556" PrivateAssets="all" />
    <AdditionalFiles Include="$(MSBuildThisFileDirectory)stylecop.json" Link="stylecop.json" />
  </ItemGroup>
</Project>
```

In `StyleCop.ruleset` change the `SA1412` line to `<Rule Id="SA1412" Action="None" />` (repo is UTF-8 without BOM). Delete `coverlet.runsettings`:

```bash
git rm coverlet.runsettings
```

- [ ] **Step 2: Scaffold projects**

```bash
dotnet new sln -n AiChromeProxy
dotnet new classlib -n AiChromeProxy.Shared -o src/AiChromeProxy.Shared -f net10.0
dotnet new blazorwasm --empty -n AiChromeProxy.Client -o src/AiChromeProxy.Client -f net10.0
dotnet new web -n AiChromeProxy.Server -o src/AiChromeProxy.Server -f net10.0
dotnet new xunit -n AiChromeProxy.Tests -o tests/AiChromeProxy.Tests -f net10.0
rm src/AiChromeProxy.Shared/Class1.cs tests/AiChromeProxy.Tests/UnitTest1.cs
dotnet sln add src/AiChromeProxy.Shared src/AiChromeProxy.Client src/AiChromeProxy.Server tests/AiChromeProxy.Tests
```

Expected: `AiChromeProxy.slnx` created (the .NET 10 default format).

- [ ] **Step 3: Project files and references**

Replace `src/AiChromeProxy.Client/AiChromeProxy.Client.csproj`:

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
    <ProjectReference Include="..\AiChromeProxy.Shared\AiChromeProxy.Shared.csproj" />
  </ItemGroup>

</Project>
```

Replace `src/AiChromeProxy.Server/AiChromeProxy.Server.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <ItemGroup>
    <ProjectReference Include="..\AiChromeProxy.Shared\AiChromeProxy.Shared.csproj" />
    <ProjectReference Include="..\AiChromeProxy.Client\AiChromeProxy.Client.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly.Server" Version="10.0.12" />
    <PackageReference Include="Microsoft.IdentityModel.JsonWebTokens" Version="8.23.0" />
  </ItemGroup>

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

</Project>
```

Replace `tests/AiChromeProxy.Tests/AiChromeProxy.Tests.csproj`:

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
    <ProjectReference Include="..\..\src\AiChromeProxy.Server\AiChromeProxy.Server.csproj" />
    <ProjectReference Include="..\..\src\AiChromeProxy.Shared\AiChromeProxy.Shared.csproj" />
    <ProjectReference Include="..\..\src\AiChromeProxy.Client\AiChromeProxy.Client.csproj" />
  </ItemGroup>

</Project>
```

Create `tests/AiChromeProxy.Tests/testconfig.json`:

```json
{
  "platformOptions": {
    "Coverlet": {
      "include": "[AiChromeProxy.*]*",
      "exclude": "[AiChromeProxy.Tests]*",
      "excludeByAttribute": "GeneratedCode,GeneratedCodeAttribute,ExcludeFromCodeCoverage,ExcludeFromCodeCoverageAttribute,CompilerGeneratedAttribute",
      "excludeByFile": "**/Program.cs,**/*.razor",
      "format": "cobertura",
      "excludeAssembliesWithoutSources": "MissingAll"
    }
  }
}
```

The template `src/AiChromeProxy.Client/Program.cs` violates `SA1210` (using order). Replace it for now (Task 5 extends it):

```csharp
using AiChromeProxy.Client;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

await builder.Build().RunAsync();
```

- [ ] **Step 4: Write the failing test**

Create `tests/AiChromeProxy.Tests/EnvelopeTests.cs`:

```csharp
using AiChromeProxy.Shared;

namespace AiChromeProxy.Tests;

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
}
```

- [ ] **Step 5: Run it to verify it fails**

Run: `dotnet build -c Release`
Expected: FAIL — `error CS0246: The type or namespace name 'Envelope' could not be found`.

- [ ] **Step 6: Implement**

Create `src/AiChromeProxy.Shared/Envelope.cs`:

```csharp
using System.Text.Json;

namespace AiChromeProxy.Shared;

/// <summary>Single wire message for every feature; routed by <see cref="Type"/>.</summary>
public sealed record Envelope(string Type, JsonElement Payload, string? CorrelationId = null)
{
	public static Envelope Create<T>(string type, T payload, string? correlationId = null) =>
		new(type, JsonSerializer.SerializeToElement(payload), correlationId);
}
```

Create `src/AiChromeProxy.Shared/MessageTypes.cs`:

```csharp
namespace AiChromeProxy.Shared;

public static class MessageTypes
{
	public const string Ping = "ping";
	public const string Pong = "pong";
	public const string Error = "error";
}
```

- [ ] **Step 7: Build and test**

Run: `dotnet build -c Release` → Expected: `0 Error(s)`.
Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build` → Expected: `total: 2, failed: 0`.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "chore: scaffold solution, MTP test runner, envelope contract"
```

---

### Task 2: Envelope router + ping handler

**Files:**
- Create: `src/AiChromeProxy.Server/Transport/IEnvelopeHandler.cs`, `EnvelopeRouter.cs`, `PingHandler.cs`
- Test: `tests/AiChromeProxy.Tests/FixedTimeProvider.cs`, `tests/AiChromeProxy.Tests/Transport/EnvelopeRouterTests.cs`, `tests/AiChromeProxy.Tests/Transport/PingHandlerTests.cs`

**Interfaces:**
- Consumes: `Envelope`, `MessageTypes` (Task 1).
- Produces: `IEnvelopeHandler { string Type { get; } Task<Envelope?> HandleAsync(Envelope request, CancellationToken ct); }`; `EnvelopeRouter(IEnumerable<IEnvelopeHandler>)` with `Task<Envelope?> RouteAsync(Envelope request, CancellationToken ct)` (duplicate `Type` → `InvalidOperationException`; unknown type → `error` envelope `{ code: "unknown_type", type }` with the request's `CorrelationId`); `PingHandler(TimeProvider)`; test helper `FixedTimeProvider(DateTimeOffset now)` with settable `Now`.

- [ ] **Step 1: Write the failing tests**

Create `tests/AiChromeProxy.Tests/FixedTimeProvider.cs`:

```csharp
namespace AiChromeProxy.Tests;

public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
	public DateTimeOffset Now { get; set; } = now;

	public override DateTimeOffset GetUtcNow() => Now;
}
```

Create `tests/AiChromeProxy.Tests/Transport/EnvelopeRouterTests.cs`:

```csharp
using AiChromeProxy.Server.Transport;
using AiChromeProxy.Shared;

namespace AiChromeProxy.Tests.Transport;

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

Create `tests/AiChromeProxy.Tests/Transport/PingHandlerTests.cs`:

```csharp
using AiChromeProxy.Server.Transport;
using AiChromeProxy.Shared;

namespace AiChromeProxy.Tests.Transport;

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

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet build -c Release`
Expected: FAIL — `CS0234`/`CS0246` for `AiChromeProxy.Server.Transport`, `EnvelopeRouter`, `IEnvelopeHandler`, `PingHandler`.

- [ ] **Step 3: Implement**

Create `src/AiChromeProxy.Server/Transport/IEnvelopeHandler.cs`:

```csharp
using AiChromeProxy.Shared;

namespace AiChromeProxy.Server.Transport;

public interface IEnvelopeHandler
{
	string Type { get; }

	Task<Envelope?> HandleAsync(Envelope request, CancellationToken ct);
}
```

Create `src/AiChromeProxy.Server/Transport/EnvelopeRouter.cs`:

```csharp
using AiChromeProxy.Shared;

namespace AiChromeProxy.Server.Transport;

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

Create `src/AiChromeProxy.Server/Transport/PingHandler.cs`:

```csharp
using AiChromeProxy.Shared;

namespace AiChromeProxy.Server.Transport;

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

- [ ] **Step 4: Build and test**

Run: `dotnet build -c Release` → `0 Error(s)`.
Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build` → Expected: `total: 6, failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(server): envelope router with ping handler"
```

---

### Task 3: Cloudflare Access JWT validation

**Files:**
- Create: `src/AiChromeProxy.Server/Security/CloudflareAccessOptions.cs`, `CloudflareAccessTokenValidator.cs`, `CloudflareAccessMiddleware.cs`
- Test: `tests/AiChromeProxy.Tests/Security/TestAccessIssuer.cs`, `CloudflareAccessMiddlewareTests.cs`, `CloudflareAccessOptionsTests.cs`

**Interfaces:**
- Consumes: `FixedTimeProvider` (Task 2).
- Produces:
  - `CloudflareAccessOptions { const string Section = "CloudflareAccess"; bool Enabled = true; string TeamDomain; string Audience; void Validate(IHostEnvironment env); }`
  - `CloudflareAccessTokenValidator(IHttpClientFactory, IOptions<CloudflareAccessOptions>, TimeProvider)` with `const string JwksHttpClient = "cf-access-jwks"` and `Task<bool> ValidateAsync(string token, CancellationToken ct)`; JWKS from `https://<TeamDomain>/cdn-cgi/access/certs`, refetched on unknown `kid` at most once per minute.
  - `CloudflareAccessMiddleware(RequestDelegate, IOptions<CloudflareAccessOptions>, CloudflareAccessTokenValidator)` with `HeaderName = "Cf-Access-Jwt-Assertion"`, `CookieName = "CF_Authorization"`.
  - Test helper `TestAccessIssuer(string keyId = "kid-1")`: `TeamDomain`, `Audience`, `Jwks()`, `Token(issuer, audience, expires, notBefore)`, `Handler()` → `JwksHandler` (`HttpMessageHandler` with `Requests` counter).

- [ ] **Step 1: Write the failing tests**

Create `tests/AiChromeProxy.Tests/Security/TestAccessIssuer.cs`:

```csharp
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AiChromeProxy.Tests.Security;

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
		DateTime? notBefore = null)
	{
		var exp = expires ?? DateTime.UtcNow.AddMinutes(10);
		return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
		{
			Issuer = issuer,
			Audience = audience,
			NotBefore = notBefore ?? exp.AddMinutes(-20),
			IssuedAt = notBefore ?? exp.AddMinutes(-20),
			Expires = exp,
			Claims = new Dictionary<string, object> { ["email"] = "user@example.com" },
			SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
		});
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

Create `tests/AiChromeProxy.Tests/Security/CloudflareAccessMiddlewareTests.cs`:

```csharp
using AiChromeProxy.Server.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Tests.Security;

public sealed class CloudflareAccessMiddlewareTests
{
	private readonly TestAccessIssuer _issuer = new();

	[Fact]
	public async Task ValidHeaderToken_Passes()
	{
		var status = await RunAsync(ctx => ctx.Request.Headers[CloudflareAccessMiddleware.HeaderName] = _issuer.Token());

		Assert.Equal(StatusCodes.Status200OK, status);
	}

	[Fact]
	public async Task ValidCookieToken_Passes()
	{
		var status = await RunAsync(ctx => ctx.Request.Headers.Cookie = $"{CloudflareAccessMiddleware.CookieName}={_issuer.Token()}");

		Assert.Equal(StatusCodes.Status200OK, status);
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
	public async Task Disabled_PassesWithoutToken()
	{
		var (validator, options) = Build(_issuer.Handler(), TimeProvider.System, enabled: false);
		var ctx = new DefaultHttpContext();
		var middleware = new CloudflareAccessMiddleware(Ok, options, validator);

		await middleware.InvokeAsync(ctx);

		Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
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
		context.Response.StatusCode = StatusCodes.Status200OK;
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
}
```

Create `tests/AiChromeProxy.Tests/Security/CloudflareAccessOptionsTests.cs`:

```csharp
using AiChromeProxy.Server.Security;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AiChromeProxy.Tests.Security;

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

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet build -c Release`
Expected: FAIL — `CS0234` for namespace `AiChromeProxy.Server.Security`.

- [ ] **Step 3: Implement**

Create `src/AiChromeProxy.Server/Security/CloudflareAccessOptions.cs`:

```csharp
namespace AiChromeProxy.Server.Security;

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

Create `src/AiChromeProxy.Server/Security/CloudflareAccessTokenValidator.cs`:

```csharp
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AiChromeProxy.Server.Security;

/// <summary>Validates Cloudflare Access JWTs against the team JWKS.</summary>
public sealed class CloudflareAccessTokenValidator(
	IHttpClientFactory httpFactory,
	IOptions<CloudflareAccessOptions> options,
	TimeProvider time)
{
	public const string JwksHttpClient = "cf-access-jwks";

	private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(1);

	private readonly JsonWebTokenHandler _handler = new();
	private readonly SemaphoreSlim _refreshLock = new(1, 1);
	private IList<SecurityKey> _keys = [];
	private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

	public async Task<bool> ValidateAsync(string token, CancellationToken ct)
	{
		if (_keys.Count == 0)
		{
			await RefreshKeysAsync(force: true, ct);
		}

		var result = await _handler.ValidateTokenAsync(token, Parameters());
		if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException && await RefreshKeysAsync(force: false, ct))
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
	private async Task<bool> RefreshKeysAsync(bool force, CancellationToken ct)
	{
		await _refreshLock.WaitAsync(ct);
		try
		{
			var now = time.GetUtcNow();
			if (!force && now - _lastRefresh < MinRefreshInterval)
			{
				return false;
			}

			var url = $"https://{options.Value.TeamDomain}/cdn-cgi/access/certs";
			var json = await httpFactory.CreateClient(JwksHttpClient).GetStringAsync(url, ct);
			_keys = new JsonWebKeySet(json).GetSigningKeys();
			_lastRefresh = now;
			return true;
		}
		finally
		{
			_refreshLock.Release();
		}
	}
}
```

Create `src/AiChromeProxy.Server/Security/CloudflareAccessMiddleware.cs`:

```csharp
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

- [ ] **Step 4: Build and test**

Run: `dotnet build -c Release` → `0 Error(s)`.
Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build` → Expected: `total: 21, failed: 0` (6 earlier + 9 middleware/validator + 6 options).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(server): validate Cloudflare Access JWT on every request"
```

---

### Task 4: Hub, server wiring, client transport — end-to-end

**Files:**
- Create: `src/AiChromeProxy.Server/Transport/TransportHub.cs`, `src/AiChromeProxy.Client/Transport/ITransport.cs`, `src/AiChromeProxy.Client/Transport/SignalRTransport.cs`
- Modify: `src/AiChromeProxy.Server/Program.cs`, `src/AiChromeProxy.Server/appsettings.json`, `src/AiChromeProxy.Server/appsettings.Development.json`
- Test: `tests/AiChromeProxy.Tests/Transport/TransportHubTests.cs`

**Interfaces:**
- Consumes: `EnvelopeRouter`, `IEnvelopeHandler`, `PingHandler` (Task 2); `CloudflareAccessOptions`, `CloudflareAccessTokenValidator`, `CloudflareAccessMiddleware` (Task 3); `TestAccessIssuer` (Task 3).
- Produces:
  - `TransportHub(EnvelopeRouter)` at `TransportHub.Path = "/hub"`, method `Send(Envelope)`, replies via client method `TransportHub.ReceiveMethod = "Receive"`.
  - `public partial class Program` (for `WebApplicationFactory<Program>`).
  - `enum TransportState { Disconnected, Connecting, Connected, Reconnecting }`; `ITransport : IAsyncDisposable { event Action<TransportState>? StateChanged; event Action<Envelope>? Received; TransportState State { get; } Task ConnectAsync(CancellationToken ct = default); Task SendAsync(Envelope envelope, CancellationToken ct = default); }`
  - `SignalRTransport(HubConnection connection) : ITransport` (`SendMethod = "Send"`, `ReceiveMethod = "Receive"`).

- [ ] **Step 1: Write the failing test**

Create `tests/AiChromeProxy.Tests/Transport/TransportHubTests.cs`:

```csharp
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Server.Security;
using AiChromeProxy.Server.Transport;
using AiChromeProxy.Shared;
using AiChromeProxy.Tests.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiChromeProxy.Tests.Transport;

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

		await Assert.ThrowsAnyAsync<Exception>(() => transport.ConnectAsync(TestContext.Current.CancellationToken));
		Assert.Equal(TransportState.Disconnected, transport.State);
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

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet build -c Release`
Expected: FAIL — `CS0246`/`CS0234` for `SignalRTransport`, `TransportState`, `TransportHub`, `Program` (no `public partial class Program`).

- [ ] **Step 3: Client transport**

Create `src/AiChromeProxy.Client/Transport/ITransport.cs`:

```csharp
using AiChromeProxy.Shared;

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

Create `src/AiChromeProxy.Client/Transport/SignalRTransport.cs`:

```csharp
using AiChromeProxy.Shared;
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

- [ ] **Step 4: Hub and server wiring**

Create `src/AiChromeProxy.Server/Transport/TransportHub.cs`:

```csharp
using AiChromeProxy.Shared;
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

Replace `src/AiChromeProxy.Server/Program.cs`:

```csharp
using System.Net;
using AiChromeProxy.Server.Security;
using AiChromeProxy.Server.Transport;

var builder = WebApplication.CreateBuilder(args);

var port = builder.Configuration.GetValue("Server:Port", 5180);
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));

builder.Services.Configure<CloudflareAccessOptions>(builder.Configuration.GetSection(CloudflareAccessOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient(CloudflareAccessTokenValidator.JwksHttpClient);
builder.Services.AddSingleton<CloudflareAccessTokenValidator>();
builder.Services.AddSingleton<IEnvelopeHandler, PingHandler>();
builder.Services.AddSingleton<EnvelopeRouter>();
builder.Services.AddSignalR();

var app = builder.Build();

var access = new CloudflareAccessOptions();
app.Configuration.GetSection(CloudflareAccessOptions.Section).Bind(access);
access.Validate(app.Environment);

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

Replace `src/AiChromeProxy.Server/appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Server": {
    "Port": 5180
  },
  "CloudflareAccess": {
    "Enabled": true,
    "TeamDomain": "",
    "Audience": ""
  }
}
```

Replace `src/AiChromeProxy.Server/appsettings.Development.json`:

```json
{
  "CloudflareAccess": {
    "Enabled": false
  }
}
```

- [ ] **Step 5: Build and test with the coverage gate**

Run: `dotnet build -c Release` → `0 Error(s)`.
Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total`
Expected: `total: 24, failed: 0`, exit code 0. (Sanity check once: the same command with `--coverlet-threshold 101` must exit with code 5 — proves the gate is live.)

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: transport hub and SignalR client transport, end-to-end ping"
```

---

### Task 5: Client UI — status and Ping

**Files:**
- Modify: `src/AiChromeProxy.Client/Program.cs`, `src/AiChromeProxy.Client/Pages/Home.razor`

**Interfaces:**
- Consumes: `ITransport`, `SignalRTransport`, `TransportState` (Task 4); `Envelope`, `MessageTypes` (Task 1).

UI is excluded from coverage (`**/*.razor`, `**/Program.cs`); it is verified manually in Chrome.

- [ ] **Step 1: Register the transport**

Replace `src/AiChromeProxy.Client/Program.cs`:

```csharp
using AiChromeProxy.Client;
using AiChromeProxy.Client.Transport;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.SignalR.Client;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var hubUrl = new Uri(new Uri(builder.HostEnvironment.BaseAddress), "hub");
builder.Services.AddSingleton<ITransport>(_ => new SignalRTransport(
	new HubConnectionBuilder().WithUrl(hubUrl).WithAutomaticReconnect().Build()));

await builder.Build().RunAsync();
```

- [ ] **Step 2: Status page**

Replace `src/AiChromeProxy.Client/Pages/Home.razor`:

```razor
@page "/"
@using System.Diagnostics
@using AiChromeProxy.Client.Transport
@using AiChromeProxy.Shared
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

- [ ] **Step 3: Build and test**

Run: `dotnet build -c Release` → `0 Error(s)`.
Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total` → `total: 24, failed: 0`.

- [ ] **Step 4: Manual check in Chrome**

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/AiChromeProxy.Server --no-launch-profile
```

Open `http://127.0.0.1:5180/`. Expected: "Connection: **Connected**"; clicking **Ping** shows `Pong in <n> ms, server time <ISO-8601>`. Also verify the bind: `netstat -ano | rg ':5180'` shows only `127.0.0.1:5180 ... LISTENING`. Stop the server afterwards.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(client): connection status page with ping"
```

---

### Task 6: CI and Cloudflare setup guide

**Files:**
- Create: `.github/workflows/ci.yml`, `docs/setup/cloudflare.md`
- Modify: `README.md`

- [ ] **Step 1: CI workflow**

Create `.github/workflows/ci.yml`:

```yaml
name: ci

on:
  push:
  pull_request:

jobs:
  build-test:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v5

      - uses: actions/setup-dotnet@v5
        with:
          global-json-file: global.json

      - name: Restore
        run: dotnet restore

      - name: Build (StyleCop errors fail the build)
        run: dotnet build -c Release --no-restore

      - name: Test + coverage gate (line >= 85%)
        run: dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total
```

- [ ] **Step 2: Setup guide**

Create `docs/setup/cloudflare.md` with exactly this content:

````markdown
# Cloudflare Tunnel + Access setup

The Server listens on `127.0.0.1` only. The only way in from outside is a Cloudflare Tunnel, and every request must carry a valid Cloudflare Access token — the Server checks it itself and answers `401` otherwise.

## 1. Install cloudflared (home server, Windows)

```powershell
winget install --id Cloudflare.cloudflared
```

## 2. Create the tunnel

In the Cloudflare dashboard: **Zero Trust → Networks → Tunnels → Create a tunnel** (type *Cloudflared*). Copy the install command it shows and run it in an elevated PowerShell — it installs `cloudflared` as a Windows service, so the tunnel is up after a reboot without anyone logging in.

Add a **public hostname**, e.g. `code.example.com` → service `http://127.0.0.1:5180`.

## 3. Protect it with Access

**Zero Trust → Access → Applications → Add an application → Self-hosted**:

- Application domain: the hostname from step 2.
- Policy: *Allow*, include your email address(es).

Open the application and copy its **Application Audience (AUD) tag**. Your team domain is shown under **Settings → Custom pages** (`<team>.cloudflareaccess.com`).

## 4. Configure the Server

Set two persistent user environment variables (PowerShell):

```powershell
[Environment]::SetEnvironmentVariable("CloudflareAccess__TeamDomain", "<team>.cloudflareaccess.com", "User")
[Environment]::SetEnvironmentVariable("CloudflareAccess__Audience", "<AUD tag>", "User")
```

Open a new terminal afterwards so the variables are visible. (The Windows host installer will manage this configuration later.)

Outside `Development` the Server refuses to start if either value is missing.

## 5. Run and verify

```powershell
dotnet run --project src/AiChromeProxy.Server -c Release --no-launch-profile
```

1. Open `https://code.example.com` in Chrome on another machine → Access login → page shows **Connected**; **Ping** shows the round-trip time.
2. On the home server, a request without a token is rejected:

   ```powershell
   curl.exe -i http://127.0.0.1:5180/   # HTTP/1.1 401 Unauthorized
   ```
````

- [ ] **Step 3: README**

In `README.md` replace the `**Status:** ...` line with:

````markdown
**Status:** skeleton — transport + Cloudflare Access. See [architecture](docs/superpowers/specs/2026-10-02-architecture-design.md) and [setup](docs/setup/cloudflare.md).

## Run locally (no Cloudflare)

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"   # disables the Access check
dotnet run --project src/AiChromeProxy.Server --no-launch-profile
# open http://127.0.0.1:5180/
```
````

- [ ] **Step 4: Verify the CI commands locally**

Run, in order: `dotnet restore`, `dotnet build -c Release --no-restore`, `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total`.
Expected: all succeed; `total: 24, failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "ci: build, test and coverage gate; docs: Cloudflare setup"
```

- [ ] **Step 6: Push and confirm CI**

Ask the user before pushing (no remote may be configured yet). Once pushed: `gh run list --limit 1` → conclusion `success`.
