# Windows Host (2a) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One `Setup.exe` installs the Server (as a Windows Service under the user's account, auto-start without login, CLEF file logs, persistent config) and an Avalonia tray (status, start/stop/restart, settings, logs, elevated install/uninstall, one-click Velopack updates); tag `v*` publishes the release.

**Architecture:** Shared hosting types (`ServerOptions`, `HostName`, `DataDirectory`) in Infrastructure; Server gains `UseWindowsService`, Serilog CLEF logs and a data-directory config source used only as a service or with `AICP_DATA_DIR`, plus the skeleton-review follow-ups (AllowedHosts, JWKS TTL, monotonic throttle, host-name validation). The tray (`net10.0-windows`) depends only on Domain and Infrastructure; Windows interop sits behind `IServiceControl`; update flow in a tested orchestrator; Velopack hooks start/stop/remove the service.

**Tech Stack:** .NET 10; Avalonia 12.1.3 (+ Desktop, Themes.Fluent, Headless for tests); CommunityToolkit.Mvvm 8.4.2; System.ServiceProcess.ServiceController 10.0.12; Microsoft.Extensions.Hosting.WindowsServices 10.0.12; Serilog.AspNetCore 10.0.0; Velopack 1.2.161 + `vpk` 1.2.161; Microsoft.Extensions.TimeProvider.Testing 10.10.0.

**Spec:** [docs/superpowers/specs/2026-10-02-windows-host-design.md](../specs/2026-10-02-windows-host-design.md) (incl. "Changes adopted from the prototype").

## Global Constraints

- Package versions exactly as listed in the files below; `vpk` version must equal the Velopack package version (1.2.161).
- Dependency direction: Domain ← Application ← Infrastructure ← Server; Client → Domain only; **Tray → Domain and Infrastructure only** (architecture test).
- Never name a source folder `Logs`, `Log`, `Release`, `Debug`, `bin` or `obj` (the `.gitignore` drops them silently); before committing, check `git status --short` lists every new file.
- **Safety while implementing:** do not install/remove real Windows services, grant LSA rights, change service DACLs, write HKCU/HKLM, trigger UAC, or open GUI windows on the desktop. Windows interop code is compiled and unit-tested through fakes; real behavior is covered by the manual checklist in `docs/windows-host.md`. Use `AICP_DATA_DIR` (never the real `%ProgramData%`) for any local run.
- All files UTF-8 without BOM; tabs in C#; no `this.`; private fields `_camelCase`; sorted usings; StyleCop errors fail the build — fix code, never the ruleset.
- English only. No Claude/AI attribution in commits.
- Gate (after `dotnet build -c Release`): `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total` — line coverage ≥ 85%.

---

### Task 1: feat(infra): host-name validation, ServerOptions, DataDirectory

**Files:**
- Create: `src/AiChromeProxy.Infrastructure/Hosting/DataDirectory.cs`, `src/AiChromeProxy.Infrastructure/Hosting/HostName.cs`, `src/AiChromeProxy.Infrastructure/Hosting/ServerOptions.cs`, `tests/AiChromeProxy.Tests/Infrastructure/HostingOptionsTests.cs`, `tests/AiChromeProxy.Tests/TestHostEnvironment.cs`
- Modify: `src/AiChromeProxy.Infrastructure/Security/CloudflareAccessOptions.cs`, `tests/AiChromeProxy.Tests/Infrastructure/CloudflareAccessOptionsTests.cs`

Every code block below is the **complete final content** of that file (verified on the prototype: builds with 0 warnings, gate passes). Write it exactly; for modified files replace the whole file.

- [ ] **Step 1: Write the tests**

Write `tests/AiChromeProxy.Tests/Infrastructure/CloudflareAccessOptionsTests.cs`:

```csharp
using AiChromeProxy.Infrastructure.Security;
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

		var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(new TestHostEnvironment(Environments.Production)));

		Assert.Contains("TeamDomain", ex.Message);
	}

	[Fact]
	public void Production_Disabled_Throws()
	{
		var options = new CloudflareAccessOptions { Enabled = false };

		var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(new TestHostEnvironment(Environments.Production)));

		Assert.Contains("Development", ex.Message);
	}

	[Theory]
	[InlineData("https://team.cloudflareaccess.com")]
	[InlineData("team.cloudflareaccess.com/")]
	[InlineData("team.cloudflareaccess.com:443")]
	public void Enabled_TeamDomainNotBareHost_Throws(string team)
	{
		var options = new CloudflareAccessOptions { TeamDomain = team, Audience = "aud" };

		var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(new TestHostEnvironment(Environments.Development)));

		Assert.Contains("bare host name", ex.Message);
	}

	[Fact]
	public void Development_Disabled_Ok()
	{
		new CloudflareAccessOptions { Enabled = false }.Validate(new TestHostEnvironment(Environments.Development));
	}

	[Fact]
	public void Production_FullConfig_Ok()
	{
		new CloudflareAccessOptions { TeamDomain = "t.cloudflareaccess.com", Audience = "a" }.Validate(new TestHostEnvironment(Environments.Production));
	}
}
```

Write `tests/AiChromeProxy.Tests/Infrastructure/HostingOptionsTests.cs`:

```csharp
using AiChromeProxy.Infrastructure.Hosting;

namespace AiChromeProxy.Tests.Infrastructure;

public sealed class HostingOptionsTests
{
	[Theory]
	[InlineData("code.example.com")]
	[InlineData("team.cloudflareaccess.com")]
	[InlineData("localhost")]
	public void HostName_Bare_Valid(string value)
	{
		Assert.True(HostName.IsValid(value));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData(" ")]
	[InlineData("https://team.cloudflareaccess.com")]
	[InlineData("team.cloudflareaccess.com/")]
	[InlineData("team.cloudflareaccess.com/path")]
	[InlineData("team.cloudflareaccess.com:443")]
	[InlineData("team.cloudflareaccess.com.")]
	[InlineData("127.0.0.1")]
	[InlineData("code example.com")]
	public void HostName_NotBare_Invalid(string? value)
	{
		Assert.False(HostName.IsValid(value));
	}

	[Fact]
	public void ServerOptions_Production_EmptyPublicHost_Error()
	{
		var error = new ServerOptions().GetError(isDevelopment: false);

		Assert.Contains("Server:PublicHost must be set", error);
	}

	[Fact]
	public void ServerOptions_Development_EmptyPublicHost_Ok()
	{
		Assert.Null(new ServerOptions().GetError(isDevelopment: true));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void ServerOptions_InvalidPublicHost_ErrorInEveryEnvironment(bool isDevelopment)
	{
		var error = new ServerOptions { PublicHost = "https://code.example.com/" }.GetError(isDevelopment);

		Assert.Contains("bare host name", error);
	}

	[Fact]
	public void ServerOptions_Validate_ThrowsWithMessage()
	{
		var ex = Assert.Throws<InvalidOperationException>(() => new ServerOptions().Validate(new TestHostEnvironment("Production")));

		Assert.Contains("Server:PublicHost", ex.Message);
	}

	[Fact]
	public void ServerOptions_Validate_ValidHost_Ok()
	{
		new ServerOptions { PublicHost = "code.example.com" }.Validate(new TestHostEnvironment("Production"));
	}

	[Fact]
	public void AllowedHosts_PublicHostPlusLoopback()
	{
		Assert.Equal(["code.example.com", "127.0.0.1", "localhost"], new ServerOptions { PublicHost = "code.example.com" }.AllowedHosts());
	}

	[Fact]
	public void AllowedHosts_NoPublicHost_LoopbackOnly_NeverWildcard()
	{
		Assert.Equal(["127.0.0.1", "localhost"], new ServerOptions().AllowedHosts());
	}

	[Fact]
	public void DataDirectory_DefaultsToProgramData()
	{
		var dir = DataDirectory.Resolve(null);

		Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AiChromeProxy"), dir.Root);
	}

	[Fact]
	public void DataDirectory_Override_UsedWithFixedLayout()
	{
		var dir = DataDirectory.Resolve(@"D:\data");

		Assert.Equal(@"D:\data", dir.Root);
		Assert.Equal(@"D:\data\appsettings.json", dir.SettingsFile);
		Assert.Equal(@"D:\data\logs", dir.Logs);
	}
}
```

Write `tests/AiChromeProxy.Tests/TestHostEnvironment.cs`:

```csharp
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AiChromeProxy.Tests;

public sealed class TestHostEnvironment(string name) : IHostEnvironment
{
	public string EnvironmentName { get; set; } = name;

	public string ApplicationName { get; set; } = "test";

	public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

	public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
```

- [ ] **Step 2: Run to verify they fail (RED)**

Run: `dotnet build -c Release`
Expected: FAIL — compile errors for the types/members this task introduces (or, where the tests only extend existing types, failing assertions in the next step). Record the first errors in your report.

- [ ] **Step 3: Implement**

Write `src/AiChromeProxy.Infrastructure/Hosting/DataDirectory.cs`:

```csharp
namespace AiChromeProxy.Infrastructure.Hosting;

/// <summary>Machine-wide folder shared by the service and the tray: persistent settings and CLEF logs.</summary>
public sealed record DataDirectory(string Root)
{
	/// <summary>Environment variable overriding the default <c>%ProgramData%\AiChromeProxy</c> (tests, local runs).</summary>
	public const string OverrideVariable = "AICP_DATA_DIR";

	public string SettingsFile => Path.Combine(Root, "appsettings.json");

	public string Logs => Path.Combine(Root, "logs");

	public static DataDirectory Resolve(string? overrideValue) => new(string.IsNullOrWhiteSpace(overrideValue)
		? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AiChromeProxy")
		: overrideValue);

	public static DataDirectory FromEnvironment() => Resolve(Environment.GetEnvironmentVariable(OverrideVariable));
}
```

Write `src/AiChromeProxy.Infrastructure/Hosting/HostName.cs`:

```csharp
namespace AiChromeProxy.Infrastructure.Hosting;

/// <summary>Bare DNS host name check shared by the Server's startup validation and the tray's settings form.</summary>
public static class HostName
{
	/// <returns>True for <c>code.example.com</c>; false for a scheme, path, port, trailing dot/slash, IP address or blank.</returns>
	public static bool IsValid(string? value) =>
		!string.IsNullOrEmpty(value) && !value.EndsWith('.') && Uri.CheckHostName(value) == UriHostNameType.Dns;
}
```

Write `src/AiChromeProxy.Infrastructure/Hosting/ServerOptions.cs`:

```csharp
using Microsoft.Extensions.Hosting;

namespace AiChromeProxy.Infrastructure.Hosting;

/// <summary>Section <c>Server</c>: where the Server listens and which public host name it answers to.</summary>
public sealed class ServerOptions
{
	public const string Section = "Server";
	public const int DefaultPort = 5180;

	public int Port { get; set; } = DefaultPort;

	/// <summary>Public host name of the tunnel (e.g. <c>code.example.com</c>); the only non-loopback <c>Host</c> header accepted.</summary>
	public string PublicHost { get; set; } = string.Empty;

	/// <summary>Host names for ASP.NET Core host filtering: the public host plus the loopback names (DNS-rebinding hardening).</summary>
	public IReadOnlyList<string> AllowedHosts() =>
		string.IsNullOrWhiteSpace(PublicHost) ? ["127.0.0.1", "localhost"] : [PublicHost, "127.0.0.1", "localhost"];

	/// <returns>Null when valid; otherwise the message the Server fails with and the tray shows.</returns>
	public string? GetError(bool isDevelopment)
	{
		if (string.IsNullOrWhiteSpace(PublicHost))
		{
			return isDevelopment ? null : "Server:PublicHost must be set outside Development (the tunnel's public host name, e.g. code.example.com).";
		}

		return HostName.IsValid(PublicHost)
			? null
			: $"Server:PublicHost must be a bare host name like code.example.com (no scheme, path, port or trailing slash); got '{PublicHost}'.";
	}

	/// <summary>Fail closed: outside Development the public host must be configured.</summary>
	public void Validate(IHostEnvironment env)
	{
		if (GetError(env.IsDevelopment()) is { } error)
		{
			throw new InvalidOperationException(error);
		}
	}
}
```

Write `src/AiChromeProxy.Infrastructure/Security/CloudflareAccessOptions.cs`:

```csharp
using AiChromeProxy.Infrastructure.Hosting;
using Microsoft.Extensions.Hosting;

namespace AiChromeProxy.Infrastructure.Security;

public sealed class CloudflareAccessOptions
{
	public const string Section = "CloudflareAccess";

	public bool Enabled { get; set; } = true;

	public string TeamDomain { get; set; } = string.Empty;

	public string Audience { get; set; } = string.Empty;

	/// <returns>Null when valid; otherwise the message the Server fails with and the tray shows.</returns>
	public string? GetError(bool isDevelopment)
	{
		if (!Enabled)
		{
			return isDevelopment ? null : "CloudflareAccess:Enabled=false is allowed only in Development.";
		}

		if (string.IsNullOrWhiteSpace(TeamDomain) || string.IsNullOrWhiteSpace(Audience))
		{
			return "CloudflareAccess:TeamDomain and CloudflareAccess:Audience must be set.";
		}

		return HostName.IsValid(TeamDomain)
			? null
			: $"CloudflareAccess:TeamDomain must be a bare host name like team.cloudflareaccess.com (no scheme, path, port or trailing slash); got '{TeamDomain}'.";
	}

	/// <summary>Fail closed: outside Development the check must be on and fully configured.</summary>
	public void Validate(IHostEnvironment env)
	{
		if (GetError(env.IsDevelopment()) is { } error)
		{
			throw new InvalidOperationException(error);
		}
	}
}
```

- [ ] **Step 4: Build and run the gate (GREEN)**

Run: `dotnet build -c Release` → Expected: `0 Warning(s)`, `0 Error(s)`.
Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total` then `echo $?` → Expected: `total: 73, failed: 0`, exit code 0.

- [ ] **Step 5: Commit**

```bash
git add -A
git status --short   # every file listed above must appear; nothing under a Logs/ folder
git commit -m "feat(infra): host-name validation, ServerOptions, DataDirectory"
```

---

### Task 2: feat(infra): JWKS 6 h key TTL and monotonic refresh throttle

**Files:**
- Modify: `src/AiChromeProxy.Infrastructure/Security/CloudflareAccessTokenValidator.cs`, `tests/AiChromeProxy.Tests/FixedTimeProvider.cs`, `tests/AiChromeProxy.Tests/Server/CloudflareAccessMiddlewareTests.cs`

Every code block below is the **complete final content** of that file (verified on the prototype: builds with 0 warnings, gate passes). Write it exactly; for modified files replace the whole file.

- [ ] **Step 1: Write the tests**

Write `tests/AiChromeProxy.Tests/FixedTimeProvider.cs`:

```csharp
namespace AiChromeProxy.Tests;

/// <summary>Test clock: wall clock (<see cref="Now"/>) and monotonic clock (<see cref="Elapsed"/>) move independently.</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
	public DateTimeOffset Now { get; set; } = now;

	/// <summary>Monotonic reading behind <see cref="GetTimestamp"/>; unaffected by setting <see cref="Now"/> (a wall-clock jump).</summary>
	public TimeSpan Elapsed { get; set; }

	public override long TimestampFrequency => TimeSpan.TicksPerSecond;

	public override DateTimeOffset GetUtcNow() => Now;

	public override long GetTimestamp() => Elapsed.Ticks;

	/// <summary>Real time passing: both clocks move.</summary>
	public void Advance(TimeSpan by)
	{
		Now += by;
		Elapsed += by;
	}
}
```

Write `tests/AiChromeProxy.Tests/Server/CloudflareAccessMiddlewareTests.cs`:

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

		time.Advance(TimeSpan.FromMinutes(2));
		Assert.False(await validator.ValidateAsync(stranger.Token(), ct));
		Assert.Equal(2, jwks.Requests);
	}

	[Fact]
	public async Task KnownKid_KeysOlderThanSixHours_Refetched_RetiredKeyRejected()
	{
		var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
		var jwks = new StubHandler(HttpStatusCode.OK, _issuer.Jwks());
		var (validator, _) = Build(jwks, time, enabled: true);
		var ct = TestContext.Current.CancellationToken;

		Assert.True(await validator.ValidateAsync(Token(time), ct));
		jwks.Body = new TestAccessIssuer("kid-1").Jwks();

		time.Advance(TimeSpan.FromHours(6) - TimeSpan.FromSeconds(1));
		Assert.True(await validator.ValidateAsync(Token(time), ct));
		Assert.Equal(1, jwks.Requests);

		time.Advance(TimeSpan.FromSeconds(1));
		Assert.False(await validator.ValidateAsync(Token(time), ct));
		Assert.Equal(2, jwks.Requests);
	}

	[Fact]
	public async Task WallClockJumpsBack_ThrottleUsesMonotonicTime_RefetchStillHappens()
	{
		var stranger = new TestAccessIssuer("kid-unknown");
		var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
		var jwks = _issuer.Handler();
		var (validator, _) = Build(jwks, time, enabled: true);
		var ct = TestContext.Current.CancellationToken;

		Assert.False(await validator.ValidateAsync(stranger.Token(), ct));
		time.Now -= TimeSpan.FromDays(1);
		time.Elapsed += TimeSpan.FromMinutes(2);
		Assert.False(await validator.ValidateAsync(stranger.Token(expires: time.Now.UtcDateTime.AddMinutes(10)), ct));

		Assert.Equal(2, jwks.Requests);
	}

	[Fact]
	public async Task WallClockJumpsForward_DoesNotBypassThrottle()
	{
		var stranger = new TestAccessIssuer("kid-unknown");
		var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
		var jwks = _issuer.Handler();
		var (validator, _) = Build(jwks, time, enabled: true);
		var ct = TestContext.Current.CancellationToken;

		Assert.False(await validator.ValidateAsync(stranger.Token(), ct));
		time.Now += TimeSpan.FromMinutes(5);
		Assert.False(await validator.ValidateAsync(stranger.Token(expires: time.Now.UtcDateTime.AddMinutes(10)), ct));

		Assert.Equal(1, jwks.Requests);
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

		time.Advance(TimeSpan.FromMinutes(2));
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

	/// <summary>Token valid at the fake clock's current time.</summary>
	private string Token(FixedTimeProvider time) => _issuer.Token(expires: time.Now.UtcDateTime.AddMinutes(10));

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

		public string Body { get; set; } = body;

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests++;
			return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(Body) });
		}
	}
}
```

- [ ] **Step 2: Run to verify they fail (RED)**

Run: `dotnet build -c Release`
Expected: FAIL — compile errors for the types/members this task introduces (or, where the tests only extend existing types, failing assertions in the next step). Record the first errors in your report.

- [ ] **Step 3: Implement**

Write `src/AiChromeProxy.Infrastructure/Security/CloudflareAccessTokenValidator.cs`:

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
	private static readonly TimeSpan KeysTtl = TimeSpan.FromHours(6);
	private static readonly TimeSpan JwksTimeout = TimeSpan.FromSeconds(10);

	private readonly JsonWebTokenHandler _handler = new();
	private readonly SemaphoreSlim _refreshLock = new(1, 1);
	private IList<SecurityKey> _keys = [];

	// Monotonic timestamps (TimeProvider.GetTimestamp): wall-clock jumps must not stall or skip a refresh.
	private long? _lastFetch;
	private long _keysLoaded;

	public async Task<bool> ValidateAsync(string token, CancellationToken ct)
	{
		if (_keys.Count == 0 || time.GetElapsedTime(_keysLoaded) >= KeysTtl)
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
			if (_lastFetch is { } last && time.GetElapsedTime(last) < MinRefreshInterval)
			{
				return false;
			}

			_lastFetch = time.GetTimestamp();
			var url = $"https://{options.Value.TeamDomain}/cdn-cgi/access/certs";
			var client = httpFactory.CreateClient(JwksHttpClient);
			client.Timeout = JwksTimeout;
			// Not the caller's token: _lastFetch is already set, so a cancelled fetch would leave keys empty for a minute.
			var json = await client.GetStringAsync(url, CancellationToken.None);
			_keys = new JsonWebKeySet(json).GetSigningKeys();
			_keysLoaded = time.GetTimestamp();
			return true;
		}
		finally
		{
			_refreshLock.Release();
		}
	}
}
```

- [ ] **Step 4: Build and run the gate (GREEN)**

Run: `dotnet build -c Release` → Expected: `0 Warning(s)`, `0 Error(s)`.
Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total` then `echo $?` → Expected: `total: 76, failed: 0`, exit code 0.

- [ ] **Step 5: Commit**

```bash
git add -A
git status --short   # every file listed above must appear; nothing under a Logs/ folder
git commit -m "feat(infra): JWKS 6 h key TTL and monotonic refresh throttle"
```

---

### Task 3: feat(server): AllowedHosts from Server:PublicHost, fail closed outside Development

**Files:**
- Create: `tests/AiChromeProxy.Tests/Server/ServerHostingTests.cs`
- Modify: `docs/setup/cloudflare.md`, `src/AiChromeProxy.Server/Program.cs`, `src/AiChromeProxy.Server/appsettings.json`, `tests/AiChromeProxy.Tests/Server/TransportHubTests.cs`

Every code block below is the **complete final content** of that file (verified on the prototype: builds with 0 warnings, gate passes). Write it exactly; for modified files replace the whole file.

- [ ] **Step 1: Write the tests**

Write `tests/AiChromeProxy.Tests/Server/ServerHostingTests.cs`:

```csharp
using System.Net;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Server.Security;
using AiChromeProxy.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiChromeProxy.Tests.Server;

/// <summary>Real Server pipeline in Production: host filtering (DNS-rebinding hardening) and fail-closed startup validation.</summary>
public sealed class ServerHostingTests : IAsyncDisposable
{
	public const string PublicHost = "code.example.com";

	private readonly TestAccessIssuer _issuer = new();
	private readonly WebApplicationFactory<Program> _factory;

	public ServerHostingTests()
	{
		_factory = Factory(b => b
			.UseSetting("Server:PublicHost", PublicHost)
			.UseSetting("CloudflareAccess:TeamDomain", TestAccessIssuer.TeamDomain)
			.UseSetting("CloudflareAccess:Audience", TestAccessIssuer.Audience)
			.ConfigureServices(s => s.AddHttpClient(CloudflareAccessTokenValidator.JwksHttpClient)
				.ConfigurePrimaryHttpMessageHandler(() => _issuer.Handler())));
	}

	[Theory]
	[InlineData("evil.example.org")]
	[InlineData("code.example.com.evil.example.org")]
	[InlineData("192.168.1.10:5180")]
	public async Task UnknownHost_400_EvenWithValidToken(string host)
	{
		Assert.Equal(HttpStatusCode.BadRequest, await GetAsync(host, _issuer.Token()));
	}

	[Theory]
	[InlineData(PublicHost)]
	[InlineData("127.0.0.1:5180")]
	[InlineData("localhost:5180")]
	public async Task KnownHost_WithToken_200(string host)
	{
		Assert.Equal(HttpStatusCode.OK, await GetAsync(host, _issuer.Token()));
	}

	[Fact]
	public void Production_WithoutPublicHost_FailsToStart()
	{
		// Explicit empty value: the machine running the tests may have a real Server__PublicHost env var set.
		using var factory = Factory(b => b
			.UseSetting("Server:PublicHost", string.Empty)
			.UseSetting("CloudflareAccess:TeamDomain", TestAccessIssuer.TeamDomain)
			.UseSetting("CloudflareAccess:Audience", TestAccessIssuer.Audience));

		var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

		Assert.Contains("Server:PublicHost must be set", ex.ToString());
	}

	[Theory]
	[InlineData("Server:PublicHost", "https://code.example.com")]
	[InlineData("CloudflareAccess:TeamDomain", "https://test-team.cloudflareaccess.com/")]
	public void Production_HostNameNotBare_FailsToStart(string key, string value)
	{
		using var factory = Factory(b => b
			.UseSetting("Server:PublicHost", PublicHost)
			.UseSetting("CloudflareAccess:TeamDomain", TestAccessIssuer.TeamDomain)
			.UseSetting("CloudflareAccess:Audience", TestAccessIssuer.Audience)
			.UseSetting(key, value));

		var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

		Assert.Contains($"{key} must be a bare host name", ex.ToString());
	}

	public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

	private static WebApplicationFactory<Program> Factory(Action<IWebHostBuilder> configure) =>
		new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment(Environments.Production);
			b.UseStaticWebAssets();
			configure(b);
		});

	private async Task<HttpStatusCode> GetAsync(string host, string token)
	{
		using var client = _factory.CreateClient();
		using var request = new HttpRequestMessage(HttpMethod.Get, "/css/app.css");
		request.Headers.Host = host;
		request.Headers.Add(CloudflareAccessMiddleware.HeaderName, token);

		using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
		return response.StatusCode;
	}
}
```

Write `tests/AiChromeProxy.Tests/Server/TransportHubTests.cs`:

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
			b.UseSetting("Server:PublicHost", ServerHostingTests.PublicHost);
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
			b.UseSetting("Server:PublicHost", ServerHostingTests.PublicHost);
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

- [ ] **Step 2: Run to verify they fail (RED)**

Run: `dotnet build -c Release`
Expected: FAIL — compile errors for the types/members this task introduces (or, where the tests only extend existing types, failing assertions in the next step). Record the first errors in your report.

- [ ] **Step 3: Implement**

Write `docs/setup/cloudflare.md`:

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

Three values:

| Setting | Example | What it is |
|---|---|---|
| `CloudflareAccess:TeamDomain` | `<team>.cloudflareaccess.com` | Team domain from step 3 — bare host name, no `https://`, path, port or trailing slash. |
| `CloudflareAccess:Audience` | `<AUD tag>` | Application Audience (AUD) tag from step 3. |
| `Server:PublicHost` | `code.example.com` | Public hostname from step 2. The Server answers only to this `Host` header plus `127.0.0.1` / `localhost` (DNS-rebinding hardening); any other host gets `400`. |

**Installed as a Windows service** (see [windows-host.md](../windows-host.md)): enter them in the tray's **Settings…** window; it writes them to `%ProgramData%\AiChromeProxy\appsettings.json`.

**Running from source** (`dotnet run`): set persistent user environment variables (PowerShell), then open a new terminal so they are visible:

```powershell
[Environment]::SetEnvironmentVariable("CloudflareAccess__TeamDomain", "<team>.cloudflareaccess.com", "User")
[Environment]::SetEnvironmentVariable("CloudflareAccess__Audience", "<AUD tag>", "User")
[Environment]::SetEnvironmentVariable("Server__PublicHost", "code.example.com", "User")
```

Environment variables override the settings file. Outside `Development` the Server refuses to start if any of the three values is missing or a host name is not bare.

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

Write `src/AiChromeProxy.Server/Program.cs`:

```csharp
using System.Net;
using AiChromeProxy.Application;
using AiChromeProxy.Infrastructure;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Server.Security;
using AiChromeProxy.Server.Transport;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

var server = builder.Configuration.GetSection(ServerOptions.Section).Get<ServerOptions>() ?? new ServerOptions();
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, server.Port));
builder.Services.Configure<HostFilteringOptions>(o => o.AllowedHosts = [.. server.AllowedHosts()]);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSignalR();

var app = builder.Build();

server.Validate(app.Environment);
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

Write `src/AiChromeProxy.Server/appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "Server": {
    "Port": 5180,
    "PublicHost": ""
  },
  "CloudflareAccess": {
    "Enabled": true,
    "TeamDomain": "",
    "Audience": ""
  }
}
```

- [ ] **Step 4: Build and run the gate (GREEN)**

Run: `dotnet build -c Release` → Expected: `0 Warning(s)`, `0 Error(s)`.
Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total` then `echo $?` → Expected: `total: 85, failed: 0`, exit code 0.

- [ ] **Step 5: Commit**

```bash
git add -A
git status --short   # every file listed above must appear; nothing under a Logs/ folder
git commit -m "feat(server): AllowedHosts from Server:PublicHost, fail closed outside Development"
```

---

### Task 4: feat(server): Windows service hosting, data directory, Serilog CLEF logs

**Files:**
- Create: `src/AiChromeProxy.Server/Hosting/DataDirectoryHosting.cs`, `tests/AiChromeProxy.Tests/Server/DataDirectoryHostingTests.cs`
- Modify: `src/AiChromeProxy.Server/AiChromeProxy.Server.csproj`, `src/AiChromeProxy.Server/Program.cs`, `src/AiChromeProxy.Server/appsettings.json`

Every code block below is the **complete final content** of that file (verified on the prototype: builds with 0 warnings, gate passes). Write it exactly; for modified files replace the whole file.

- [ ] **Step 1: Write the tests**

Write `tests/AiChromeProxy.Tests/Server/DataDirectoryHostingTests.cs`:

```csharp
using System.Text.Json;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Server.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiChromeProxy.Tests.Server;

public sealed class DataDirectoryHostingTests : IDisposable
{
	private readonly DataDirectory _dataDir = new(Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N")));

	[Fact]
	public void Select_NotServiceNoOverride_NoDataDirectory()
	{
		Assert.Null(DataDirectoryHosting.Select(isWindowsService: false, overrideValue: null));
		Assert.Null(DataDirectoryHosting.Select(isWindowsService: false, overrideValue: " "));
	}

	[Fact]
	public void Select_Service_ProgramData()
	{
		Assert.Equal(DataDirectory.Resolve(null), DataDirectoryHosting.Select(isWindowsService: true, overrideValue: null));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Select_Override_WinsInBothModes(bool isService)
	{
		Assert.Equal(@"D:\aicp", DataDirectoryHosting.Select(isService, @"D:\aicp")?.Root);
	}

	[Fact]
	public void PersistentSettings_OverrideAppSettings_EnvVarsOverrideThem()
	{
		var key = "AicpTests_" + Guid.NewGuid().ToString("N");
		WriteSettings(new { Server = new { Port = 6100 }, AicpTests = new Dictionary<string, string> { [key] = "file", [key + "_FileOnly"] = "file" } });
		Environment.SetEnvironmentVariable($"AicpTests__{key}", "env");
		try
		{
			var builder = WebApplication.CreateBuilder();

			builder.Configuration.AddPersistentSettings(_dataDir);

			Assert.Equal("6100", builder.Configuration["Server:Port"]);
			Assert.Equal("env", builder.Configuration[$"AicpTests:{key}"]);
			Assert.Equal("file", builder.Configuration[$"AicpTests:{key}_FileOnly"]);
		}
		finally
		{
			Environment.SetEnvironmentVariable($"AicpTests__{key}", null);
		}
	}

	[Fact]
	public void PersistentSettings_MissingFileAndFolder_Ignored()
	{
		var config = new ConfigurationManager();
		config.AddInMemoryCollection(new Dictionary<string, string?> { ["Server:Port"] = "5180" });

		config.AddPersistentSettings(_dataDir);

		Assert.Equal("5180", config["Server:Port"]);
	}

	[Fact]
	public void PersistentSettings_NoEnvironmentSource_Appended()
	{
		WriteSettings(new { Server = new { Port = 6200 } });
		var config = new ConfigurationManager();
		config.AddInMemoryCollection(new Dictionary<string, string?> { ["Server:Port"] = "5180" });

		config.AddPersistentSettings(_dataDir);

		Assert.Equal("6200", config["Server:Port"]);
	}

	[Fact]
	public void Logging_WithDataDirectory_WritesDailyClefFile()
	{
		var config = new ConfigurationBuilder().Build();
		using (var provider = new ServiceCollection().AddServerLogging(config, _dataDir).BuildServiceProvider())
		{
			provider.GetRequiredService<ILoggerFactory>().CreateLogger("Test").LogWarning("Hello {Name}", "clef");
		}

		var file = Assert.Single(Directory.GetFiles(_dataDir.Logs));
		Assert.Matches(@"server-\d{8}\.clef$", file);
		using var json = JsonDocument.Parse(File.ReadAllLines(file).Single());
		Assert.Equal("Hello {Name}", json.RootElement.GetProperty("@mt").GetString());
		Assert.Equal("Warning", json.RootElement.GetProperty("@l").GetString());
		Assert.Equal("clef", json.RootElement.GetProperty("Name").GetString());
	}

	[Fact]
	public void Logging_WithoutDataDirectory_NoFiles()
	{
		var config = new ConfigurationBuilder().Build();
		using (var provider = new ServiceCollection().AddServerLogging(config, dataDir: null).BuildServiceProvider())
		{
			provider.GetRequiredService<ILoggerFactory>().CreateLogger("Test").LogWarning("console only");
		}

		Assert.False(Directory.Exists(_dataDir.Root));
	}

	public void Dispose()
	{
		if (Directory.Exists(_dataDir.Root))
		{
			Directory.Delete(_dataDir.Root, recursive: true);
		}
	}

	private void WriteSettings(object settings)
	{
		Directory.CreateDirectory(_dataDir.Root);
		File.WriteAllText(_dataDir.SettingsFile, JsonSerializer.Serialize(settings));
	}
}
```

- [ ] **Step 2: Run to verify they fail (RED)**

Run: `dotnet build -c Release`
Expected: FAIL — compile errors for the types/members this task introduces (or, where the tests only extend existing types, failing assertions in the next step). Record the first errors in your report.

- [ ] **Step 3: Implement**

Write `src/AiChromeProxy.Server/AiChromeProxy.Server.csproj`:

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
    <PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices" Version="10.0.12" />
    <PackageReference Include="Serilog.AspNetCore" Version="10.0.0" />
  </ItemGroup>

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

</Project>
```

Write `src/AiChromeProxy.Server/Hosting/DataDirectoryHosting.cs`:

```csharp
using AiChromeProxy.Infrastructure.Hosting;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Serilog;
using Serilog.Formatting.Compact;

namespace AiChromeProxy.Server.Hosting;

/// <summary>Wires the machine data directory into the host: persistent settings and CLEF log files.</summary>
public static class DataDirectoryHosting
{
	public const string LogFilePattern = "server-.clef";
	public const int RetainedLogFiles = 14;

	/// <summary>
	/// The data directory is used only by the Windows service or when <c>AICP_DATA_DIR</c> is set,
	/// so a developer's machine config never leaks into <c>dotnet run</c> or tests.
	/// </summary>
	/// <returns>The directory, or null when the Server runs without one.</returns>
	public static DataDirectory? Select(bool isWindowsService, string? overrideValue) =>
		isWindowsService || !string.IsNullOrWhiteSpace(overrideValue) ? DataDirectory.Resolve(overrideValue) : null;

	/// <summary>Adds the optional <c>&lt;DataDir&gt;\appsettings.json</c> just below environment variables: env vars and the command line still win.</summary>
	public static void AddPersistentSettings(this IConfigurationBuilder config, DataDirectory dataDir)
	{
		var source = new JsonConfigurationSource { Path = dataDir.SettingsFile, Optional = true };
		source.ResolveFileProvider();
		var envIndex = config.Sources.ToList().FindLastIndex(s => s is EnvironmentVariablesConfigurationSource);
		config.Sources.Insert(envIndex < 0 ? config.Sources.Count : envIndex, source);
	}

	/// <summary>Serilog (levels from the <c>Serilog</c> section): console always; daily CLEF files in <c>&lt;DataDir&gt;\logs</c> when a data directory is in use.</summary>
	public static IServiceCollection AddServerLogging(this IServiceCollection services, IConfiguration configuration, DataDirectory? dataDir)
	{
		var log = new LoggerConfiguration().ReadFrom.Configuration(configuration).Enrich.FromLogContext().WriteTo.Console();
		if (dataDir is not null)
		{
			log.WriteTo.File(
				new CompactJsonFormatter(),
				Path.Combine(dataDir.Logs, LogFilePattern),
				rollingInterval: RollingInterval.Day,
				retainedFileCountLimit: RetainedLogFiles);
		}

		// Owned by the container (dispose: true) and never assigned to the static Log.Logger: parallel test hosts stay isolated.
		return services.AddSerilog(log.CreateLogger(), dispose: true);
	}
}
```

Write `src/AiChromeProxy.Server/Program.cs`:

```csharp
using System.Net;
using AiChromeProxy.Application;
using AiChromeProxy.Infrastructure;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Server.Hosting;
using AiChromeProxy.Server.Security;
using AiChromeProxy.Server.Transport;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

var isService = WindowsServiceHelpers.IsWindowsService();

// A service starts in %WINDIR%\System32: content root (appsettings.json, wwwroot) must be the exe folder.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
	Args = args,
	ContentRootPath = isService ? AppContext.BaseDirectory : null,
});
builder.Host.UseWindowsService();

var dataDir = DataDirectoryHosting.Select(isService, Environment.GetEnvironmentVariable(DataDirectory.OverrideVariable));
if (dataDir is not null)
{
	builder.Configuration.AddPersistentSettings(dataDir);
}

builder.Services.AddServerLogging(builder.Configuration, dataDir);

var server = builder.Configuration.GetSection(ServerOptions.Section).Get<ServerOptions>() ?? new ServerOptions();
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, server.Port));
builder.Services.Configure<HostFilteringOptions>(o => o.AllowedHosts = [.. server.AllowedHosts()]);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSignalR();

var app = builder.Build();

var access = app.Services.GetRequiredService<IOptions<CloudflareAccessOptions>>().Value;
try
{
	server.Validate(app.Environment);
	access.Validate(app.Environment);
}
catch (InvalidOperationException ex)
{
	// A service has no console: the log file is the only place this reason shows up.
	app.Logger.LogCritical(ex, "Invalid configuration, the Server will not start: {Reason}", ex.Message);
	throw;
}

if (!access.Enabled)
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

Write `src/AiChromeProxy.Server/appsettings.json`:

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.AspNetCore": "Warning"
      }
    }
  },
  "Server": {
    "Port": 5180,
    "PublicHost": ""
  },
  "CloudflareAccess": {
    "Enabled": true,
    "TeamDomain": "",
    "Audience": ""
  }
}
```

- [ ] **Step 4: Build and run the gate (GREEN)**

Run: `dotnet build -c Release` → Expected: `0 Warning(s)`, `0 Error(s)`.
Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total` then `echo $?` → Expected: `total: 94, failed: 0`, exit code 0.

- [ ] **Step 5: Commit**

```bash
git add -A
git status --short   # every file listed above must appear; nothing under a Logs/ folder
git commit -m "feat(server): Windows service hosting, data directory, Serilog CLEF logs"
```

---

### Task 5: feat(tray): Avalonia tray app with service status, start, stop, restart

**Files:**
- Create: `src/AiChromeProxy.Tray/AiChromeProxy.Tray.csproj`, `src/AiChromeProxy.Tray/App.axaml`, `src/AiChromeProxy.Tray/App.axaml.cs`, `src/AiChromeProxy.Tray/Assets/tray.ico`, `src/AiChromeProxy.Tray/Program.cs`, `src/AiChromeProxy.Tray/Services/IServiceControl.cs`, `src/AiChromeProxy.Tray/Services/ServiceState.cs`, `src/AiChromeProxy.Tray/Services/WindowsServiceControl.cs`, `src/AiChromeProxy.Tray/ViewModels/TrayViewModel.cs`, `src/AiChromeProxy.Tray/app.manifest`, `tests/AiChromeProxy.Tests/Tray/FakeServiceControl.cs`, `tests/AiChromeProxy.Tests/Tray/TrayViewModelTests.cs`, `tests/AiChromeProxy.Tests/Tray/WindowsServiceControlTests.cs`
- Modify: `AiChromeProxy.slnx`, `tests/AiChromeProxy.Tests/AiChromeProxy.Tests.csproj`, `tests/AiChromeProxy.Tests/Architecture/LayerDependencyTests.cs`, `tests/AiChromeProxy.Tests/testconfig.json`

Every code block below is the **complete final content** of that file (verified on the prototype: builds with 0 warnings, gate passes). Write it exactly; for modified files replace the whole file.

- [ ] **Step 1: Write the tests**

Write `tests/AiChromeProxy.Tests/AiChromeProxy.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
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
    <ProjectReference Include="..\..\src\AiChromeProxy.Tray\AiChromeProxy.Tray.csproj" />
  </ItemGroup>

</Project>
```

Write `tests/AiChromeProxy.Tests/Architecture/LayerDependencyTests.cs`:

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
```

Write `tests/AiChromeProxy.Tests/Tray/FakeServiceControl.cs`:

```csharp
using AiChromeProxy.Tray.Services;

namespace AiChromeProxy.Tests.Tray;

/// <summary>In-memory service: records the call order and can be told to fail.</summary>
public sealed class FakeServiceControl(ServiceState state = ServiceState.Running) : IServiceControl
{
	public ServiceState State { get; set; } = state;

	public List<string> Calls { get; } = [];

	public Exception? FailStart { get; set; }

	public Exception? FailStop { get; set; }

	public Exception? FailGetState { get; set; }

	public ServiceState GetState() => FailGetState is null ? State : throw FailGetState;

	public Task StartAsync(CancellationToken ct)
	{
		Calls.Add("start");
		if (FailStart is not null)
		{
			throw FailStart;
		}

		State = ServiceState.Running;
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken ct)
	{
		Calls.Add("stop");
		if (FailStop is not null)
		{
			throw FailStop;
		}

		State = ServiceState.Stopped;
		return Task.CompletedTask;
	}
}
```

Write `tests/AiChromeProxy.Tests/Tray/TrayViewModelTests.cs`:

```csharp
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.ViewModels;

namespace AiChromeProxy.Tests.Tray;

public sealed class TrayViewModelTests
{
	[Theory]
	[InlineData(ServiceState.NotInstalled, "Service: not installed")]
	[InlineData(ServiceState.Stopped, "Service: stopped")]
	[InlineData(ServiceState.Starting, "Service: starting…")]
	[InlineData(ServiceState.Stopping, "Service: stopping…")]
	[InlineData(ServiceState.Running, "Service: running")]
	public void Refresh_StatusLineFollowsService(ServiceState state, string text)
	{
		var vm = new TrayViewModel(new FakeServiceControl(state));

		vm.Refresh();

		Assert.Equal(text, vm.StatusText);
	}

	[Theory]
	[InlineData(ServiceState.NotInstalled, false, false)]
	[InlineData(ServiceState.Stopped, true, false)]
	[InlineData(ServiceState.Starting, false, true)]
	[InlineData(ServiceState.Stopping, false, false)]
	[InlineData(ServiceState.Running, false, true)]
	public void Commands_EnabledByState(ServiceState state, bool canStart, bool canStop)
	{
		var vm = new TrayViewModel(new FakeServiceControl(state));

		vm.Refresh();

		Assert.Equal(canStart, vm.StartCommand.CanExecute(null));
		Assert.Equal(canStop, vm.StopCommand.CanExecute(null));
		Assert.Equal(canStop, vm.RestartCommand.CanExecute(null));
	}

	[Fact]
	public async Task Restart_StopsThenStarts_AndRefreshes()
	{
		var service = new FakeServiceControl(ServiceState.Running);
		var vm = new TrayViewModel(service);
		vm.Refresh();

		await vm.RestartCommand.ExecuteAsync(null);

		Assert.Equal(["stop", "start"], service.Calls);
		Assert.Equal(ServiceState.Running, vm.State);
		Assert.Null(vm.Error);
	}

	[Fact]
	public async Task Stop_StopsService()
	{
		var service = new FakeServiceControl(ServiceState.Running);
		var vm = new TrayViewModel(service);
		vm.Refresh();

		await vm.StopCommand.ExecuteAsync(null);

		Assert.Equal(["stop"], service.Calls);
		Assert.Equal(ServiceState.Stopped, vm.State);
		Assert.True(vm.StartCommand.CanExecute(null));
	}

	[Fact]
	public async Task Start_Fails_ErrorShown_NextActionClearsIt()
	{
		var service = new FakeServiceControl(ServiceState.Stopped) { FailStart = new InvalidOperationException("access denied") };
		var vm = new TrayViewModel(service);
		vm.Refresh();

		await vm.StartCommand.ExecuteAsync(null);
		Assert.Equal("access denied", vm.Error);

		service.FailStart = null;
		await vm.StartCommand.ExecuteAsync(null);
		Assert.Null(vm.Error);
		Assert.Equal(ServiceState.Running, vm.State);
	}

	[Fact]
	public void Refresh_StatusQueryFails_ErrorShown()
	{
		var vm = new TrayViewModel(new FakeServiceControl { FailGetState = new InvalidOperationException("scm down") });

		vm.Refresh();

		Assert.Equal("scm down", vm.Error);
	}
}
```

Write `tests/AiChromeProxy.Tests/Tray/WindowsServiceControlTests.cs`:

```csharp
using AiChromeProxy.Tray.Services;

namespace AiChromeProxy.Tests.Tray;

/// <summary>Read-only queries against the real Service Control Manager (no elevation, nothing changed).</summary>
public sealed class WindowsServiceControlTests
{
	[Fact]
	public void UnknownService_NotInstalled()
	{
		Assert.Equal(ServiceState.NotInstalled, new WindowsServiceControl("AiChromeProxyTests-" + Guid.NewGuid().ToString("N")).GetState());
	}

	[Fact]
	public void EventLogService_Running()
	{
		Assert.Equal(ServiceState.Running, new WindowsServiceControl("EventLog").GetState());
	}
}
```

Write `tests/AiChromeProxy.Tests/testconfig.json`:

```json
{
  "platformOptions": {
    "Coverlet": {
      "include": "[AiChromeProxy.*]*",
      "exclude": "[AiChromeProxy.Tests]*",
      "excludeByAttribute": "GeneratedCode,GeneratedCodeAttribute,ExcludeFromCodeCoverage,ExcludeFromCodeCoverageAttribute,CompilerGeneratedAttribute",
      "excludeByFile": "**/Program.cs,**/*.razor,**/*.axaml.cs",
      "format": "cobertura",
      "excludeAssembliesWithoutSources": "MissingAll"
    }
  }
}
```

- [ ] **Step 2: Run to verify they fail (RED)**

Run: `dotnet build -c Release`
Expected: FAIL — compile errors for the types/members this task introduces (or, where the tests only extend existing types, failing assertions in the next step). Record the first errors in your report.

- [ ] **Step 3: Implement**

Write `AiChromeProxy.slnx`:

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
    <Project Path="src/AiChromeProxy.Tray/AiChromeProxy.Tray.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/AiChromeProxy.E2E/AiChromeProxy.E2E.csproj" />
    <Project Path="tests/AiChromeProxy.Tests/AiChromeProxy.Tests.csproj" />
  </Folder>
</Solution>
```

Write `src/AiChromeProxy.Tray/AiChromeProxy.Tray.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <ApplicationIcon>Assets\tray.ico</ApplicationIcon>
    <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Avalonia" Version="12.1.3" />
    <PackageReference Include="Avalonia.Desktop" Version="12.1.3" />
    <PackageReference Include="Avalonia.Themes.Fluent" Version="12.1.3" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
    <PackageReference Include="System.ServiceProcess.ServiceController" Version="10.0.12" />
    <PackageReference Include="Velopack" Version="1.2.161" />
  </ItemGroup>

  <ItemGroup>
    <AvaloniaResource Include="Assets\**" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\AiChromeProxy.Domain\AiChromeProxy.Domain.csproj" />
    <ProjectReference Include="..\AiChromeProxy.Infrastructure\AiChromeProxy.Infrastructure.csproj" />
  </ItemGroup>

</Project>
```

Write `src/AiChromeProxy.Tray/App.axaml`:

```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="AiChromeProxy.Tray.App"
             RequestedThemeVariant="Default">
  <Application.Styles>
    <FluentTheme />
  </Application.Styles>
</Application>
```

Write `src/AiChromeProxy.Tray/App.axaml.cs`:

```csharp
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.ViewModels;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;

namespace AiChromeProxy.Tray;

public partial class App : Avalonia.Application
{
	private static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(2);

	public override void Initialize() => AvaloniaXamlLoader.Load(this);

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			StartTray(desktop);
		}

		base.OnFrameworkInitializationCompleted();
	}

	private static NativeMenuItem Item(string header, Action onClick)
	{
		var item = new NativeMenuItem(header);
		item.Click += (_, _) => onClick();
		return item;
	}

	private void StartTray(IClassicDesktopStyleApplicationLifetime desktop)
	{
		var vm = new TrayViewModel(new WindowsServiceControl());
		var status = new NativeMenuItem { IsEnabled = false };
		var error = new NativeMenuItem { IsEnabled = false };
		var menu = new NativeMenu
		{
			status,
			error,
			new NativeMenuItemSeparator(),
			new NativeMenuItem("Start") { Command = vm.StartCommand },
			new NativeMenuItem("Stop") { Command = vm.StopCommand },
			new NativeMenuItem("Restart") { Command = vm.RestartCommand },
			new NativeMenuItemSeparator(),
			Item("Exit", () => desktop.Shutdown()),
		};

		var icon = new TrayIcon
		{
			Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://AiChromeProxy.Tray/Assets/tray.ico"))),
			Menu = menu,
		};

		void Render()
		{
			status.Header = vm.StatusText;
			icon.ToolTipText = "AI Chrome Proxy: " + vm.StatusText;
			error.Header = vm.Error;
			error.IsVisible = vm.Error is not null;
		}

		vm.PropertyChanged += (_, _) => Render();
		vm.Refresh();
		Render();
		TrayIcon.SetIcons(this, [icon]);

		// ponytail: polls the SCM every 2 s for the tray's lifetime (one cheap query); restrict to "menu open" via NativeMenu.Opening/Closed if it ever matters.
		DispatcherTimer.Run(
			() =>
			{
				vm.Refresh();
				return true;
			},
			StatusPollInterval);
	}
}
```

Write `src/AiChromeProxy.Tray/Assets/tray.ico`:

Binary file — create it from base64 (Git Bash):

```bash
mkdir -p src/AiChromeProxy.Tray/Assets
printf '%s' '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' | base64 -d > src/AiChromeProxy.Tray/Assets/tray.ico
```

Write `src/AiChromeProxy.Tray/Program.cs`:

```csharp
using Avalonia;
using Avalonia.Controls;

namespace AiChromeProxy.Tray;

internal static class Program
{
	[STAThread]
	public static int Main(string[] args)
	{
		// One tray per user session (Start with Windows plus a manual launch must not show two icons).
		using var single = new Mutex(initiallyOwned: true, @"Local\AiChromeProxy.Tray", out var isFirst);
		return isFirst ? BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown) : 0;
	}

	/// <summary>Also used by the Avalonia previewer.</summary>
	public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
```

Write `src/AiChromeProxy.Tray/Services/IServiceControl.cs`:

```csharp
namespace AiChromeProxy.Tray.Services;

/// <summary>The Windows service seam: view models and the update orchestrator are unit-tested against a fake.</summary>
public interface IServiceControl
{
	ServiceState GetState();

	/// <summary>Starts the service and waits (up to 30 s) until it reports Running.</summary>
	Task StartAsync(CancellationToken ct);

	/// <summary>Stops the service and waits (up to 30 s) until it reports Stopped.</summary>
	Task StopAsync(CancellationToken ct);
}
```

Write `src/AiChromeProxy.Tray/Services/ServiceState.cs`:

```csharp
namespace AiChromeProxy.Tray.Services;

public enum ServiceState
{
	NotInstalled,
	Stopped,
	Starting,
	Stopping,
	Running,
}
```

Write `src/AiChromeProxy.Tray/Services/WindowsServiceControl.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.ServiceProcess;

namespace AiChromeProxy.Tray.Services;

/// <summary>Status, start and stop through the Service Control Manager; no elevation once the service DACL grants the user start/stop.</summary>
public sealed class WindowsServiceControl(string serviceName = WindowsServiceControl.ServiceName) : IServiceControl
{
	public const string ServiceName = "AiChromeProxy";

	private const int ErrorServiceDoesNotExist = 1060;
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

	public ServiceState GetState()
	{
		using var service = new ServiceController(serviceName);
		try
		{
			return service.Status switch
			{
				ServiceControllerStatus.Running => ServiceState.Running,
				ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => ServiceState.Starting,
				ServiceControllerStatus.StopPending or ServiceControllerStatus.PausePending => ServiceState.Stopping,
				_ => ServiceState.Stopped,
			};
		}
		catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: ErrorServiceDoesNotExist })
		{
			return ServiceState.NotInstalled;
		}
	}

	/// <summary>Not unit-tested: starting a real service needs one installed with a DACL for the test user (manual checklist).</summary>
	[ExcludeFromCodeCoverage]
	public Task StartAsync(CancellationToken ct) => Task.Run(
		() =>
		{
			using var service = new ServiceController(serviceName);
			if (service.Status == ServiceControllerStatus.Stopped)
			{
				service.Start();
			}

			service.WaitForStatus(ServiceControllerStatus.Running, Timeout);
		},
		ct);

	[ExcludeFromCodeCoverage]
	public Task StopAsync(CancellationToken ct) => Task.Run(
		() =>
		{
			using var service = new ServiceController(serviceName);
			if (service.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
			{
				// Stop() would enumerate dependent services first, which needs SERVICE_ENUMERATE_DEPENDENTS; there are none.
				service.Stop(stopDependentServices: false);
			}

			service.WaitForStatus(ServiceControllerStatus.Stopped, Timeout);
		},
		ct);
}
```

Write `src/AiChromeProxy.Tray/ViewModels/TrayViewModel.cs`:

```csharp
using AiChromeProxy.Tray.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiChromeProxy.Tray.ViewModels;

/// <summary>Tray menu state: service status line and Start / Stop / Restart.</summary>
public sealed partial class TrayViewModel(IServiceControl service) : ObservableObject
{
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(StatusText))]
	[NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(RestartCommand))]
	public partial ServiceState State { get; private set; }

	/// <summary>Last failure of a menu action or status query; cleared when the next action starts.</summary>
	[ObservableProperty]
	public partial string? Error { get; private set; }

	public string StatusText => State switch
	{
		ServiceState.NotInstalled => "Service: not installed",
		ServiceState.Stopped => "Service: stopped",
		ServiceState.Starting => "Service: starting…",
		ServiceState.Stopping => "Service: stopping…",
		_ => "Service: running",
	};

	public void Refresh()
	{
		try
		{
			State = service.GetState();
		}
		catch (Exception ex)
		{
			Error = ex.Message;
		}
	}

	[RelayCommand(CanExecute = nameof(CanStart))]
	private Task StartAsync() => RunAsync(service.StartAsync);

	[RelayCommand(CanExecute = nameof(CanStop))]
	private Task StopAsync() => RunAsync(service.StopAsync);

	[RelayCommand(CanExecute = nameof(CanStop))]
	private Task RestartAsync() => RunAsync(async ct =>
	{
		await service.StopAsync(ct);
		await service.StartAsync(ct);
	});

	private bool CanStart() => State == ServiceState.Stopped;

	private bool CanStop() => State is ServiceState.Running or ServiceState.Starting;

	private async Task RunAsync(Func<CancellationToken, Task> action)
	{
		Error = null;
		try
		{
			await action(CancellationToken.None);
		}
		catch (Exception ex)
		{
			Error = ex.Message;
		}

		Refresh();
	}
}
```

Write `src/AiChromeProxy.Tray/app.manifest`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="AiChromeProxy.Tray" />
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <!-- Never elevated by default: only the "--admin" relaunch asks for UAC (Verb = runas). -->
        <requestedExecutionLevel level="asInvoker" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <!-- Windows 10 and 11 -->
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
    </application>
  </compatibility>
</assembly>
```

- [ ] **Step 4: Build and run the gate (GREEN)**

Run: `dotnet build -c Release` → Expected: `0 Warning(s)`, `0 Error(s)`.
Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total` then `echo $?` → Expected: `total: 111, failed: 0`, exit code 0.

- [ ] **Step 5: Commit**

```bash
git add -A
git status --short   # every file listed above must appear; nothing under a Logs/ folder
git commit -m "feat(tray): Avalonia tray app with service status, start, stop, restart"
```

---

### Task 6: feat(tray): settings window with the Server's validation rules

**Files:**
- Create: `src/AiChromeProxy.Tray/Services/IAutoStart.cs`, `src/AiChromeProxy.Tray/Services/RegistryAutoStart.cs`, `src/AiChromeProxy.Tray/ViewModels/SettingsViewModel.cs`, `src/AiChromeProxy.Tray/Views/SettingsWindow.axaml`, `src/AiChromeProxy.Tray/Views/SettingsWindow.axaml.cs`, `tests/AiChromeProxy.Tests/Tray/SettingsViewModelTests.cs`
- Modify: `src/AiChromeProxy.Tray/App.axaml.cs`

Every code block below is the **complete final content** of that file (verified on the prototype: builds with 0 warnings, gate passes). Write it exactly; for modified files replace the whole file.

- [ ] **Step 1: Write the tests**

Write `tests/AiChromeProxy.Tests/Tray/SettingsViewModelTests.cs`:

```csharp
using System.Text.Json.Nodes;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.ViewModels;

namespace AiChromeProxy.Tests.Tray;

public sealed class SettingsViewModelTests : IDisposable
{
	private readonly DataDirectory _dataDir = new(Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N")));
	private readonly FakeAutoStart _autoStart = new();
	private readonly FakeServiceControl _service = new(ServiceState.Running);

	[Fact]
	public void NoSettingsFile_Defaults()
	{
		var vm = Create();

		Assert.Equal(string.Empty, vm.TeamDomain);
		Assert.Equal("5180", vm.Port);
		Assert.Empty(vm.Errors);
	}

	[Fact]
	public void ExistingFile_ValuesLoaded()
	{
		WriteFile("""{ "CloudflareAccess": { "TeamDomain": "t.cloudflareaccess.com", "Audience": "aud" }, "Server": { "Port": 6000, "PublicHost": "code.example.com" } }""");
		_autoStart.IsEnabled = true;

		var vm = Create();

		Assert.Equal("t.cloudflareaccess.com", vm.TeamDomain);
		Assert.Equal("aud", vm.Audience);
		Assert.Equal("6000", vm.Port);
		Assert.Equal("code.example.com", vm.PublicHost);
		Assert.True(vm.StartWithWindows);
	}

	[Fact]
	public void BrokenFile_ErrorShown_SaveReplacesIt()
	{
		WriteFile("{ not json");

		var vm = Valid(Create());
		Assert.Contains("Could not read", Assert.Single(vm.Errors));

		vm.SaveCommand.Execute(null);

		Assert.Empty(vm.Errors);
		Assert.Equal("code.example.com", (string?)ReadFile()["Server"]?["PublicHost"]);
	}

	[Theory]
	[InlineData("https://team.cloudflareaccess.com", "aud", "code.example.com", "5180", "CloudflareAccess:TeamDomain must be a bare host name")]
	[InlineData("team.cloudflareaccess.com", "", "code.example.com", "5180", "CloudflareAccess:TeamDomain and CloudflareAccess:Audience must be set.")]
	[InlineData("team.cloudflareaccess.com", "aud", "", "5180", "Server:PublicHost must be set")]
	[InlineData("team.cloudflareaccess.com", "aud", "code.example.com:443", "5180", "Server:PublicHost must be a bare host name")]
	[InlineData("team.cloudflareaccess.com", "aud", "code.example.com", "0", "Server:Port must be a number from 1 to 65535.")]
	[InlineData("team.cloudflareaccess.com", "aud", "code.example.com", "65536", "Server:Port must be a number from 1 to 65535.")]
	[InlineData("team.cloudflareaccess.com", "aud", "code.example.com", "-1", "Server:Port must be a number from 1 to 65535.")]
	[InlineData("team.cloudflareaccess.com", "aud", "code.example.com", "abc", "Server:Port must be a number from 1 to 65535.")]
	public void Save_Invalid_ShowsServerMessage_WritesNothing(string team, string audience, string publicHost, string port, string message)
	{
		var vm = Create();
		vm.TeamDomain = team;
		vm.Audience = audience;
		vm.PublicHost = publicHost;
		vm.Port = port;

		vm.SaveCommand.Execute(null);

		Assert.StartsWith(message, Assert.Single(vm.Errors));
		Assert.False(File.Exists(_dataDir.SettingsFile));
		Assert.Null(vm.Status);
	}

	[Fact]
	public void Save_Valid_WritesExpectedJson_KeepsOtherKeys_SetsAutoStart()
	{
		WriteFile("""{ "Serilog": { "MinimumLevel": { "Default": "Debug" } }, "Server": { "Port": 5180, "Extra": true } }""");
		var vm = Valid(Create());
		vm.Port = " 6001 ";
		vm.StartWithWindows = true;

		vm.SaveCommand.Execute(null);

		Assert.Empty(vm.Errors);
		var expected = JsonNode.Parse("""
			{
			  "Serilog": { "MinimumLevel": { "Default": "Debug" } },
			  "Server": { "Port": 6001, "Extra": true, "PublicHost": "code.example.com" },
			  "CloudflareAccess": { "TeamDomain": "team.cloudflareaccess.com", "Audience": "aud" }
			}
			""");
		Assert.True(JsonNode.DeepEquals(expected, ReadFile()), ReadFile().ToJsonString());
		Assert.True(_autoStart.IsEnabled);
	}

	[Fact]
	public void Save_FileNotWritable_ErrorShown_NoRestartOffered()
	{
		Directory.CreateDirectory(_dataDir.SettingsFile);
		var vm = Valid(Create());

		vm.SaveCommand.Execute(null);

		Assert.Contains("appsettings.json", Assert.Single(vm.Errors));
		Assert.False(vm.IsRestartOffered);
		Assert.Null(vm.Status);
	}

	[Fact]
	public async Task Save_ServiceRunning_OffersRestart_RestartStopsThenStarts()
	{
		var vm = Valid(Create());

		vm.SaveCommand.Execute(null);

		Assert.True(vm.IsRestartOffered);
		Assert.Equal("Saved. Restart the service to apply.", vm.Status);
		await vm.RestartServiceCommand.ExecuteAsync(null);
		Assert.Equal(["stop", "start"], _service.Calls);
		Assert.False(vm.IsRestartOffered);
		Assert.Equal("Service restarted.", vm.Status);
	}

	[Fact]
	public async Task RestartFails_ErrorShown()
	{
		_service.FailStart = new InvalidOperationException("cannot start");
		var vm = Valid(Create());
		vm.SaveCommand.Execute(null);

		await vm.RestartServiceCommand.ExecuteAsync(null);

		Assert.Equal(["cannot start"], vm.Errors);
	}

	[Theory]
	[InlineData(ServiceState.Stopped)]
	[InlineData(ServiceState.NotInstalled)]
	public void Save_ServiceNotRunning_NoRestartOffered(ServiceState state)
	{
		_service.State = state;
		var vm = Valid(Create());

		vm.SaveCommand.Execute(null);

		Assert.False(vm.IsRestartOffered);
		Assert.False(vm.RestartServiceCommand.CanExecute(null));
		Assert.Equal("Saved. Applied when the service starts.", vm.Status);
	}

	[Fact]
	public void UiAddress_PublicHost_ThroughTunnel()
	{
		WriteFile("""{ "Server": { "Port": 6000, "PublicHost": "code.example.com" } }""");

		Assert.Equal("https://code.example.com/", SettingsViewModel.UiAddress(_dataDir).AbsoluteUri);
	}

	[Theory]
	[InlineData("""{ "Server": { "Port": 6000 } }""", "http://127.0.0.1:6000/")]
	[InlineData("{ broken", "http://127.0.0.1:5180/")]
	public void UiAddress_NoPublicHost_Loopback(string json, string expected)
	{
		WriteFile(json);

		Assert.Equal(expected, SettingsViewModel.UiAddress(_dataDir).AbsoluteUri);
	}

	public void Dispose()
	{
		if (Directory.Exists(_dataDir.Root))
		{
			Directory.Delete(_dataDir.Root, recursive: true);
		}
	}

	private static SettingsViewModel Valid(SettingsViewModel vm)
	{
		vm.TeamDomain = " team.cloudflareaccess.com ";
		vm.Audience = "aud";
		vm.PublicHost = "code.example.com";
		return vm;
	}

	private SettingsViewModel Create() => new(_dataDir, _autoStart, _service);

	private void WriteFile(string json)
	{
		Directory.CreateDirectory(_dataDir.Root);
		File.WriteAllText(_dataDir.SettingsFile, json);
	}

	private JsonNode ReadFile() => JsonNode.Parse(File.ReadAllText(_dataDir.SettingsFile))!;

	private sealed class FakeAutoStart : IAutoStart
	{
		public bool IsEnabled { get; set; }
	}
}
```

- [ ] **Step 2: Run to verify they fail (RED)**

Run: `dotnet build -c Release`
Expected: FAIL — compile errors for the types/members this task introduces (or, where the tests only extend existing types, failing assertions in the next step). Record the first errors in your report.

- [ ] **Step 3: Implement**

Write `src/AiChromeProxy.Tray/App.axaml.cs`:

```csharp
using System.Diagnostics;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.ViewModels;
using AiChromeProxy.Tray.Views;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;

namespace AiChromeProxy.Tray;

public partial class App : Avalonia.Application
{
	private static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(2);

	public override void Initialize() => AvaloniaXamlLoader.Load(this);

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			StartTray(desktop);
		}

		base.OnFrameworkInitializationCompleted();
	}

	private static NativeMenuItem Item(string header, Action onClick)
	{
		var item = new NativeMenuItem(header);
		item.Click += (_, _) => onClick();
		return item;
	}

	/// <summary>One window per kind: a second click brings the open one to front.</summary>
	private static void ShowSingle<TWindow>(IClassicDesktopStyleApplicationLifetime desktop, Func<TWindow> create)
		where TWindow : Window
	{
		var window = desktop.Windows.OfType<TWindow>().FirstOrDefault() ?? create();
		window.Show();
		window.Activate();
	}

	private static void Open(Uri address) => Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true })?.Dispose();

	private void StartTray(IClassicDesktopStyleApplicationLifetime desktop)
	{
		var dataDir = DataDirectory.FromEnvironment();
		var service = new WindowsServiceControl();
		var vm = new TrayViewModel(service);
		var status = new NativeMenuItem { IsEnabled = false };
		var error = new NativeMenuItem { IsEnabled = false };
		var menu = new NativeMenu
		{
			status,
			error,
			new NativeMenuItemSeparator(),
			new NativeMenuItem("Start") { Command = vm.StartCommand },
			new NativeMenuItem("Stop") { Command = vm.StopCommand },
			new NativeMenuItem("Restart") { Command = vm.RestartCommand },
			new NativeMenuItemSeparator(),
			Item("Settings…", () => ShowSingle(desktop, () => new SettingsWindow { DataContext = new SettingsViewModel(dataDir, new RegistryAutoStart(), service) })),
			Item("Open UI", () => Open(SettingsViewModel.UiAddress(dataDir))),
			new NativeMenuItemSeparator(),
			Item("Exit", () => desktop.Shutdown()),
		};

		var icon = new TrayIcon
		{
			Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://AiChromeProxy.Tray/Assets/tray.ico"))),
			Menu = menu,
		};

		void Render()
		{
			status.Header = vm.StatusText;
			icon.ToolTipText = "AI Chrome Proxy: " + vm.StatusText;
			error.Header = vm.Error;
			error.IsVisible = vm.Error is not null;
		}

		vm.PropertyChanged += (_, _) => Render();
		vm.Refresh();
		Render();
		TrayIcon.SetIcons(this, [icon]);

		// ponytail: polls the SCM every 2 s for the tray's lifetime (one cheap query); restrict to "menu open" via NativeMenu.Opening/Closed if it ever matters.
		DispatcherTimer.Run(
			() =>
			{
				vm.Refresh();
				return true;
			},
			StatusPollInterval);
	}
}
```

Write `src/AiChromeProxy.Tray/Services/IAutoStart.cs`:

```csharp
namespace AiChromeProxy.Tray.Services;

/// <summary>"Start with Windows" for the tray (per user, at login).</summary>
public interface IAutoStart
{
	bool IsEnabled { get; set; }
}
```

Write `src/AiChromeProxy.Tray/Services/RegistryAutoStart.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32;

namespace AiChromeProxy.Tray.Services;

/// <summary>
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> value pointing at the running tray exe
/// (Velopack's stable <c>current\AiChromeProxy.Tray.exe</c>, so it survives updates).
/// Excluded from coverage: tests must not write the user's registry; covered by the manual checklist.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class RegistryAutoStart : IAutoStart
{
	private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
	private const string ValueName = "AiChromeProxy";

	public bool IsEnabled
	{
		get
		{
			using var key = Registry.CurrentUser.OpenSubKey(RunKey);
			return key?.GetValue(ValueName) is not null;
		}

		set
		{
			using var key = Registry.CurrentUser.CreateSubKey(RunKey);
			if (value)
			{
				key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
			}
			else
			{
				key.DeleteValue(ValueName, throwOnMissingValue: false);
			}
		}
	}
}
```

Write `src/AiChromeProxy.Tray/ViewModels/SettingsViewModel.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Tray.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiChromeProxy.Tray.ViewModels;

/// <summary>Edits <c>&lt;DataDir&gt;\appsettings.json</c> with the Server's own validation rules; other keys in the file are kept.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
	private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

	private readonly DataDirectory _dataDir;
	private readonly IAutoStart _autoStart;
	private readonly IServiceControl _service;

	public SettingsViewModel(DataDirectory dataDir, IAutoStart autoStart, IServiceControl service)
	{
		_dataDir = dataDir;
		_autoStart = autoStart;
		_service = service;

		var settings = new JsonObject();
		try
		{
			settings = Load(dataDir);
		}
		catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
		{
			Errors = [$"Could not read {dataDir.SettingsFile}: {ex.Message} Saving replaces the file."];
		}

		TeamDomain = (string?)settings[CloudflareAccessOptions.Section]?[nameof(CloudflareAccessOptions.TeamDomain)] ?? string.Empty;
		Audience = (string?)settings[CloudflareAccessOptions.Section]?[nameof(CloudflareAccessOptions.Audience)] ?? string.Empty;
		PublicHost = (string?)settings[ServerOptions.Section]?[nameof(ServerOptions.PublicHost)] ?? string.Empty;
		Port = settings[ServerOptions.Section]?[nameof(ServerOptions.Port)]?.ToString() ?? ServerOptions.DefaultPort.ToString(CultureInfo.InvariantCulture);
		StartWithWindows = autoStart.IsEnabled;
	}

	[ObservableProperty]
	public partial string TeamDomain { get; set; }

	[ObservableProperty]
	public partial string Audience { get; set; }

	[ObservableProperty]
	public partial string PublicHost { get; set; }

	[ObservableProperty]
	public partial string Port { get; set; }

	[ObservableProperty]
	public partial bool StartWithWindows { get; set; }

	[ObservableProperty]
	public partial IReadOnlyList<string> Errors { get; private set; } = [];

	[ObservableProperty]
	public partial string? Status { get; private set; }

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(RestartServiceCommand))]
	public partial bool IsRestartOffered { get; private set; }

	/// <summary>The settings file as a JSON object (empty when it does not exist yet).</summary>
	public static JsonObject Load(DataDirectory dataDir) =>
		File.Exists(dataDir.SettingsFile) ? JsonNode.Parse(File.ReadAllText(dataDir.SettingsFile))?.AsObject() ?? [] : [];

	/// <summary>"Open UI": through the tunnel when a public host is set (local requests carry no Access token), else loopback.</summary>
	public static Uri UiAddress(DataDirectory dataDir)
	{
		var settings = LoadOrEmpty(dataDir);
		var publicHost = (string?)settings[ServerOptions.Section]?[nameof(ServerOptions.PublicHost)];
		var port = settings[ServerOptions.Section]?[nameof(ServerOptions.Port)]?.ToString() ?? ServerOptions.DefaultPort.ToString(CultureInfo.InvariantCulture);
		return string.IsNullOrWhiteSpace(publicHost) ? new Uri($"http://127.0.0.1:{port}/") : new Uri($"https://{publicHost}/");
	}

	/// <returns>The messages the Server would fail with for these values (empty when valid).</returns>
	public IReadOnlyList<string> Validate()
	{
		var errors = new List<string>();
		if (ParsePort() is null)
		{
			errors.Add("Server:Port must be a number from 1 to 65535.");
		}

		if (new ServerOptions { PublicHost = PublicHost.Trim() }.GetError(isDevelopment: false) is { } serverError)
		{
			errors.Add(serverError);
		}

		if (new CloudflareAccessOptions { TeamDomain = TeamDomain.Trim(), Audience = Audience.Trim() }.GetError(isDevelopment: false) is { } accessError)
		{
			errors.Add(accessError);
		}

		return errors;
	}

	/// <summary>A broken file counts as empty: the user was told on open that saving replaces it.</summary>
	private static JsonObject LoadOrEmpty(DataDirectory dataDir)
	{
		try
		{
			return Load(dataDir);
		}
		catch (JsonException)
		{
			return [];
		}
	}

	private static JsonObject Section(JsonObject settings, string name)
	{
		if (settings[name] is JsonObject section)
		{
			return section;
		}

		section = [];
		settings[name] = section;
		return section;
	}

	[RelayCommand]
	private void Save()
	{
		Status = null;
		IsRestartOffered = false;
		Errors = Validate();
		if (Errors.Count > 0)
		{
			return;
		}

		try
		{
			var settings = LoadOrEmpty(_dataDir);
			var access = Section(settings, CloudflareAccessOptions.Section);
			access[nameof(CloudflareAccessOptions.TeamDomain)] = TeamDomain.Trim();
			access[nameof(CloudflareAccessOptions.Audience)] = Audience.Trim();
			var server = Section(settings, ServerOptions.Section);
			server[nameof(ServerOptions.Port)] = ParsePort();
			server[nameof(ServerOptions.PublicHost)] = PublicHost.Trim();

			Directory.CreateDirectory(_dataDir.Root);
			File.WriteAllText(_dataDir.SettingsFile, settings.ToJsonString(Indented));
			_autoStart.IsEnabled = StartWithWindows;
		}
		catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
		{
			Errors = [ex.Message];
			return;
		}

		IsRestartOffered = _service.GetState() is ServiceState.Running or ServiceState.Starting;
		Status = IsRestartOffered ? "Saved. Restart the service to apply." : "Saved. Applied when the service starts.";
	}

	[RelayCommand(CanExecute = nameof(IsRestartOffered))]
	private async Task RestartServiceAsync()
	{
		try
		{
			await _service.StopAsync(CancellationToken.None);
			await _service.StartAsync(CancellationToken.None);
			IsRestartOffered = false;
			Status = "Service restarted.";
		}
		catch (Exception ex)
		{
			Errors = [ex.Message];
		}
	}

	private int? ParsePort() =>
		int.TryParse(Port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535 ? port : null;
}
```

Write `src/AiChromeProxy.Tray/Views/SettingsWindow.axaml`:

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:AiChromeProxy.Tray.ViewModels"
        x:Class="AiChromeProxy.Tray.Views.SettingsWindow"
        x:DataType="vm:SettingsViewModel"
        Title="AI Chrome Proxy settings"
        Icon="/Assets/tray.ico"
        Width="520" SizeToContent="Height" CanResize="False"
        WindowStartupLocation="CenterScreen">
  <StackPanel Margin="16" Spacing="8">
    <TextBlock Text="Cloudflare Access team domain" />
    <TextBox Text="{Binding TeamDomain}" PlaceholderText="team.cloudflareaccess.com" />
    <TextBlock Text="Cloudflare Access application audience (AUD tag)" />
    <TextBox Text="{Binding Audience}" />
    <TextBlock Text="Public host name (the tunnel's hostname)" />
    <TextBox Text="{Binding PublicHost}" PlaceholderText="code.example.com" />
    <TextBlock Text="Local port" />
    <TextBox Text="{Binding Port}" Width="120" HorizontalAlignment="Left" />
    <CheckBox Content="Start the tray with Windows" IsChecked="{Binding StartWithWindows}" />
    <ItemsControl ItemsSource="{Binding Errors}">
      <ItemsControl.ItemTemplate>
        <DataTemplate x:DataType="x:String">
          <TextBlock Text="{Binding}" Foreground="#C42B1C" TextWrapping="Wrap" />
        </DataTemplate>
      </ItemsControl.ItemTemplate>
    </ItemsControl>
    <TextBlock Text="{Binding Status}" TextWrapping="Wrap" />
    <StackPanel Orientation="Horizontal" Spacing="8" HorizontalAlignment="Right">
      <Button Content="Restart service" Command="{Binding RestartServiceCommand}" IsVisible="{Binding IsRestartOffered}" />
      <Button Content="Save" Command="{Binding SaveCommand}" IsDefault="True" />
    </StackPanel>
  </StackPanel>
</Window>
```

Write `src/AiChromeProxy.Tray/Views/SettingsWindow.axaml.cs`:

```csharp
using Avalonia.Controls;

namespace AiChromeProxy.Tray.Views;

public partial class SettingsWindow : Window
{
	public SettingsWindow() => InitializeComponent();
}
```

- [ ] **Step 4: Build and run the gate (GREEN)**

Run: `dotnet build -c Release` → Expected: `0 Warning(s)`, `0 Error(s)`.
Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total` then `echo $?` → Expected: `total: 131, failed: 0`, exit code 0.

- [ ] **Step 5: Commit**

```bash
git add -A
git status --short   # every file listed above must appear; nothing under a Logs/ folder
git commit -m "feat(tray): settings window with the Server's validation rules"
```

---

### Task 7: feat(tray): logs window with CLEF parser, filters and follow

**Files:**
- Create: `src/AiChromeProxy.Tray/Clef/ClefLevel.cs`, `src/AiChromeProxy.Tray/Clef/ClefParser.cs`, `src/AiChromeProxy.Tray/Clef/ClefTail.cs`, `src/AiChromeProxy.Tray/Clef/LogEntry.cs`, `src/AiChromeProxy.Tray/ViewModels/LogsViewModel.cs`, `src/AiChromeProxy.Tray/Views/LogsWindow.axaml`, `src/AiChromeProxy.Tray/Views/LogsWindow.axaml.cs`, `tests/AiChromeProxy.Tests/Tray/ClefTests.cs`, `tests/AiChromeProxy.Tests/Tray/LogsViewModelTests.cs`
- Modify: `src/AiChromeProxy.Server/Hosting/DataDirectoryHosting.cs`, `src/AiChromeProxy.Tray/App.axaml.cs`, `tests/AiChromeProxy.Tests/Server/DataDirectoryHostingTests.cs`

Every code block below is the **complete final content** of that file (verified on the prototype: builds with 0 warnings, gate passes). Write it exactly; for modified files replace the whole file.

- [ ] **Step 1: Write the tests**

Write `tests/AiChromeProxy.Tests/Server/DataDirectoryHostingTests.cs`:

```csharp
using System.Text.Json;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Server.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiChromeProxy.Tests.Server;

public sealed class DataDirectoryHostingTests : IDisposable
{
	private readonly DataDirectory _dataDir = new(Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N")));

	[Fact]
	public void Select_NotServiceNoOverride_NoDataDirectory()
	{
		Assert.Null(DataDirectoryHosting.Select(isWindowsService: false, overrideValue: null));
		Assert.Null(DataDirectoryHosting.Select(isWindowsService: false, overrideValue: " "));
	}

	[Fact]
	public void Select_Service_ProgramData()
	{
		Assert.Equal(DataDirectory.Resolve(null), DataDirectoryHosting.Select(isWindowsService: true, overrideValue: null));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Select_Override_WinsInBothModes(bool isService)
	{
		Assert.Equal(@"D:\aicp", DataDirectoryHosting.Select(isService, @"D:\aicp")?.Root);
	}

	[Fact]
	public void PersistentSettings_OverrideAppSettings_EnvVarsOverrideThem()
	{
		var key = "AicpTests_" + Guid.NewGuid().ToString("N");
		WriteSettings(new { Server = new { Port = 6100 }, AicpTests = new Dictionary<string, string> { [key] = "file", [key + "_FileOnly"] = "file" } });
		Environment.SetEnvironmentVariable($"AicpTests__{key}", "env");
		try
		{
			var builder = WebApplication.CreateBuilder();

			builder.Configuration.AddPersistentSettings(_dataDir);

			Assert.Equal("6100", builder.Configuration["Server:Port"]);
			Assert.Equal("env", builder.Configuration[$"AicpTests:{key}"]);
			Assert.Equal("file", builder.Configuration[$"AicpTests:{key}_FileOnly"]);
		}
		finally
		{
			Environment.SetEnvironmentVariable($"AicpTests__{key}", null);
		}
	}

	[Fact]
	public void PersistentSettings_MissingFileAndFolder_Ignored()
	{
		var config = new ConfigurationManager();
		config.AddInMemoryCollection(new Dictionary<string, string?> { ["Server:Port"] = "5180" });

		config.AddPersistentSettings(_dataDir);

		Assert.Equal("5180", config["Server:Port"]);
	}

	[Fact]
	public void PersistentSettings_NoEnvironmentSource_Appended()
	{
		WriteSettings(new { Server = new { Port = 6200 } });
		var config = new ConfigurationManager();
		config.AddInMemoryCollection(new Dictionary<string, string?> { ["Server:Port"] = "5180" });

		config.AddPersistentSettings(_dataDir);

		Assert.Equal("6200", config["Server:Port"]);
	}

	[Fact]
	public void Logging_WithDataDirectory_WritesDailyClefFile()
	{
		var config = new ConfigurationBuilder().Build();
		using (var provider = new ServiceCollection().AddServerLogging(config, _dataDir).BuildServiceProvider())
		{
			provider.GetRequiredService<ILoggerFactory>().CreateLogger("Test").LogWarning("Hello {Name}", "clef");
		}

		var file = Assert.Single(Directory.GetFiles(_dataDir.Logs));
		Assert.Matches(@"server-\d{8}\.clef$", file);
		using var json = JsonDocument.Parse(File.ReadAllLines(file).Single());
		Assert.Equal("Hello \"clef\"", json.RootElement.GetProperty("@m").GetString());
		Assert.Equal("Warning", json.RootElement.GetProperty("@l").GetString());
		Assert.Equal("clef", json.RootElement.GetProperty("Name").GetString());
	}

	[Fact]
	public void Logging_WithoutDataDirectory_NoFiles()
	{
		var config = new ConfigurationBuilder().Build();
		using (var provider = new ServiceCollection().AddServerLogging(config, dataDir: null).BuildServiceProvider())
		{
			provider.GetRequiredService<ILoggerFactory>().CreateLogger("Test").LogWarning("console only");
		}

		Assert.False(Directory.Exists(_dataDir.Root));
	}

	public void Dispose()
	{
		if (Directory.Exists(_dataDir.Root))
		{
			Directory.Delete(_dataDir.Root, recursive: true);
		}
	}

	private void WriteSettings(object settings)
	{
		Directory.CreateDirectory(_dataDir.Root);
		File.WriteAllText(_dataDir.SettingsFile, JsonSerializer.Serialize(settings));
	}
}
```

Write `tests/AiChromeProxy.Tests/Tray/ClefTests.cs`:

```csharp
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Server.Hosting;
using AiChromeProxy.Tray.Clef;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiChromeProxy.Tests.Tray;

public sealed class ClefTests : IDisposable
{
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N"));

	public ClefTests() => Directory.CreateDirectory(_dir);

	[Fact]
	public void Parse_RenderedLine()
	{
		var entry = ClefParser.Parse("""{"@t":"2026-10-02T10:00:00.1234567Z","@m":"Now listening on: \"http://127.0.0.1:5180\"","@i":"a1b2c3d4","Address":"http://127.0.0.1:5180"}""");

		Assert.NotNull(entry);
		Assert.Equal(new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero).AddTicks(1234567), entry.Timestamp);
		Assert.Equal(ClefLevel.Information, entry.Level);
		Assert.Equal("Now listening on: \"http://127.0.0.1:5180\"", entry.Message);
		Assert.Null(entry.Exception);
	}

	[Fact]
	public void Parse_LevelAndException()
	{
		var entry = ClefParser.Parse("""{"@t":"2026-10-02T10:00:00Z","@m":"Boom","@l":"Error","@x":"System.Exception: boom\r\n   at X"}""");

		Assert.Equal(ClefLevel.Error, entry?.Level);
		Assert.Equal("System.Exception: boom\r\n   at X", entry?.Exception);
	}

	[Fact]
	public void Parse_TemplateOnly_FallsBackToTemplate()
	{
		Assert.Equal("Hello {Name}", ClefParser.Parse("""{"@t":"2026-10-02T10:00:00Z","@mt":"Hello {Name}","Name":"x"}""")?.Message);
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("{ not json")]
	[InlineData("[1,2]")]
	[InlineData("""{"@m":"no timestamp"}""")]
	[InlineData("""{"@t":42,"@m":"numeric timestamp"}""")]
	[InlineData("""{"@t":"yesterday","@m":"bad timestamp"}""")]
	public void Parse_Unusable_Null(string line)
	{
		Assert.Null(ClefParser.Parse(line));
	}

	[Fact]
	public void Parse_UnknownLevel_Information()
	{
		Assert.Equal(ClefLevel.Information, ClefParser.Parse("""{"@t":"2026-10-02T10:00:00Z","@m":"x","@l":"Chatty"}""")?.Level);
	}

	[Theory]
	[InlineData(ClefLevel.Warning, null, true)]
	[InlineData(ClefLevel.Warning, "", true)]
	[InlineData(ClefLevel.Error, null, false)]
	[InlineData(ClefLevel.Verbose, "DISK", true)]
	[InlineData(ClefLevel.Verbose, " ioexception ", true)]
	[InlineData(ClefLevel.Verbose, "network", false)]
	public void Matches_LevelAndText(ClefLevel minimum, string? text, bool expected)
	{
		var entry = new LogEntry(DateTimeOffset.UnixEpoch, ClefLevel.Warning, "Disk almost full", "System.IO.IOException: no space");

		Assert.Equal(expected, entry.Matches(minimum, text));
	}

	[Fact]
	public void Tail_ReadsCompleteLinesOnly_ThenTheRest()
	{
		var path = Path.Combine(_dir, "server-20261002.clef");
		File.WriteAllText(path, Line("one") + Line("two") + """{"@t":"2026-10-02T10:00:00Z","@m":"thr""");
		var tail = new ClefTail(path);

		Assert.Equal(["one", "two"], tail.ReadNew().Select(e => e.Message));
		Assert.Empty(tail.ReadNew());

		File.AppendAllText(path, "ee\"}\n" + Line("four"));
		Assert.Equal(["three", "four"], tail.ReadNew().Select(e => e.Message));
	}

	[Fact]
	public void Tail_CrLfAndGarbageLines_Skipped()
	{
		var path = Path.Combine(_dir, "server-20261002.clef");
		File.WriteAllText(path, Line("one").Replace("\n", "\r\n") + "garbage\n\n" + Line("two"));

		Assert.Equal(["one", "two"], new ClefTail(path).ReadNew().Select(e => e.Message));
	}

	[Fact]
	public void Tail_WhileServerSerilogSinkHoldsTheFile_SharedRead()
	{
		var dataDir = new DataDirectory(_dir);
		using var provider = new ServiceCollection().AddServerLogging(new ConfigurationBuilder().Build(), dataDir).BuildServiceProvider();
		var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Test");
		logger.LogWarning("first {N}", 1);
		var tail = new ClefTail(Assert.Single(Directory.GetFiles(dataDir.Logs)));

		var first = Assert.Single(tail.ReadNew());
		logger.LogError(new InvalidOperationException("bad"), "second {N}", 2);
		var second = Assert.Single(tail.ReadNew());

		Assert.Equal((ClefLevel.Warning, "first 1"), (first.Level, first.Message));
		Assert.Equal((ClefLevel.Error, "second 2"), (second.Level, second.Message));
		Assert.Contains("InvalidOperationException: bad", second.Exception);
	}

	public void Dispose() => Directory.Delete(_dir, recursive: true);

	private static string Line(string message) => $$"""{"@t":"2026-10-02T10:00:00Z","@m":"{{message}}"}""" + "\n";
}
```

Write `tests/AiChromeProxy.Tests/Tray/LogsViewModelTests.cs`:

```csharp
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tray.Clef;
using AiChromeProxy.Tray.ViewModels;

namespace AiChromeProxy.Tests.Tray;

public sealed class LogsViewModelTests : IDisposable
{
	private readonly DataDirectory _dataDir = new(Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N")));

	[Fact]
	public void NoLogs_ExplainsWhere()
	{
		var vm = new LogsViewModel(_dataDir);

		Assert.Empty(vm.Files);
		Assert.Null(vm.SelectedFile);
		Assert.Equal($"No log files yet in {_dataDir.Logs}.", vm.Error);
	}

	[Fact]
	public void Files_NewestFirst_NewestSelectedAndLoaded()
	{
		Write("server-20261001.clef", ("Information", "old"));
		Write("server-20261002.clef", ("Information", "new"));

		var vm = new LogsViewModel(_dataDir);

		Assert.Equal(["server-20261002.clef", "server-20261001.clef"], vm.Files);
		Assert.Equal("server-20261002.clef", vm.SelectedFile);
		Assert.Equal(["new"], Messages(vm));
	}

	[Fact]
	public void SelectOlderFile_LoadsIt()
	{
		Write("server-20261001.clef", ("Information", "old"));
		Write("server-20261002.clef", ("Information", "new"));
		var vm = new LogsViewModel(_dataDir);

		vm.SelectedFile = "server-20261001.clef";

		Assert.Equal(["old"], Messages(vm));
	}

	[Fact]
	public void Filters_LevelAndText()
	{
		Write("server-20261002.clef", ("Debug", "noise"), ("Information", "Now listening"), ("Warning", "Disk low"), ("Error", "Disk failed"));
		var vm = new LogsViewModel(_dataDir);
		Assert.Equal(["Now listening", "Disk low", "Disk failed"], Messages(vm));

		vm.MinimumLevel = ClefLevel.Warning;
		Assert.Equal(["Disk low", "Disk failed"], Messages(vm));

		vm.FilterText = "failed";
		Assert.Equal(["Disk failed"], Messages(vm));

		vm.MinimumLevel = ClefLevel.Verbose;
		vm.FilterText = string.Empty;
		Assert.Equal(["noise", "Now listening", "Disk low", "Disk failed"], Messages(vm));
	}

	[Fact]
	public void Follow_AppendsNewEntries_ThroughTheFilter()
	{
		Write("server-20261002.clef", ("Information", "one"));
		var vm = new LogsViewModel(_dataDir) { MinimumLevel = ClefLevel.Information };

		Append("server-20261002.clef", ("Debug", "hidden"), ("Warning", "two"));
		vm.Poll();

		Assert.Equal(["one", "two"], Messages(vm));
	}

	[Fact]
	public void FollowOff_PollDoesNothing()
	{
		Write("server-20261002.clef", ("Information", "one"));
		var vm = new LogsViewModel(_dataDir) { Follow = false };

		Append("server-20261002.clef", ("Information", "two"));
		vm.Poll();

		Assert.Equal(["one"], Messages(vm));
	}

	[Fact]
	public void Follow_DailyRollOver_SwitchesToNewFile_KeepsList()
	{
		Write("server-20261002.clef", ("Information", "yesterday"));
		var vm = new LogsViewModel(_dataDir);

		Write("server-20261003.clef", ("Information", "today"));
		vm.Poll();

		Assert.Equal("server-20261003.clef", vm.SelectedFile);
		Assert.Equal(["server-20261003.clef", "server-20261002.clef"], vm.Files);
		Assert.Equal(["today"], Messages(vm));
	}

	[Fact]
	public void SelectedFileDeleted_ErrorShown_FileDropsOutOfList()
	{
		Write("server-20261001.clef", ("Information", "old"));
		Write("server-20261002.clef", ("Information", "new"));
		var vm = new LogsViewModel(_dataDir) { SelectedFile = "server-20261001.clef" };

		File.Delete(Path.Combine(_dataDir.Logs, "server-20261001.clef"));
		vm.Follow = false;
		vm.SelectedFile = "server-20261002.clef";
		vm.SelectedFile = "server-20261001.clef";

		Assert.NotNull(vm.Error);
		Assert.Empty(vm.Entries);
		vm.Follow = true;
		vm.Poll();
		Assert.Equal(["server-20261002.clef"], vm.Files);
	}

	public void Dispose()
	{
		if (Directory.Exists(_dataDir.Root))
		{
			Directory.Delete(_dataDir.Root, recursive: true);
		}
	}

	private static List<string> Messages(LogsViewModel vm) => [.. vm.Entries.Select(e => e.Message)];

	private void Write(string file, params (string Level, string Message)[] entries)
	{
		Directory.CreateDirectory(_dataDir.Logs);
		File.WriteAllText(Path.Combine(_dataDir.Logs, file), string.Empty);
		Append(file, entries);
	}

	private void Append(string file, params (string Level, string Message)[] entries)
	{
		var lines = entries.Select(e => $$"""{"@t":"2026-10-02T10:00:00Z","@l":"{{e.Level}}","@m":"{{e.Message}}"}""" + "\n");
		File.AppendAllText(Path.Combine(_dataDir.Logs, file), string.Concat(lines));
	}
}
```

- [ ] **Step 2: Run to verify they fail (RED)**

Run: `dotnet build -c Release`
Expected: FAIL — compile errors for the types/members this task introduces (or, where the tests only extend existing types, failing assertions in the next step). Record the first errors in your report.

- [ ] **Step 3: Implement**

Write `src/AiChromeProxy.Server/Hosting/DataDirectoryHosting.cs`:

```csharp
using AiChromeProxy.Infrastructure.Hosting;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Serilog;
using Serilog.Formatting.Compact;

namespace AiChromeProxy.Server.Hosting;

/// <summary>Wires the machine data directory into the host: persistent settings and CLEF log files.</summary>
public static class DataDirectoryHosting
{
	public const string LogFilePattern = "server-.clef";
	public const int RetainedLogFiles = 14;

	/// <summary>
	/// The data directory is used only by the Windows service or when <c>AICP_DATA_DIR</c> is set,
	/// so a developer's machine config never leaks into <c>dotnet run</c> or tests.
	/// </summary>
	/// <returns>The directory, or null when the Server runs without one.</returns>
	public static DataDirectory? Select(bool isWindowsService, string? overrideValue) =>
		isWindowsService || !string.IsNullOrWhiteSpace(overrideValue) ? DataDirectory.Resolve(overrideValue) : null;

	/// <summary>Adds the optional <c>&lt;DataDir&gt;\appsettings.json</c> just below environment variables: env vars and the command line still win.</summary>
	public static void AddPersistentSettings(this IConfigurationBuilder config, DataDirectory dataDir)
	{
		var source = new JsonConfigurationSource { Path = dataDir.SettingsFile, Optional = true };
		source.ResolveFileProvider();
		var envIndex = config.Sources.ToList().FindLastIndex(s => s is EnvironmentVariablesConfigurationSource);
		config.Sources.Insert(envIndex < 0 ? config.Sources.Count : envIndex, source);
	}

	/// <summary>Serilog (levels from the <c>Serilog</c> section): console always; daily CLEF files (rendered @m, so the tray needs no template renderer) in <c>&lt;DataDir&gt;\logs</c> when a data directory is in use.</summary>
	public static IServiceCollection AddServerLogging(this IServiceCollection services, IConfiguration configuration, DataDirectory? dataDir)
	{
		var log = new LoggerConfiguration().ReadFrom.Configuration(configuration).Enrich.FromLogContext().WriteTo.Console();
		if (dataDir is not null)
		{
			log.WriteTo.File(
				new RenderedCompactJsonFormatter(),
				Path.Combine(dataDir.Logs, LogFilePattern),
				rollingInterval: RollingInterval.Day,
				retainedFileCountLimit: RetainedLogFiles);
		}

		// Owned by the container (dispose: true) and never assigned to the static Log.Logger: parallel test hosts stay isolated.
		return services.AddSerilog(log.CreateLogger(), dispose: true);
	}
}
```

Write `src/AiChromeProxy.Tray/App.axaml.cs`:

```csharp
using System.Diagnostics;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.ViewModels;
using AiChromeProxy.Tray.Views;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;

namespace AiChromeProxy.Tray;

public partial class App : Avalonia.Application
{
	private static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(2);

	public override void Initialize() => AvaloniaXamlLoader.Load(this);

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			StartTray(desktop);
		}

		base.OnFrameworkInitializationCompleted();
	}

	private static NativeMenuItem Item(string header, Action onClick)
	{
		var item = new NativeMenuItem(header);
		item.Click += (_, _) => onClick();
		return item;
	}

	/// <summary>One window per kind: a second click brings the open one to front.</summary>
	private static void ShowSingle<TWindow>(IClassicDesktopStyleApplicationLifetime desktop, Func<TWindow> create)
		where TWindow : Window
	{
		var window = desktop.Windows.OfType<TWindow>().FirstOrDefault() ?? create();
		window.Show();
		window.Activate();
	}

	private static void Open(Uri address) => Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true })?.Dispose();

	private void StartTray(IClassicDesktopStyleApplicationLifetime desktop)
	{
		var dataDir = DataDirectory.FromEnvironment();
		var service = new WindowsServiceControl();
		var vm = new TrayViewModel(service);
		var status = new NativeMenuItem { IsEnabled = false };
		var error = new NativeMenuItem { IsEnabled = false };
		var menu = new NativeMenu
		{
			status,
			error,
			new NativeMenuItemSeparator(),
			new NativeMenuItem("Start") { Command = vm.StartCommand },
			new NativeMenuItem("Stop") { Command = vm.StopCommand },
			new NativeMenuItem("Restart") { Command = vm.RestartCommand },
			new NativeMenuItemSeparator(),
			Item("Settings…", () => ShowSingle(desktop, () => new SettingsWindow { DataContext = new SettingsViewModel(dataDir, new RegistryAutoStart(), service) })),
			Item("Logs…", () => ShowSingle(desktop, () => new LogsWindow { DataContext = new LogsViewModel(dataDir) })),
			Item("Open UI", () => Open(SettingsViewModel.UiAddress(dataDir))),
			new NativeMenuItemSeparator(),
			Item("Exit", () => desktop.Shutdown()),
		};

		var icon = new TrayIcon
		{
			Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://AiChromeProxy.Tray/Assets/tray.ico"))),
			Menu = menu,
		};

		void Render()
		{
			status.Header = vm.StatusText;
			icon.ToolTipText = "AI Chrome Proxy: " + vm.StatusText;
			error.Header = vm.Error;
			error.IsVisible = vm.Error is not null;
		}

		vm.PropertyChanged += (_, _) => Render();
		vm.Refresh();
		Render();
		TrayIcon.SetIcons(this, [icon]);

		// ponytail: polls the SCM every 2 s for the tray's lifetime (one cheap query); restrict to "menu open" via NativeMenu.Opening/Closed if it ever matters.
		DispatcherTimer.Run(
			() =>
			{
				vm.Refresh();
				return true;
			},
			StatusPollInterval);
	}
}
```

Write `src/AiChromeProxy.Tray/Clef/ClefLevel.cs`:

```csharp
namespace AiChromeProxy.Tray.Clef;

/// <summary>Serilog levels as written to CLEF <c>@l</c> (absent means Information).</summary>
public enum ClefLevel
{
	Verbose,
	Debug,
	Information,
	Warning,
	Error,
	Fatal,
}
```

Write `src/AiChromeProxy.Tray/Clef/ClefParser.cs`:

```csharp
using System.Text.Json;

namespace AiChromeProxy.Tray.Clef;

/// <summary>Parses one CLEF line (compact JSON, as written by the Server's Serilog file sink).</summary>
public static class ClefParser
{
	/// <returns>The entry, or null for a blank, malformed or timestamp-less line.</returns>
	public static LogEntry? Parse(string line)
	{
		if (string.IsNullOrWhiteSpace(line))
		{
			return null;
		}

		try
		{
			using var json = JsonDocument.Parse(line);
			var root = json.RootElement;
			if (root.ValueKind != JsonValueKind.Object
				|| !root.TryGetProperty("@t", out var t)
				|| t.ValueKind != JsonValueKind.String
				|| !t.TryGetDateTimeOffset(out var timestamp))
			{
				return null;
			}

			var level = String(root, "@l") is { } name && Enum.TryParse<ClefLevel>(name, ignoreCase: true, out var parsed) ? parsed : ClefLevel.Information;

			// @m: rendered message (RenderedCompactJsonFormatter); @mt: template only (CompactJsonFormatter).
			return new LogEntry(timestamp, level, String(root, "@m") ?? String(root, "@mt") ?? string.Empty, String(root, "@x"));
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static string? String(JsonElement root, string name) =>
		root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
```

Write `src/AiChromeProxy.Tray/Clef/ClefTail.cs`:

```csharp
using System.Text;

namespace AiChromeProxy.Tray.Clef;

/// <summary>Reads a CLEF file incrementally while the Server keeps writing it (shared read; a trailing partial line waits for its newline).</summary>
public sealed class ClefTail(string path)
{
	private long _position;

	public string Path { get; } = path;

	/// <returns>Entries completed since the previous call (all of them on the first call).</returns>
	public IReadOnlyList<LogEntry> ReadNew()
	{
		using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		if (stream.Length <= _position)
		{
			return [];
		}

		// ponytail: reads the whole unread tail into memory; fine for daily files kept 14 days, stream it if files grow to hundreds of MB.
		var bytes = new byte[stream.Length - _position];
		stream.Position = _position;
		stream.ReadExactly(bytes);

		var end = Array.LastIndexOf(bytes, (byte)'\n');
		if (end < 0)
		{
			return [];
		}

		_position += end + 1;
		return Encoding.UTF8.GetString(bytes, 0, end + 1)
			.Split('\n')
			.Select(line => ClefParser.Parse(line.TrimEnd('\r')))
			.OfType<LogEntry>()
			.ToList();
	}
}
```

Write `src/AiChromeProxy.Tray/Clef/LogEntry.cs`:

```csharp
using System.Globalization;

namespace AiChromeProxy.Tray.Clef;

public sealed record LogEntry(DateTimeOffset Timestamp, ClefLevel Level, string Message, string? Exception)
{
	public string Time => Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

	/// <summary>Logs window filter: at least <paramref name="minimum"/>, and <paramref name="text"/> (if any) in the message or exception, case-insensitive.</summary>
	public bool Matches(ClefLevel minimum, string? text) =>
		Level >= minimum
		&& (string.IsNullOrWhiteSpace(text)
			|| Message.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase)
			|| (Exception?.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase) ?? false));
}
```

Write `src/AiChromeProxy.Tray/ViewModels/LogsViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tray.Clef;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiChromeProxy.Tray.ViewModels;

/// <summary>Logs window: CLEF files in <c>&lt;DataDir&gt;\logs</c> (newest first), level/text filters and Follow (tail the current file).</summary>
public sealed partial class LogsViewModel : ObservableObject
{
	private readonly string _logsDir;
	private readonly List<LogEntry> _all = [];
	private ClefTail? _tail;

	public LogsViewModel(DataDirectory dataDir)
	{
		_logsDir = dataDir.Logs;
		RefreshFiles();
		SelectedFile = Files.FirstOrDefault();
		if (SelectedFile is null)
		{
			Error = $"No log files yet in {_logsDir}.";
		}
	}

	/// <summary>File names, newest first.</summary>
	public ObservableCollection<string> Files { get; } = [];

	public IReadOnlyList<ClefLevel> Levels { get; } = Enum.GetValues<ClefLevel>();

	/// <summary>Entries of the selected file that pass the filters.</summary>
	[ObservableProperty]
	public partial ObservableCollection<LogEntry> Entries { get; private set; } = [];

	[ObservableProperty]
	public partial string? SelectedFile { get; set; }

	[ObservableProperty]
	public partial ClefLevel MinimumLevel { get; set; } = ClefLevel.Information;

	[ObservableProperty]
	public partial string FilterText { get; set; } = string.Empty;

	/// <summary>While on, <see cref="Poll"/> appends new entries and jumps to a newer file (daily roll-over).</summary>
	[ObservableProperty]
	public partial bool Follow { get; set; } = true;

	[ObservableProperty]
	public partial string? Error { get; private set; }

	/// <summary>Called by the window once a second.</summary>
	public void Poll()
	{
		if (!Follow)
		{
			return;
		}

		RefreshFiles();
		if (Files.FirstOrDefault() is { } newest && newest != SelectedFile)
		{
			SelectedFile = newest;
			return;
		}

		Append(ReadNew());
	}

	partial void OnSelectedFileChanged(string? value)
	{
		_all.Clear();
		Entries = [];
		Error = null;
		_tail = value is null ? null : new ClefTail(Path.Combine(_logsDir, value));
		Append(ReadNew());
	}

	partial void OnMinimumLevelChanged(ClefLevel value) => Entries = [.. _all.Where(Visible)];

	partial void OnFilterTextChanged(string value) => Entries = [.. _all.Where(Visible)];

	private bool Visible(LogEntry entry) => entry.Matches(MinimumLevel, FilterText);

	private IReadOnlyList<LogEntry> ReadNew()
	{
		try
		{
			return _tail?.ReadNew() ?? [];
		}
		catch (IOException ex)
		{
			Error = ex.Message;
			return [];
		}
	}

	private void Append(IReadOnlyList<LogEntry> entries)
	{
		_all.AddRange(entries);
		foreach (var entry in entries.Where(Visible))
		{
			Entries.Add(entry);
		}
	}

	/// <summary>Syncs <see cref="Files"/> in place (no reset), so the bound selection survives a refresh.</summary>
	private void RefreshFiles()
	{
		var names = Directory.Exists(_logsDir)
			? Directory.GetFiles(_logsDir, "*.clef").Select(Path.GetFileName).OfType<string>().OrderDescending(StringComparer.OrdinalIgnoreCase).ToList()
			: [];
		foreach (var gone in Files.Except(names).ToList())
		{
			Files.Remove(gone);
		}

		for (var i = 0; i < names.Count; i++)
		{
			if (i >= Files.Count || Files[i] != names[i])
			{
				Files.Insert(i, names[i]);
			}
		}
	}
}
```

Write `src/AiChromeProxy.Tray/Views/LogsWindow.axaml`:

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:AiChromeProxy.Tray.ViewModels"
        xmlns:clef="using:AiChromeProxy.Tray.Clef"
        x:Class="AiChromeProxy.Tray.Views.LogsWindow"
        x:DataType="vm:LogsViewModel"
        Title="AI Chrome Proxy logs"
        Icon="/Assets/tray.ico"
        Width="1100" Height="650"
        WindowStartupLocation="CenterScreen">
  <DockPanel Margin="8">
    <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Spacing="8" Margin="0,0,0,8">
      <ComboBox ItemsSource="{Binding Files}" SelectedItem="{Binding SelectedFile}" Width="240" />
      <ComboBox ItemsSource="{Binding Levels}" SelectedItem="{Binding MinimumLevel}" Width="140" />
      <TextBox Text="{Binding FilterText}" PlaceholderText="Filter text" Width="320" />
      <CheckBox Content="Follow" IsChecked="{Binding Follow}" />
    </StackPanel>
    <TextBlock DockPanel.Dock="Top" Text="{Binding Error}" Foreground="#C42B1C" TextWrapping="Wrap"
               IsVisible="{Binding Error, Converter={x:Static ObjectConverters.IsNotNull}}" />
    <ListBox x:Name="EntryList" ItemsSource="{Binding Entries}">
      <ListBox.ItemTemplate>
        <DataTemplate x:DataType="clef:LogEntry">
          <Grid ColumnDefinitions="190,90,*" RowDefinitions="Auto,Auto">
            <TextBlock Grid.Column="0" Text="{Binding Time}" FontFamily="Consolas" />
            <TextBlock Grid.Column="1" Text="{Binding Level}" />
            <TextBlock Grid.Column="2" Text="{Binding Message}" TextWrapping="Wrap" />
            <TextBlock Grid.Row="1" Grid.Column="2" Text="{Binding Exception}" FontFamily="Consolas" Foreground="#C42B1C" TextWrapping="Wrap"
                       IsVisible="{Binding Exception, Converter={x:Static ObjectConverters.IsNotNull}}" />
          </Grid>
        </DataTemplate>
      </ListBox.ItemTemplate>
    </ListBox>
  </DockPanel>
</Window>
```

Write `src/AiChromeProxy.Tray/Views/LogsWindow.axaml.cs`:

```csharp
using AiChromeProxy.Tray.ViewModels;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AiChromeProxy.Tray.Views;

public partial class LogsWindow : Window
{
	private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };

	public LogsWindow()
	{
		InitializeComponent();
		_poll.Tick += (_, _) =>
		{
			if (DataContext is LogsViewModel { Follow: true } vm)
			{
				vm.Poll();
				if (vm.Entries.Count > 0)
				{
					EntryList.ScrollIntoView(vm.Entries[^1]);
				}
			}
		};
		Opened += (_, _) => _poll.Start();
		Closed += (_, _) => _poll.Stop();
	}
}
```

- [ ] **Step 4: Build and run the gate (GREEN)**

Run: `dotnet build -c Release` → Expected: `0 Warning(s)`, `0 Error(s)`.
Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total` then `echo $?` → Expected: `total: 159, failed: 0`, exit code 0.

- [ ] **Step 5: Commit**

```bash
git add -A
git status --short   # every file listed above must appear; nothing under a Logs/ folder
git commit -m "feat(tray): logs window with CLEF parser, filters and follow"
```

---

### Task 8: feat(tray): elevated service install/uninstall

**Files:**
- Create: `src/AiChromeProxy.Tray/Services/AdminCommand.cs`, `src/AiChromeProxy.Tray/Services/ServiceInstaller.cs`, `src/AiChromeProxy.Tray/Services/ServiceSetup.cs`, `src/AiChromeProxy.Tray/ViewModels/InstallViewModel.cs`, `src/AiChromeProxy.Tray/Views/InstallWindow.axaml`, `src/AiChromeProxy.Tray/Views/InstallWindow.axaml.cs`, `tests/AiChromeProxy.Tests/Tray/ServiceSetupTests.cs`
- Modify: `src/AiChromeProxy.Tray/App.axaml.cs`, `src/AiChromeProxy.Tray/Program.cs`, `src/AiChromeProxy.Tray/Services/IServiceControl.cs`, `src/AiChromeProxy.Tray/Services/WindowsServiceControl.cs`, `src/AiChromeProxy.Tray/ViewModels/TrayViewModel.cs`, `tests/AiChromeProxy.Tests/Tray/FakeServiceControl.cs`, `tests/AiChromeProxy.Tests/Tray/TrayViewModelTests.cs`

Every code block below is the **complete final content** of that file (verified on the prototype: builds with 0 warnings, gate passes). Write it exactly; for modified files replace the whole file.

- [ ] **Step 1: Write the tests**

Write `tests/AiChromeProxy.Tests/Tray/FakeServiceControl.cs`:

```csharp
using AiChromeProxy.Tray.Services;

namespace AiChromeProxy.Tests.Tray;

/// <summary>In-memory service: records the call order and can be told to fail.</summary>
public sealed class FakeServiceControl(ServiceState state = ServiceState.Running) : IServiceControl
{
	public ServiceState State { get; set; } = state;

	public List<string> Calls { get; } = [];

	public Exception? FailStart { get; set; }

	public Exception? FailStop { get; set; }

	public Exception? FailGetState { get; set; }

	public Exception? FailInstall { get; set; }

	public Exception? FailUninstall { get; set; }

	public ServiceState GetState() => FailGetState is null ? State : throw FailGetState;

	public Task StartAsync(CancellationToken ct)
	{
		Calls.Add("start");
		if (FailStart is not null)
		{
			throw FailStart;
		}

		State = ServiceState.Running;
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken ct)
	{
		Calls.Add("stop");
		if (FailStop is not null)
		{
			throw FailStop;
		}

		State = ServiceState.Stopped;
		return Task.CompletedTask;
	}

	public void Install(string account, string password, string controlUser)
	{
		Calls.Add($"install {account} {password} {controlUser}");
		if (FailInstall is not null)
		{
			throw FailInstall;
		}

		State = ServiceState.Running;
	}

	public void Uninstall()
	{
		Calls.Add("uninstall");
		if (FailUninstall is not null)
		{
			throw FailUninstall;
		}

		State = ServiceState.NotInstalled;
	}
}
```

Write `tests/AiChromeProxy.Tests/Tray/ServiceSetupTests.cs`:

```csharp
using System.Security.AccessControl;
using System.Security.Principal;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.ViewModels;

namespace AiChromeProxy.Tests.Tray;

public sealed class ServiceSetupTests
{
	// Typical default service DACL: SYSTEM, Administrators, interactive users (query only).
	private const string DefaultServiceSddl = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)";

	private static readonly SecurityIdentifier User = new("S-1-5-21-1000000000-2000000000-3000000000-1001");

	[Fact]
	public void ServerExecutable_ServerFolderNextToTray_QuotedForTheScm()
	{
		var exe = ServiceSetup.ServerExecutable(@"C:\Users\Jane Doe\AppData\Local\AiChromeProxy\current\");

		Assert.Equal(@"C:\Users\Jane Doe\AppData\Local\AiChromeProxy\current\server\AiChromeProxy.Server.exe", exe);
		Assert.Equal($"\"{exe}\"", ServiceSetup.BinaryPathName(exe));
	}

	[Theory]
	[InlineData(@"HOMESERVER\jane", "HOMESERVER", "jane", @"HOMESERVER\jane")]
	[InlineData(@" CORP\jane.doe ", "CORP", "jane.doe", @"CORP\jane.doe")]
	[InlineData("jane", ".", "jane", @".\jane")]
	public void Account_SplitAndServiceStartName(string account, string domain, string user, string startName)
	{
		Assert.Equal((domain, user), ServiceSetup.SplitAccount(account));
		Assert.Equal(startName, ServiceSetup.ServiceStartName(account));
	}

	[Fact]
	public void Sid_CurrentUser_BothNameForms()
	{
		var current = WindowsIdentity.GetCurrent();
		var bareName = current.Name[(current.Name.IndexOf('\\', StringComparison.Ordinal) + 1)..];

		Assert.Equal(current.User, ServiceSetup.Sid(current.Name));
		if (current.Name.StartsWith(Environment.MachineName + "\\", StringComparison.OrdinalIgnoreCase))
		{
			Assert.Equal(current.User, ServiceSetup.Sid(bareName));
		}
	}

	[Fact]
	public void GrantUserControl_AddsAllowAceWithStartStopQuery_KeepsOthers()
	{
		var original = new RawSecurityDescriptor(DefaultServiceSddl);

		var granted = new RawSecurityDescriptor(ServiceSetup.GrantUserControl(Binary(original), User), 0);

		var aces = granted.DiscretionaryAcl!.Cast<CommonAce>().ToList();
		Assert.Equal(original.DiscretionaryAcl!.Count + 1, aces.Count);
		var ace = Assert.Single(aces, a => a.SecurityIdentifier == User);
		Assert.Equal(AceQualifier.AccessAllowed, ace.AceQualifier);
		Assert.Equal(0x0001 | 0x0004 | 0x0010 | 0x0020, ace.AccessMask);
		Assert.Contains(aces, a => a.SecurityIdentifier.IsWellKnown(WellKnownSidType.LocalSystemSid));
		Assert.Contains(aces, a => a.SecurityIdentifier.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
	}

	[Fact]
	public void GrantUserControl_Twice_SingleAce()
	{
		var once = ServiceSetup.GrantUserControl(Binary(new RawSecurityDescriptor(DefaultServiceSddl)), User);

		var twice = new RawSecurityDescriptor(ServiceSetup.GrantUserControl(once, User), 0);

		Assert.Single(twice.DiscretionaryAcl!.Cast<CommonAce>(), a => a.SecurityIdentifier == User);
	}

	[Fact]
	public void PrepareDataDirectory_CreatesLogs_InheritableFullControlForAccount()
	{
		var dataDir = new DataDirectory(Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N")));
		var account = WindowsIdentity.GetCurrent().User!;
		try
		{
			ServiceSetup.PrepareDataDirectory(dataDir, account);

			Assert.True(Directory.Exists(dataDir.Logs));
			var rules = new DirectoryInfo(dataDir.Root).GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
			Assert.Contains(rules, r => r.IdentityReference == account
				&& r.AccessControlType == AccessControlType.Allow
				&& r.FileSystemRights.HasFlag(FileSystemRights.FullControl)
				&& r.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit));
		}
		finally
		{
			Directory.Delete(dataDir.Root, recursive: true);
		}
	}

	[Theory]
	[InlineData(@"--admin|install|HOME\jane", "install", @"HOME\jane")]
	[InlineData(@"--admin|uninstall|HOME\jane", "uninstall", @"HOME\jane")]
	public void AdminCommand_Parse_Valid(string args, string command, string user)
	{
		Assert.Equal((command, user), AdminCommand.Parse(Args(args)));
	}

	[Theory]
	[InlineData("")]
	[InlineData(@"--admin")]
	[InlineData(@"--admin|install")]
	[InlineData(@"--admin|format|HOME\jane")]
	[InlineData(@"--admin|install| ")]
	[InlineData(@"--admin|install|HOME\jane|extra")]
	[InlineData(@"--veloapp-install|1.0.0")]
	public void AdminCommand_Parse_Invalid(string args)
	{
		Assert.Null(AdminCommand.Parse(Args(args)));
	}

	[Fact]
	public void AdminCommand_Arguments_QuoteTheUser_NoPassword()
	{
		Assert.Equal(@"--admin install ""HOME\jane doe""", AdminCommand.Arguments(AdminCommand.Install, @"HOME\jane doe"));
	}

	[Fact]
	public void AdminCommand_RunUninstall_ExitCodes()
	{
		var service = new FakeServiceControl();

		Assert.Equal(0, AdminCommand.RunUninstall(service));
		Assert.Equal(["uninstall"], service.Calls);
		Assert.Equal(1, AdminCommand.RunUninstall(new FakeServiceControl { FailUninstall = new InvalidOperationException("access denied") }));
	}

	[Fact]
	public void InstallViewModel_DefaultsToTrayUser_RequiresPassword()
	{
		var service = new FakeServiceControl(ServiceState.NotInstalled);
		var vm = new InstallViewModel(service, @"HOME\jane");

		vm.InstallCommand.Execute(null);

		Assert.Equal(@"HOME\jane", vm.Account);
		Assert.Equal("Enter the account and its Windows password.", vm.Error);
		Assert.Empty(service.Calls);
		Assert.False(vm.Succeeded);
	}

	[Fact]
	public async Task InstallViewModel_Success_InstallsAsAccount_ControlForTrayUser_ClearsPassword()
	{
		var service = new FakeServiceControl(ServiceState.NotInstalled);
		var vm = new InstallViewModel(service, @"HOME\jane") { Account = @" HOME\svc ", Password = "p@ss word" };

		await vm.InstallCommand.ExecuteAsync(null);

		Assert.Equal([@"install HOME\svc p@ss word HOME\jane"], service.Calls);
		Assert.True(vm.Succeeded);
		Assert.Equal(string.Empty, vm.Password);
		Assert.Null(vm.Error);
		Assert.False(vm.IsBusy);
	}

	[Fact]
	public async Task InstallViewModel_Failure_ShowsMessage_KeepsPasswordForRetry()
	{
		var service = new FakeServiceControl(ServiceState.NotInstalled) { FailInstall = new InvalidOperationException("Wrong password") };
		var vm = new InstallViewModel(service, @"HOME\jane") { Password = "typo" };

		await vm.InstallCommand.ExecuteAsync(null);

		Assert.Equal("Wrong password", vm.Error);
		Assert.False(vm.Succeeded);
		Assert.Equal("typo", vm.Password);
		Assert.True(vm.InstallCommand.CanExecute(null));
	}

	/// <summary>Command line as '|'-separated arguments (attributes cannot hold string arrays as data).</summary>
	private static string[] Args(string joined) => joined.Length == 0 ? [] : joined.Split('|');

	private static byte[] Binary(RawSecurityDescriptor descriptor)
	{
		var bytes = new byte[descriptor.BinaryLength];
		descriptor.GetBinaryForm(bytes, 0);
		return bytes;
	}
}
```

Write `tests/AiChromeProxy.Tests/Tray/TrayViewModelTests.cs`:

```csharp
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.ViewModels;

namespace AiChromeProxy.Tests.Tray;

public sealed class TrayViewModelTests
{
	[Theory]
	[InlineData(ServiceState.NotInstalled, "Service: not installed")]
	[InlineData(ServiceState.Stopped, "Service: stopped")]
	[InlineData(ServiceState.Starting, "Service: starting…")]
	[InlineData(ServiceState.Stopping, "Service: stopping…")]
	[InlineData(ServiceState.Running, "Service: running")]
	public void Refresh_StatusLineFollowsService(ServiceState state, string text)
	{
		var vm = Create(new FakeServiceControl(state));

		vm.Refresh();

		Assert.Equal(text, vm.StatusText);
	}

	[Theory]
	[InlineData(ServiceState.NotInstalled, false, false, false)]
	[InlineData(ServiceState.Stopped, true, false, true)]
	[InlineData(ServiceState.Starting, false, true, true)]
	[InlineData(ServiceState.Stopping, false, false, true)]
	[InlineData(ServiceState.Running, false, true, true)]
	public void Commands_EnabledByState(ServiceState state, bool canStart, bool canStop, bool canUninstall)
	{
		var vm = Create(new FakeServiceControl(state));

		vm.Refresh();

		Assert.Equal(canStart, vm.StartCommand.CanExecute(null));
		Assert.Equal(canStop, vm.StopCommand.CanExecute(null));
		Assert.Equal(canStop, vm.RestartCommand.CanExecute(null));
		Assert.Equal(canUninstall, vm.UninstallCommand.CanExecute(null));
		Assert.True(vm.InstallCommand.CanExecute(null));
	}

	[Fact]
	public async Task Install_RunsElevatedAdminInstall_ThenRefreshes()
	{
		var service = new FakeServiceControl(ServiceState.NotInstalled);
		var elevated = new List<string>();
		var vm = new TrayViewModel(service, command =>
		{
			elevated.Add(command);
			service.State = ServiceState.Running;
			return Task.FromResult<int?>(0);
		});
		vm.Refresh();

		await vm.InstallCommand.ExecuteAsync(null);

		Assert.Equal(["install"], elevated);
		Assert.Equal(ServiceState.Running, vm.State);
		Assert.Null(vm.Error);
	}

	[Fact]
	public async Task Uninstall_ElevatedInstanceFails_ErrorWithExitCode()
	{
		var vm = new TrayViewModel(new FakeServiceControl(ServiceState.Running), _ => Task.FromResult<int?>(1));
		vm.Refresh();

		await vm.UninstallCommand.ExecuteAsync(null);

		Assert.Equal("Service uninstall did not complete (exit code 1).", vm.Error);
	}

	[Fact]
	public async Task Install_UacDeclined_NoError()
	{
		var vm = new TrayViewModel(new FakeServiceControl(ServiceState.NotInstalled), _ => Task.FromResult<int?>(null));

		await vm.InstallCommand.ExecuteAsync(null);

		Assert.Null(vm.Error);
	}

	[Fact]
	public async Task Restart_StopsThenStarts_AndRefreshes()
	{
		var service = new FakeServiceControl(ServiceState.Running);
		var vm = Create(service);
		vm.Refresh();

		await vm.RestartCommand.ExecuteAsync(null);

		Assert.Equal(["stop", "start"], service.Calls);
		Assert.Equal(ServiceState.Running, vm.State);
		Assert.Null(vm.Error);
	}

	[Fact]
	public async Task Stop_StopsService()
	{
		var service = new FakeServiceControl(ServiceState.Running);
		var vm = Create(service);
		vm.Refresh();

		await vm.StopCommand.ExecuteAsync(null);

		Assert.Equal(["stop"], service.Calls);
		Assert.Equal(ServiceState.Stopped, vm.State);
		Assert.True(vm.StartCommand.CanExecute(null));
	}

	[Fact]
	public async Task Start_Fails_ErrorShown_NextActionClearsIt()
	{
		var service = new FakeServiceControl(ServiceState.Stopped) { FailStart = new InvalidOperationException("access denied") };
		var vm = Create(service);
		vm.Refresh();

		await vm.StartCommand.ExecuteAsync(null);
		Assert.Equal("access denied", vm.Error);

		service.FailStart = null;
		await vm.StartCommand.ExecuteAsync(null);
		Assert.Null(vm.Error);
		Assert.Equal(ServiceState.Running, vm.State);
	}

	[Fact]
	public void Refresh_StatusQueryFails_ErrorShown()
	{
		var vm = Create(new FakeServiceControl { FailGetState = new InvalidOperationException("scm down") });

		vm.Refresh();

		Assert.Equal("scm down", vm.Error);
	}

	private static TrayViewModel Create(FakeServiceControl service) => new(service, _ => Task.FromResult<int?>(0));
}
```

- [ ] **Step 2: Run to verify they fail (RED)**

Run: `dotnet build -c Release`
Expected: FAIL — compile errors for the types/members this task introduces (or, where the tests only extend existing types, failing assertions in the next step). Record the first errors in your report.

- [ ] **Step 3: Implement**

Write `src/AiChromeProxy.Tray/App.axaml.cs`:

```csharp
using System.Diagnostics;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.ViewModels;
using AiChromeProxy.Tray.Views;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;

namespace AiChromeProxy.Tray;

public partial class App : Avalonia.Application
{
	private static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(2);

	public override void Initialize() => AvaloniaXamlLoader.Load(this);

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			if (AdminCommand.Parse(desktop.Args) is { Command: AdminCommand.Install } admin)
			{
				ShowInstall(desktop, admin.User);
			}
			else
			{
				StartTray(desktop);
			}
		}

		base.OnFrameworkInitializationCompleted();
	}

	private static NativeMenuItem Item(string header, Action onClick)
	{
		var item = new NativeMenuItem(header);
		item.Click += (_, _) => onClick();
		return item;
	}

	/// <summary>One window per kind: a second click brings the open one to front.</summary>
	private static void ShowSingle<TWindow>(IClassicDesktopStyleApplicationLifetime desktop, Func<TWindow> create)
		where TWindow : Window
	{
		var window = desktop.Windows.OfType<TWindow>().FirstOrDefault() ?? create();
		window.Show();
		window.Activate();
	}

	/// <summary>The elevated <c>--admin install</c> instance: only the password dialog; exit code 0 once installed.</summary>
	private static void ShowInstall(IClassicDesktopStyleApplicationLifetime desktop, string controlUser)
	{
		var vm = new InstallViewModel(new WindowsServiceControl(), controlUser);
		var window = new InstallWindow(vm);
		window.Closed += (_, _) => desktop.Shutdown(vm.Succeeded ? 0 : 1);
		window.Show();
	}

	private static void Open(Uri address) => Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true })?.Dispose();

	private void StartTray(IClassicDesktopStyleApplicationLifetime desktop)
	{
		var dataDir = DataDirectory.FromEnvironment();
		var service = new WindowsServiceControl();
		var vm = new TrayViewModel(service, AdminCommand.RunElevatedAsync);
		var status = new NativeMenuItem { IsEnabled = false };
		var error = new NativeMenuItem { IsEnabled = false };
		var menu = new NativeMenu
		{
			status,
			error,
			new NativeMenuItemSeparator(),
			new NativeMenuItem("Start") { Command = vm.StartCommand },
			new NativeMenuItem("Stop") { Command = vm.StopCommand },
			new NativeMenuItem("Restart") { Command = vm.RestartCommand },
			new NativeMenuItemSeparator(),
			new NativeMenuItem("Install service…") { Command = vm.InstallCommand },
			new NativeMenuItem("Uninstall service") { Command = vm.UninstallCommand },
			new NativeMenuItemSeparator(),
			Item("Settings…", () => ShowSingle(desktop, () => new SettingsWindow { DataContext = new SettingsViewModel(dataDir, new RegistryAutoStart(), service) })),
			Item("Logs…", () => ShowSingle(desktop, () => new LogsWindow { DataContext = new LogsViewModel(dataDir) })),
			Item("Open UI", () => Open(SettingsViewModel.UiAddress(dataDir))),
			new NativeMenuItemSeparator(),
			Item("Exit", () => desktop.Shutdown()),
		};

		var icon = new TrayIcon
		{
			Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://AiChromeProxy.Tray/Assets/tray.ico"))),
			Menu = menu,
		};

		void Render()
		{
			status.Header = vm.StatusText;
			icon.ToolTipText = "AI Chrome Proxy: " + vm.StatusText;
			error.Header = vm.Error;
			error.IsVisible = vm.Error is not null;
		}

		vm.PropertyChanged += (_, _) => Render();
		vm.Refresh();
		Render();
		TrayIcon.SetIcons(this, [icon]);

		// ponytail: polls the SCM every 2 s for the tray's lifetime (one cheap query); restrict to "menu open" via NativeMenu.Opening/Closed if it ever matters.
		DispatcherTimer.Run(
			() =>
			{
				vm.Refresh();
				return true;
			},
			StatusPollInterval);
	}
}
```

Write `src/AiChromeProxy.Tray/Program.cs`:

```csharp
using AiChromeProxy.Tray.Services;
using Avalonia;
using Avalonia.Controls;

namespace AiChromeProxy.Tray;

internal static class Program
{
	[STAThread]
	public static int Main(string[] args)
	{
		switch (AdminCommand.Parse(args)?.Command)
		{
			case AdminCommand.Uninstall:
				return AdminCommand.RunUninstall(new WindowsServiceControl());
			case AdminCommand.Install:
				return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
		}

		// One tray per user session (Start with Windows plus a manual launch must not show two icons).
		using var single = new Mutex(initiallyOwned: true, @"Local\AiChromeProxy.Tray", out var isFirst);
		return isFirst ? BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown) : 0;
	}

	/// <summary>Also used by the Avalonia previewer.</summary>
	public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
```

Write `src/AiChromeProxy.Tray/Services/AdminCommand.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Principal;

namespace AiChromeProxy.Tray.Services;

/// <summary>
/// <c>AiChromeProxy.Tray.exe --admin install|uninstall "DOMAIN\user"</c>: the only elevated entry points.
/// The user is the tray's (non-elevated) user, who gets start/stop rights; with over-the-shoulder UAC the elevated
/// identity is a different admin, so it is passed explicitly. The password is typed into the elevated dialog, never passed.
/// </summary>
public static class AdminCommand
{
	public const string Flag = "--admin";
	public const string Install = "install";
	public const string Uninstall = "uninstall";

	private const int ErrorCancelled = 1223;

	public static string CurrentUser => WindowsIdentity.GetCurrent().Name;

	/// <returns>The command and the tray user, or null when <paramref name="args"/> is not an admin command line.</returns>
	public static (string Command, string User)? Parse(IReadOnlyList<string>? args) =>
		args is [Flag, Install or Uninstall, var user] && !string.IsNullOrWhiteSpace(user) ? (args[1], user) : null;

	public static string Arguments(string command, string user) => $"{Flag} {command} \"{user}\"";

	/// <summary>The headless <c>--admin uninstall</c> instance (Velopack's before-uninstall hook and the tray menu).</summary>
	/// <returns>Process exit code: 0 on success.</returns>
	public static int RunUninstall(IServiceControl service)
	{
		try
		{
			service.Uninstall();
			return 0;
		}
		catch (Exception)
		{
			return 1;
		}
	}

	/// <summary>Relaunches this exe elevated (UAC prompt) and waits for it.</summary>
	/// <returns>Its exit code, or null when the user declined the UAC prompt.</returns>
	[ExcludeFromCodeCoverage]
	public static async Task<int?> RunElevatedAsync(string command)
	{
		try
		{
			using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, Arguments(command, CurrentUser))
			{
				UseShellExecute = true,
				Verb = "runas",
			});
			await process!.WaitForExitAsync();
			return process.ExitCode;
		}
		catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
		{
			return null;
		}
	}
}
```

Write `src/AiChromeProxy.Tray/Services/IServiceControl.cs`:

```csharp
namespace AiChromeProxy.Tray.Services;

/// <summary>The Windows service seam: view models and the update orchestrator are unit-tested against a fake.</summary>
public interface IServiceControl
{
	ServiceState GetState();

	/// <summary>Starts the service and waits (up to 30 s) until it reports Running.</summary>
	Task StartAsync(CancellationToken ct);

	/// <summary>Stops the service and waits (up to 30 s) until it reports Stopped.</summary>
	Task StopAsync(CancellationToken ct);

	/// <summary>
	/// Elevated only. Creates (or reconfigures) the service running as <paramref name="account"/>, lets
	/// <paramref name="controlUser"/> start/stop it without UAC, prepares the data directory and starts the service.
	/// </summary>
	void Install(string account, string password, string controlUser);

	/// <summary>Elevated only. Stops and deletes the service; the data directory (settings, logs) is kept.</summary>
	void Uninstall();
}
```

Write `src/AiChromeProxy.Tray/Services/ServiceInstaller.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using AiChromeProxy.Infrastructure.Hosting;
using Microsoft.Win32.SafeHandles;

namespace AiChromeProxy.Tray.Services;

/// <summary>
/// Thin P/Invoke layer (advapi32: SCM, LSA, LogonUser) for the elevated <c>--admin install|uninstall</c> instance.
/// Excluded from coverage: every call needs elevation and changes the machine; the decisions it applies live in
/// <see cref="ServiceSetup"/> (tested) and the calls themselves are covered by the manual acceptance checklist.
/// </summary>
[ExcludeFromCodeCoverage]
internal static partial class ServiceInstaller
{
	private const string SeServiceLogonRight = "SeServiceLogonRight";

	private const uint ScManagerConnect = 0x0001;
	private const uint ScManagerCreateService = 0x0002;
	private const uint ServiceChangeConfig = 0x0002;
	private const uint ServiceQueryStatus = 0x0004;
	private const uint ServiceStart = 0x0010;
	private const uint ServiceStop = 0x0020;
	private const uint Delete = 0x0001_0000;
	private const uint ReadControl = 0x0002_0000;
	private const uint WriteDac = 0x0004_0000;
	private const uint ServiceWin32OwnProcess = 0x0010;
	private const uint ServiceAutoStart = 0x0002;
	private const uint ServiceErrorNormal = 0x0001;
	private const uint ServiceNoChange = 0xFFFF_FFFF;
	private const uint ServiceConfigFailureActions = 2;
	private const uint ScActionRestart = 1;
	private const uint DaclSecurityInformation = 0x0004;
	private const uint Logon32LogonService = 5;
	private const uint Logon32ProviderDefault = 0;
	private const uint PolicyCreateAccount = 0x0010;
	private const uint PolicyLookupNames = 0x0800;
	private const int ErrorInsufficientBuffer = 122;
	private const int ErrorServiceMarkedForDelete = 1072;
	private const int ErrorServiceExists = 1073;
	private const int ErrorServiceDoesNotExist = 1060;
	private const int ErrorLogonFailure = 1326;

	private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

	/// <summary>Creates (or reconfigures) the service to run as <paramref name="account"/>, then starts it.</summary>
	public static void Install(string serviceName, string account, string password, string controlUser, DataDirectory dataDir)
	{
		var accountSid = ServiceSetup.Sid(account);

		// "Log on as a service" first: LogonUser(LOGON32_LOGON_SERVICE) below then fails only for bad credentials.
		GrantLogonAsService(accountSid);
		VerifyPassword(account, password);

		using var manager = OpenSCManager(null, null, ScManagerConnect | ScManagerCreateService);
		ThrowIfInvalid(manager);
		var binaryPath = ServiceSetup.BinaryPathName(ServiceSetup.ServerExecutable(AppContext.BaseDirectory));
		using var service = CreateOrReconfigure(manager, serviceName, binaryPath, ServiceSetup.ServiceStartName(account), password);
		SetFailureActions(service);
		GrantUserControl(service, ServiceSetup.Sid(controlUser));
		ServiceSetup.PrepareDataDirectory(dataDir, accountSid);

		using var controller = new ServiceController(serviceName);
		if (controller.Status == ServiceControllerStatus.Stopped)
		{
			controller.Start();
		}
	}

	/// <summary>Marks the service for deletion and stops it (deletion completes once it has stopped); the data directory is kept.</summary>
	public static void Uninstall(string serviceName)
	{
		using var manager = OpenSCManager(null, null, ScManagerConnect);
		ThrowIfInvalid(manager);
		using var service = OpenService(manager, serviceName, ServiceStop | ServiceQueryStatus | Delete);
		if (service.IsInvalid && Marshal.GetLastPInvokeError() == ErrorServiceDoesNotExist)
		{
			return;
		}

		ThrowIfInvalid(service);
		using var controller = new ServiceController(serviceName);
		if (controller.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
		{
			controller.Stop(stopDependentServices: false);
		}

		// Delete before waiting: if the caller (Velopack's 30 s uninstall hook) is killed, the service still goes away once stopped.
		if (!DeleteService(service) && Marshal.GetLastPInvokeError() != ErrorServiceMarkedForDelete)
		{
			throw new Win32Exception();
		}

		controller.WaitForStatus(ServiceControllerStatus.Stopped, StopTimeout);
	}

	private static void VerifyPassword(string account, string password)
	{
		var (domain, user) = ServiceSetup.SplitAccount(account);
		if (!LogonUser(user, domain, password, Logon32LogonService, Logon32ProviderDefault, out var token))
		{
			var error = Marshal.GetLastPInvokeError();
			throw error == ErrorLogonFailure
				? new Win32Exception(error, $"Wrong password for {account}. Use the Windows account password (a PIN does not work for services).")
				: new Win32Exception(error);
		}

		CloseHandle(token);
	}

	private static void GrantLogonAsService(SecurityIdentifier sid)
	{
		var attributes = default(LsaObjectAttributes);
		CheckNtStatus(LsaOpenPolicy(IntPtr.Zero, ref attributes, PolicyCreateAccount | PolicyLookupNames, out var policy));
		var right = Marshal.StringToHGlobalUni(SeServiceLogonRight);
		try
		{
			var sidBytes = new byte[sid.BinaryLength];
			sid.GetBinaryForm(sidBytes, 0);
			LsaUnicodeString[] rights =
			[
				new()
				{
					Length = (ushort)(SeServiceLogonRight.Length * sizeof(char)),
					MaximumLength = (ushort)((SeServiceLogonRight.Length + 1) * sizeof(char)),
					Buffer = right,
				},
			];
			CheckNtStatus(LsaAddAccountRights(policy, sidBytes, rights, 1));
		}
		finally
		{
			Marshal.FreeHGlobal(right);
			_ = LsaClose(policy);
		}
	}

	private static ServiceHandle CreateOrReconfigure(ServiceHandle manager, string serviceName, string binaryPath, string account, string password)
	{
		const uint access = ServiceChangeConfig | ServiceStart | ReadControl | WriteDac;
		var service = CreateService(
			manager, serviceName, ServiceSetup.DisplayName, access, ServiceWin32OwnProcess, ServiceAutoStart, ServiceErrorNormal,
			binaryPath, null, IntPtr.Zero, null, account, password);
		if (!service.IsInvalid)
		{
			return service;
		}

		var error = Marshal.GetLastPInvokeError();
		service.Dispose();
		if (error != ErrorServiceExists)
		{
			throw new Win32Exception(error);
		}

		// Re-running "Install service…" updates binary path, account and password (e.g. after a Windows password change).
		service = OpenService(manager, serviceName, access);
		ThrowIfInvalid(service);
		if (!ChangeServiceConfig(
			service, ServiceNoChange, ServiceAutoStart, ServiceNoChange, binaryPath, null, IntPtr.Zero, null, account, password, ServiceSetup.DisplayName))
		{
			throw new Win32Exception();
		}

		return service;
	}

	private static void SetFailureActions(ServiceHandle service)
	{
		var actions = Enumerable.Repeat(
			new ScAction { Type = ScActionRestart, Delay = (uint)ServiceSetup.RestartDelay.TotalMilliseconds },
			ServiceSetup.RestartAttempts).ToArray();
		var pinned = GCHandle.Alloc(actions, GCHandleType.Pinned);
		try
		{
			var info = new ServiceFailureActions
			{
				ResetPeriod = (uint)ServiceSetup.FailureResetPeriod.TotalSeconds,
				ActionCount = (uint)actions.Length,
				Actions = pinned.AddrOfPinnedObject(),
			};
			if (!ChangeServiceConfig2(service, ServiceConfigFailureActions, ref info))
			{
				throw new Win32Exception();
			}
		}
		finally
		{
			pinned.Free();
		}
	}

	private static void GrantUserControl(ServiceHandle service, SecurityIdentifier user)
	{
		if (!QueryServiceObjectSecurity(service, DaclSecurityInformation, null, 0, out var needed) && Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer)
		{
			throw new Win32Exception();
		}

		var descriptor = new byte[needed];
		if (!QueryServiceObjectSecurity(service, DaclSecurityInformation, descriptor, needed, out _)
			|| !SetServiceObjectSecurity(service, DaclSecurityInformation, ServiceSetup.GrantUserControl(descriptor, user)))
		{
			throw new Win32Exception();
		}
	}

	private static void ThrowIfInvalid(SafeHandle handle)
	{
		if (handle.IsInvalid)
		{
			throw new Win32Exception();
		}
	}

	private static void CheckNtStatus(uint status)
	{
		if (status != 0)
		{
			throw new Win32Exception(LsaNtStatusToWinError(status));
		}
	}

	[LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	private static partial ServiceHandle OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

	[LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	private static partial ServiceHandle OpenService(ServiceHandle manager, string serviceName, uint desiredAccess);

	[LibraryImport("advapi32.dll", EntryPoint = "CreateServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	private static partial ServiceHandle CreateService(
		ServiceHandle manager,
		string serviceName,
		string displayName,
		uint desiredAccess,
		uint serviceType,
		uint startType,
		uint errorControl,
		string binaryPathName,
		string? loadOrderGroup,
		IntPtr tagId,
		string? dependencies,
		string? serviceStartName,
		string? password);

	[LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool ChangeServiceConfig(
		ServiceHandle service,
		uint serviceType,
		uint startType,
		uint errorControl,
		string? binaryPathName,
		string? loadOrderGroup,
		IntPtr tagId,
		string? dependencies,
		string? serviceStartName,
		string? password,
		string? displayName);

	[LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool ChangeServiceConfig2(ServiceHandle service, uint infoLevel, ref ServiceFailureActions info);

	[LibraryImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool QueryServiceObjectSecurity(ServiceHandle service, uint securityInformation, byte[]? securityDescriptor, uint bufferSize, out uint bytesNeeded);

	[LibraryImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetServiceObjectSecurity(ServiceHandle service, uint securityInformation, byte[] securityDescriptor);

	[LibraryImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool DeleteService(ServiceHandle service);

	[LibraryImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool CloseServiceHandle(IntPtr handle);

	[LibraryImport("advapi32.dll", EntryPoint = "LogonUserW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool LogonUser(string userName, string domain, string password, uint logonType, uint logonProvider, out IntPtr token);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool CloseHandle(IntPtr handle);

	[LibraryImport("advapi32.dll")]
	private static partial uint LsaOpenPolicy(IntPtr systemName, ref LsaObjectAttributes objectAttributes, uint desiredAccess, out IntPtr policyHandle);

	[LibraryImport("advapi32.dll")]
	private static partial uint LsaAddAccountRights(IntPtr policyHandle, byte[] accountSid, LsaUnicodeString[] userRights, uint countOfRights);

	[LibraryImport("advapi32.dll")]
	private static partial uint LsaClose(IntPtr policyHandle);

	[LibraryImport("advapi32.dll")]
	private static partial int LsaNtStatusToWinError(uint status);

	[StructLayout(LayoutKind.Sequential)]
	private struct LsaObjectAttributes
	{
		public int Length;
		public IntPtr RootDirectory;
		public IntPtr ObjectName;
		public uint Attributes;
		public IntPtr SecurityDescriptor;
		public IntPtr SecurityQualityOfService;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct LsaUnicodeString
	{
		public ushort Length;
		public ushort MaximumLength;
		public IntPtr Buffer;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ServiceFailureActions
	{
		public uint ResetPeriod;
		public IntPtr RebootMessage;
		public IntPtr Command;
		public uint ActionCount;
		public IntPtr Actions;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ScAction
	{
		public uint Type;
		public uint Delay;
	}

	/// <summary>SC_HANDLE closed with CloseServiceHandle.</summary>
	private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
	{
		public ServiceHandle()
			: base(ownsHandle: true)
		{
		}

		protected override bool ReleaseHandle() => CloseServiceHandle(handle);
	}
}
```

Write `src/AiChromeProxy.Tray/Services/ServiceSetup.cs`:

```csharp
using System.Security.AccessControl;
using System.Security.Principal;
using AiChromeProxy.Infrastructure.Hosting;

namespace AiChromeProxy.Tray.Services;

/// <summary>The decisions behind service installation, kept out of the P/Invoke code so they are unit-tested.</summary>
public static class ServiceSetup
{
	public const string DisplayName = "AI Chrome Proxy";

	/// <summary>SERVICE_QUERY_CONFIG | SERVICE_QUERY_STATUS | SERVICE_START | SERVICE_STOP: tray Start/Stop/Restart without UAC.</summary>
	public const int UserControlRights = 0x0001 | 0x0004 | 0x0010 | 0x0020;

	/// <summary>Failure actions: restart after <see cref="RestartDelay"/>, this many times; the count resets after <see cref="FailureResetPeriod"/>.</summary>
	public const int RestartAttempts = 3;

	public static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(10);
	public static readonly TimeSpan FailureResetPeriod = TimeSpan.FromDays(1);

	/// <summary>The Server published self-contained into <c>server\</c> next to the tray (Velopack's stable <c>current\</c> folder).</summary>
	public static string ServerExecutable(string trayDirectory) => Path.Combine(trayDirectory, "server", "AiChromeProxy.Server.exe");

	/// <summary>Always quoted: the path is under the user's profile and may contain spaces (unquoted service paths are also a privilege-escalation hole).</summary>
	public static string BinaryPathName(string executable) => $"\"{executable}\"";

	/// <summary>Splits <c>DOMAIN\user</c>; a bare user name is a local account (<c>.</c>).</summary>
	public static (string Domain, string User) SplitAccount(string account)
	{
		var trimmed = account.Trim();
		var separator = trimmed.IndexOf('\\', StringComparison.Ordinal);
		return separator < 0 ? (".", trimmed) : (trimmed[..separator], trimmed[(separator + 1)..]);
	}

	/// <summary>Account name for CreateService: <c>DOMAIN\user</c>, or <c>.\user</c> for a local account.</summary>
	public static string ServiceStartName(string account)
	{
		var (domain, user) = SplitAccount(account);
		return $"{domain}\\{user}";
	}

	public static SecurityIdentifier Sid(string account)
	{
		var (domain, user) = SplitAccount(account);
		var name = domain == "." ? new NTAccount(Environment.MachineName, user) : new NTAccount(domain, user);
		return (SecurityIdentifier)name.Translate(typeof(SecurityIdentifier));
	}

	/// <summary>Adds an allow ACE with <see cref="UserControlRights"/> for <paramref name="user"/> to a service's self-relative security descriptor.</summary>
	public static byte[] GrantUserControl(byte[] securityDescriptor, SecurityIdentifier user)
	{
		var descriptor = new CommonSecurityDescriptor(isContainer: false, isDS: false, new RawSecurityDescriptor(securityDescriptor, 0));
		var dacl = descriptor.DiscretionaryAcl ?? throw new InvalidOperationException("The service has no DACL to extend.");
		dacl.AddAccess(AccessControlType.Allow, user, UserControlRights, InheritanceFlags.None, PropagationFlags.None);
		var bytes = new byte[descriptor.BinaryLength];
		descriptor.GetBinaryForm(bytes, 0);
		return bytes;
	}

	/// <summary>Creates <c>&lt;DataDir&gt;</c> and <c>logs</c> with inheritable full control for the service account.</summary>
	public static void PrepareDataDirectory(DataDirectory dataDir, SecurityIdentifier account)
	{
		Directory.CreateDirectory(dataDir.Logs);
		var root = new DirectoryInfo(dataDir.Root);
		var security = root.GetAccessControl();
		security.AddAccessRule(new FileSystemAccessRule(
			account,
			FileSystemRights.FullControl,
			InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
			PropagationFlags.None,
			AccessControlType.Allow));
		root.SetAccessControl(security);
	}
}
```

Write `src/AiChromeProxy.Tray/Services/WindowsServiceControl.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.ServiceProcess;
using AiChromeProxy.Infrastructure.Hosting;

namespace AiChromeProxy.Tray.Services;

/// <summary>Status, start and stop through the Service Control Manager; no elevation once the service DACL grants the user start/stop.</summary>
public sealed class WindowsServiceControl(string serviceName = WindowsServiceControl.ServiceName) : IServiceControl
{
	public const string ServiceName = "AiChromeProxy";

	private const int ErrorServiceDoesNotExist = 1060;
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

	public ServiceState GetState()
	{
		using var service = new ServiceController(serviceName);
		try
		{
			return service.Status switch
			{
				ServiceControllerStatus.Running => ServiceState.Running,
				ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => ServiceState.Starting,
				ServiceControllerStatus.StopPending or ServiceControllerStatus.PausePending => ServiceState.Stopping,
				_ => ServiceState.Stopped,
			};
		}
		catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: ErrorServiceDoesNotExist })
		{
			return ServiceState.NotInstalled;
		}
	}

	/// <summary>Not unit-tested: starting a real service needs one installed with a DACL for the test user (manual checklist).</summary>
	[ExcludeFromCodeCoverage]
	public Task StartAsync(CancellationToken ct) => Task.Run(
		() =>
		{
			using var service = new ServiceController(serviceName);
			if (service.Status == ServiceControllerStatus.Stopped)
			{
				service.Start();
			}

			service.WaitForStatus(ServiceControllerStatus.Running, Timeout);
		},
		ct);

	[ExcludeFromCodeCoverage]
	public Task StopAsync(CancellationToken ct) => Task.Run(
		() =>
		{
			using var service = new ServiceController(serviceName);
			if (service.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
			{
				// Stop() would enumerate dependent services first, which needs SERVICE_ENUMERATE_DEPENDENTS; there are none.
				service.Stop(stopDependentServices: false);
			}

			service.WaitForStatus(ServiceControllerStatus.Stopped, Timeout);
		},
		ct);

	[ExcludeFromCodeCoverage]
	public void Install(string account, string password, string controlUser) =>
		ServiceInstaller.Install(serviceName, account, password, controlUser, DataDirectory.FromEnvironment());

	[ExcludeFromCodeCoverage]
	public void Uninstall() => ServiceInstaller.Uninstall(serviceName);
}
```

Write `src/AiChromeProxy.Tray/ViewModels/InstallViewModel.cs`:

```csharp
using AiChromeProxy.Tray.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiChromeProxy.Tray.ViewModels;

/// <summary>The elevated install dialog: the account the service runs as (default: the tray user) and its password.</summary>
public sealed partial class InstallViewModel : ObservableObject
{
	private readonly IServiceControl _service;
	private readonly string _controlUser;

	public InstallViewModel(IServiceControl service, string controlUser)
	{
		_service = service;
		_controlUser = controlUser;
		Account = controlUser;
	}

	[ObservableProperty]
	public partial string Account { get; set; }

	[ObservableProperty]
	public partial string Password { get; set; } = string.Empty;

	[ObservableProperty]
	public partial string? Error { get; private set; }

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(InstallCommand))]
	public partial bool IsBusy { get; private set; }

	/// <summary>Set once the service is installed and started; the window then closes with exit code 0.</summary>
	[ObservableProperty]
	public partial bool Succeeded { get; private set; }

	[RelayCommand(CanExecute = nameof(CanInstall))]
	private async Task InstallAsync()
	{
		Error = null;
		if (string.IsNullOrWhiteSpace(Account) || Password.Length == 0)
		{
			Error = "Enter the account and its Windows password.";
			return;
		}

		IsBusy = true;
		try
		{
			var account = Account.Trim();
			var password = Password;
			await Task.Run(() => _service.Install(account, password, _controlUser));
			Password = string.Empty;
			Succeeded = true;
		}
		catch (Exception ex)
		{
			Error = ex.Message;
		}
		finally
		{
			IsBusy = false;
		}
	}

	private bool CanInstall() => !IsBusy;
}
```

Write `src/AiChromeProxy.Tray/ViewModels/TrayViewModel.cs`:

```csharp
using AiChromeProxy.Tray.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiChromeProxy.Tray.ViewModels;

/// <summary>Tray menu state: service status line, Start / Stop / Restart and the elevated Install / Uninstall.</summary>
/// <param name="runElevated">Runs <c>--admin &lt;command&gt;</c> elevated; returns its exit code, or null when UAC was declined.</param>
public sealed partial class TrayViewModel(IServiceControl service, Func<string, Task<int?>> runElevated) : ObservableObject
{
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(StatusText))]
	[NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(RestartCommand), nameof(UninstallCommand))]
	public partial ServiceState State { get; private set; }

	/// <summary>Last failure of a menu action or status query; cleared when the next action starts.</summary>
	[ObservableProperty]
	public partial string? Error { get; private set; }

	public string StatusText => State switch
	{
		ServiceState.NotInstalled => "Service: not installed",
		ServiceState.Stopped => "Service: stopped",
		ServiceState.Starting => "Service: starting…",
		ServiceState.Stopping => "Service: stopping…",
		_ => "Service: running",
	};

	public void Refresh()
	{
		try
		{
			State = service.GetState();
		}
		catch (Exception ex)
		{
			Error = ex.Message;
		}
	}

	[RelayCommand(CanExecute = nameof(CanStart))]
	private Task StartAsync() => RunAsync(service.StartAsync);

	[RelayCommand(CanExecute = nameof(CanStop))]
	private Task StopAsync() => RunAsync(service.StopAsync);

	[RelayCommand(CanExecute = nameof(CanStop))]
	private Task RestartAsync() => RunAsync(async ct =>
	{
		await service.StopAsync(ct);
		await service.StartAsync(ct);
	});

	/// <summary>Always available: on an installed service it updates the account and password (e.g. after a Windows password change).</summary>
	[RelayCommand]
	private Task InstallAsync() => RunAsync(_ => ElevateAsync(AdminCommand.Install));

	[RelayCommand(CanExecute = nameof(CanUninstall))]
	private Task UninstallAsync() => RunAsync(_ => ElevateAsync(AdminCommand.Uninstall));

	private bool CanStart() => State == ServiceState.Stopped;

	private bool CanUninstall() => State != ServiceState.NotInstalled;

	private bool CanStop() => State is ServiceState.Running or ServiceState.Starting;

	private async Task ElevateAsync(string command)
	{
		var exitCode = await runElevated(command);
		if (exitCode is not (null or 0))
		{
			throw new InvalidOperationException($"Service {command} did not complete (exit code {exitCode}).");
		}
	}

	private async Task RunAsync(Func<CancellationToken, Task> action)
	{
		Error = null;
		try
		{
			await action(CancellationToken.None);
		}
		catch (Exception ex)
		{
			Error = ex.Message;
		}

		Refresh();
	}
}
```

Write `src/AiChromeProxy.Tray/Views/InstallWindow.axaml`:

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:AiChromeProxy.Tray.ViewModels"
        x:Class="AiChromeProxy.Tray.Views.InstallWindow"
        x:DataType="vm:InstallViewModel"
        Title="Install AI Chrome Proxy service"
        Icon="/Assets/tray.ico"
        Width="480" SizeToContent="Height" CanResize="False"
        WindowStartupLocation="CenterScreen">
  <StackPanel Margin="16" Spacing="8">
    <TextBlock TextWrapping="Wrap"
               Text="The service starts with Windows, before anyone logs in, and runs as this account so Claude Code sees its profile. Windows needs the account's password for that; it is stored only by the Service Control Manager." />
    <TextBlock Text="Account" />
    <TextBox Text="{Binding Account}" />
    <TextBlock Text="Windows password (not the PIN)" />
    <TextBox Text="{Binding Password}" PasswordChar="●" />
    <TextBlock Text="{Binding Error}" Foreground="#C42B1C" TextWrapping="Wrap"
               IsVisible="{Binding Error, Converter={x:Static ObjectConverters.IsNotNull}}" />
    <StackPanel Orientation="Horizontal" Spacing="8" HorizontalAlignment="Right">
      <Button Content="Cancel" IsCancel="True" Click="OnCancel" />
      <Button Content="Install" Command="{Binding InstallCommand}" IsDefault="True" />
    </StackPanel>
  </StackPanel>
</Window>
```

Write `src/AiChromeProxy.Tray/Views/InstallWindow.axaml.cs`:

```csharp
using AiChromeProxy.Tray.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AiChromeProxy.Tray.Views;

public partial class InstallWindow : Window
{
	public InstallWindow() => InitializeComponent();

	public InstallWindow(InstallViewModel vm)
		: this()
	{
		DataContext = vm;
		vm.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(InstallViewModel.Succeeded) && vm.Succeeded)
			{
				Close();
			}
		};
	}

	private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
```

- [ ] **Step 4: Build and run the gate (GREEN)**

Run: `dotnet build -c Release` → Expected: `0 Warning(s)`, `0 Error(s)`.
Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total` then `echo $?` → Expected: `total: 184, failed: 0`, exit code 0.

- [ ] **Step 5: Commit**

```bash
git add -A
git status --short   # every file listed above must appear; nothing under a Logs/ folder
git commit -m "feat(tray): elevated service install/uninstall"
```

---

### Task 9: feat(tray): Velopack hooks and update orchestration

**Files:**
- Create: `src/AiChromeProxy.Tray/Updates/IUpdateSource.cs`, `src/AiChromeProxy.Tray/Updates/UpdateOrchestrator.cs`, `src/AiChromeProxy.Tray/Updates/VelopackHooks.cs`, `src/AiChromeProxy.Tray/Updates/VelopackUpdateSource.cs`, `tests/AiChromeProxy.Tests/Tray/FakeAutoStart.cs`, `tests/AiChromeProxy.Tests/Tray/FakeUpdateSource.cs`, `tests/AiChromeProxy.Tests/Tray/UpdateOrchestratorTests.cs`
- Modify: `src/AiChromeProxy.Tray/AiChromeProxy.Tray.csproj`, `src/AiChromeProxy.Tray/App.axaml.cs`, `src/AiChromeProxy.Tray/Program.cs`, `src/AiChromeProxy.Tray/ViewModels/TrayViewModel.cs`, `tests/AiChromeProxy.Tests/AiChromeProxy.Tests.csproj`, `tests/AiChromeProxy.Tests/Tray/SettingsViewModelTests.cs`, `tests/AiChromeProxy.Tests/Tray/TrayViewModelTests.cs`

Every code block below is the **complete final content** of that file (verified on the prototype: builds with 0 warnings, gate passes). Write it exactly; for modified files replace the whole file.

- [ ] **Step 1: Write the tests**

Write `tests/AiChromeProxy.Tests/AiChromeProxy.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.MTP" Version="10.1.0" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.12" />
    <PackageReference Include="Microsoft.AspNetCore.SignalR.Client" Version="10.0.12" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.10.0" />
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
    <ProjectReference Include="..\..\src\AiChromeProxy.Tray\AiChromeProxy.Tray.csproj" />
  </ItemGroup>

</Project>
```

Write `tests/AiChromeProxy.Tests/Tray/FakeAutoStart.cs`:

```csharp
using AiChromeProxy.Tray.Services;

namespace AiChromeProxy.Tests.Tray;

public sealed class FakeAutoStart : IAutoStart
{
	public bool IsEnabled { get; set; }
}
```

Write `tests/AiChromeProxy.Tests/Tray/FakeUpdateSource.cs`:

```csharp
using AiChromeProxy.Tray.Updates;

namespace AiChromeProxy.Tests.Tray;

/// <summary>Records into the same call list as <see cref="FakeServiceControl"/>, so tests can assert the cross-object order.</summary>
public sealed class FakeUpdateSource(List<string> calls) : IUpdateSource
{
	public string? Available { get; set; } = "1.2.3";

	public Exception? FailCheck { get; set; }

	public Exception? FailDownload { get; set; }

	public Exception? FailApply { get; set; }

	public int Checks { get; private set; }

	public Task<string?> CheckAsync(CancellationToken ct)
	{
		Checks++;
		return FailCheck is null ? Task.FromResult(Available) : Task.FromException<string?>(FailCheck);
	}

	public Task DownloadAsync(CancellationToken ct)
	{
		calls.Add("download");
		return FailDownload is null ? Task.CompletedTask : Task.FromException(FailDownload);
	}

	public void ApplyAndRestart()
	{
		calls.Add("apply");
		if (FailApply is not null)
		{
			throw FailApply;
		}
	}
}
```

Write `tests/AiChromeProxy.Tests/Tray/SettingsViewModelTests.cs`:

```csharp
using System.Text.Json.Nodes;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.ViewModels;

namespace AiChromeProxy.Tests.Tray;

public sealed class SettingsViewModelTests : IDisposable
{
	private readonly DataDirectory _dataDir = new(Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N")));
	private readonly FakeAutoStart _autoStart = new();
	private readonly FakeServiceControl _service = new(ServiceState.Running);

	[Fact]
	public void NoSettingsFile_Defaults()
	{
		var vm = Create();

		Assert.Equal(string.Empty, vm.TeamDomain);
		Assert.Equal("5180", vm.Port);
		Assert.Empty(vm.Errors);
	}

	[Fact]
	public void ExistingFile_ValuesLoaded()
	{
		WriteFile("""{ "CloudflareAccess": { "TeamDomain": "t.cloudflareaccess.com", "Audience": "aud" }, "Server": { "Port": 6000, "PublicHost": "code.example.com" } }""");
		_autoStart.IsEnabled = true;

		var vm = Create();

		Assert.Equal("t.cloudflareaccess.com", vm.TeamDomain);
		Assert.Equal("aud", vm.Audience);
		Assert.Equal("6000", vm.Port);
		Assert.Equal("code.example.com", vm.PublicHost);
		Assert.True(vm.StartWithWindows);
	}

	[Fact]
	public void BrokenFile_ErrorShown_SaveReplacesIt()
	{
		WriteFile("{ not json");

		var vm = Valid(Create());
		Assert.Contains("Could not read", Assert.Single(vm.Errors));

		vm.SaveCommand.Execute(null);

		Assert.Empty(vm.Errors);
		Assert.Equal("code.example.com", (string?)ReadFile()["Server"]?["PublicHost"]);
	}

	[Theory]
	[InlineData("https://team.cloudflareaccess.com", "aud", "code.example.com", "5180", "CloudflareAccess:TeamDomain must be a bare host name")]
	[InlineData("team.cloudflareaccess.com", "", "code.example.com", "5180", "CloudflareAccess:TeamDomain and CloudflareAccess:Audience must be set.")]
	[InlineData("team.cloudflareaccess.com", "aud", "", "5180", "Server:PublicHost must be set")]
	[InlineData("team.cloudflareaccess.com", "aud", "code.example.com:443", "5180", "Server:PublicHost must be a bare host name")]
	[InlineData("team.cloudflareaccess.com", "aud", "code.example.com", "0", "Server:Port must be a number from 1 to 65535.")]
	[InlineData("team.cloudflareaccess.com", "aud", "code.example.com", "65536", "Server:Port must be a number from 1 to 65535.")]
	[InlineData("team.cloudflareaccess.com", "aud", "code.example.com", "-1", "Server:Port must be a number from 1 to 65535.")]
	[InlineData("team.cloudflareaccess.com", "aud", "code.example.com", "abc", "Server:Port must be a number from 1 to 65535.")]
	public void Save_Invalid_ShowsServerMessage_WritesNothing(string team, string audience, string publicHost, string port, string message)
	{
		var vm = Create();
		vm.TeamDomain = team;
		vm.Audience = audience;
		vm.PublicHost = publicHost;
		vm.Port = port;

		vm.SaveCommand.Execute(null);

		Assert.StartsWith(message, Assert.Single(vm.Errors));
		Assert.False(File.Exists(_dataDir.SettingsFile));
		Assert.Null(vm.Status);
	}

	[Fact]
	public void Save_Valid_WritesExpectedJson_KeepsOtherKeys_SetsAutoStart()
	{
		WriteFile("""{ "Serilog": { "MinimumLevel": { "Default": "Debug" } }, "Server": { "Port": 5180, "Extra": true } }""");
		var vm = Valid(Create());
		vm.Port = " 6001 ";
		vm.StartWithWindows = true;

		vm.SaveCommand.Execute(null);

		Assert.Empty(vm.Errors);
		var expected = JsonNode.Parse("""
			{
			  "Serilog": { "MinimumLevel": { "Default": "Debug" } },
			  "Server": { "Port": 6001, "Extra": true, "PublicHost": "code.example.com" },
			  "CloudflareAccess": { "TeamDomain": "team.cloudflareaccess.com", "Audience": "aud" }
			}
			""");
		Assert.True(JsonNode.DeepEquals(expected, ReadFile()), ReadFile().ToJsonString());
		Assert.True(_autoStart.IsEnabled);
	}

	[Fact]
	public void Save_FileNotWritable_ErrorShown_NoRestartOffered()
	{
		Directory.CreateDirectory(_dataDir.SettingsFile);
		var vm = Valid(Create());

		vm.SaveCommand.Execute(null);

		Assert.Contains("appsettings.json", Assert.Single(vm.Errors));
		Assert.False(vm.IsRestartOffered);
		Assert.Null(vm.Status);
	}

	[Fact]
	public async Task Save_ServiceRunning_OffersRestart_RestartStopsThenStarts()
	{
		var vm = Valid(Create());

		vm.SaveCommand.Execute(null);

		Assert.True(vm.IsRestartOffered);
		Assert.Equal("Saved. Restart the service to apply.", vm.Status);
		await vm.RestartServiceCommand.ExecuteAsync(null);
		Assert.Equal(["stop", "start"], _service.Calls);
		Assert.False(vm.IsRestartOffered);
		Assert.Equal("Service restarted.", vm.Status);
	}

	[Fact]
	public async Task RestartFails_ErrorShown()
	{
		_service.FailStart = new InvalidOperationException("cannot start");
		var vm = Valid(Create());
		vm.SaveCommand.Execute(null);

		await vm.RestartServiceCommand.ExecuteAsync(null);

		Assert.Equal(["cannot start"], vm.Errors);
	}

	[Theory]
	[InlineData(ServiceState.Stopped)]
	[InlineData(ServiceState.NotInstalled)]
	public void Save_ServiceNotRunning_NoRestartOffered(ServiceState state)
	{
		_service.State = state;
		var vm = Valid(Create());

		vm.SaveCommand.Execute(null);

		Assert.False(vm.IsRestartOffered);
		Assert.False(vm.RestartServiceCommand.CanExecute(null));
		Assert.Equal("Saved. Applied when the service starts.", vm.Status);
	}

	[Fact]
	public void UiAddress_PublicHost_ThroughTunnel()
	{
		WriteFile("""{ "Server": { "Port": 6000, "PublicHost": "code.example.com" } }""");

		Assert.Equal("https://code.example.com/", SettingsViewModel.UiAddress(_dataDir).AbsoluteUri);
	}

	[Theory]
	[InlineData("""{ "Server": { "Port": 6000 } }""", "http://127.0.0.1:6000/")]
	[InlineData("{ broken", "http://127.0.0.1:5180/")]
	public void UiAddress_NoPublicHost_Loopback(string json, string expected)
	{
		WriteFile(json);

		Assert.Equal(expected, SettingsViewModel.UiAddress(_dataDir).AbsoluteUri);
	}

	public void Dispose()
	{
		if (Directory.Exists(_dataDir.Root))
		{
			Directory.Delete(_dataDir.Root, recursive: true);
		}
	}

	private static SettingsViewModel Valid(SettingsViewModel vm)
	{
		vm.TeamDomain = " team.cloudflareaccess.com ";
		vm.Audience = "aud";
		vm.PublicHost = "code.example.com";
		return vm;
	}

	private SettingsViewModel Create() => new(_dataDir, _autoStart, _service);

	private void WriteFile(string json)
	{
		Directory.CreateDirectory(_dataDir.Root);
		File.WriteAllText(_dataDir.SettingsFile, json);
	}

	private JsonNode ReadFile() => JsonNode.Parse(File.ReadAllText(_dataDir.SettingsFile))!;
}
```

Write `tests/AiChromeProxy.Tests/Tray/TrayViewModelTests.cs`:

```csharp
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.Updates;
using AiChromeProxy.Tray.ViewModels;

namespace AiChromeProxy.Tests.Tray;

public sealed class TrayViewModelTests
{
	[Theory]
	[InlineData(ServiceState.NotInstalled, "Service: not installed")]
	[InlineData(ServiceState.Stopped, "Service: stopped")]
	[InlineData(ServiceState.Starting, "Service: starting…")]
	[InlineData(ServiceState.Stopping, "Service: stopping…")]
	[InlineData(ServiceState.Running, "Service: running")]
	public void Refresh_StatusLineFollowsService(ServiceState state, string text)
	{
		var vm = Create(new FakeServiceControl(state));

		vm.Refresh();

		Assert.Equal(text, vm.StatusText);
	}

	[Theory]
	[InlineData(ServiceState.NotInstalled, false, false, false)]
	[InlineData(ServiceState.Stopped, true, false, true)]
	[InlineData(ServiceState.Starting, false, true, true)]
	[InlineData(ServiceState.Stopping, false, false, true)]
	[InlineData(ServiceState.Running, false, true, true)]
	public void Commands_EnabledByState(ServiceState state, bool canStart, bool canStop, bool canUninstall)
	{
		var vm = Create(new FakeServiceControl(state));

		vm.Refresh();

		Assert.Equal(canStart, vm.StartCommand.CanExecute(null));
		Assert.Equal(canStop, vm.StopCommand.CanExecute(null));
		Assert.Equal(canStop, vm.RestartCommand.CanExecute(null));
		Assert.Equal(canUninstall, vm.UninstallCommand.CanExecute(null));
		Assert.True(vm.InstallCommand.CanExecute(null));
	}

	[Fact]
	public async Task Install_RunsElevatedAdminInstall_ThenRefreshes()
	{
		var service = new FakeServiceControl(ServiceState.NotInstalled);
		var elevated = new List<string>();
		var vm = new TrayViewModel(
			service,
			command =>
			{
				elevated.Add(command);
				service.State = ServiceState.Running;
				return Task.FromResult<int?>(0);
			},
			Updates(service));
		vm.Refresh();

		await vm.InstallCommand.ExecuteAsync(null);

		Assert.Equal(["install"], elevated);
		Assert.Equal(ServiceState.Running, vm.State);
		Assert.Null(vm.Error);
	}

	[Fact]
	public async Task Uninstall_ElevatedInstanceFails_ErrorWithExitCode()
	{
		var vm = new TrayViewModel(new FakeServiceControl(ServiceState.Running), _ => Task.FromResult<int?>(1), Updates(new FakeServiceControl()));
		vm.Refresh();

		await vm.UninstallCommand.ExecuteAsync(null);

		Assert.Equal("Service uninstall did not complete (exit code 1).", vm.Error);
	}

	[Fact]
	public async Task Install_UacDeclined_NoError()
	{
		var vm = new TrayViewModel(new FakeServiceControl(ServiceState.NotInstalled), _ => Task.FromResult<int?>(null), Updates(new FakeServiceControl()));

		await vm.InstallCommand.ExecuteAsync(null);

		Assert.Null(vm.Error);
	}

	[Fact]
	public async Task Restart_StopsThenStarts_AndRefreshes()
	{
		var service = new FakeServiceControl(ServiceState.Running);
		var vm = Create(service);
		vm.Refresh();

		await vm.RestartCommand.ExecuteAsync(null);

		Assert.Equal(["stop", "start"], service.Calls);
		Assert.Equal(ServiceState.Running, vm.State);
		Assert.Null(vm.Error);
	}

	[Fact]
	public async Task Stop_StopsService()
	{
		var service = new FakeServiceControl(ServiceState.Running);
		var vm = Create(service);
		vm.Refresh();

		await vm.StopCommand.ExecuteAsync(null);

		Assert.Equal(["stop"], service.Calls);
		Assert.Equal(ServiceState.Stopped, vm.State);
		Assert.True(vm.StartCommand.CanExecute(null));
	}

	[Fact]
	public async Task Start_Fails_ErrorShown_NextActionClearsIt()
	{
		var service = new FakeServiceControl(ServiceState.Stopped) { FailStart = new InvalidOperationException("access denied") };
		var vm = Create(service);
		vm.Refresh();

		await vm.StartCommand.ExecuteAsync(null);
		Assert.Equal("access denied", vm.Error);

		service.FailStart = null;
		await vm.StartCommand.ExecuteAsync(null);
		Assert.Null(vm.Error);
		Assert.Equal(ServiceState.Running, vm.State);
	}

	[Fact]
	public void Refresh_StatusQueryFails_ErrorShown()
	{
		var vm = Create(new FakeServiceControl { FailGetState = new InvalidOperationException("scm down") });

		vm.Refresh();

		Assert.Equal("scm down", vm.Error);
	}

	private static TrayViewModel Create(FakeServiceControl service) => new(service, _ => Task.FromResult<int?>(0), Updates(service));

	private static UpdateOrchestrator Updates(FakeServiceControl service) =>
		new(new FakeUpdateSource(service.Calls), service, Path.Combine(Path.GetTempPath(), "aicp-tests-" + Guid.NewGuid().ToString("N")));
}
```

Write `tests/AiChromeProxy.Tests/Tray/UpdateOrchestratorTests.cs`:

```csharp
using System.Threading.Channels;
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.Updates;
using AiChromeProxy.Tray.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AiChromeProxy.Tests.Tray;

public sealed class UpdateOrchestratorTests : IDisposable
{
	private readonly string _marker = Path.Combine(Path.GetTempPath(), "aicp-tests-" + Guid.NewGuid().ToString("N") + ".update-pending");
	private readonly FakeServiceControl _service = new(ServiceState.Running);
	private readonly FakeUpdateSource _source;
	private readonly UpdateOrchestrator _updates;

	public UpdateOrchestratorTests()
	{
		_source = new FakeUpdateSource(_service.Calls);
		_updates = new UpdateOrchestrator(_source, _service, _marker);
	}

	[Fact]
	public async Task Update_DownloadThenStopThenApply()
	{
		await _updates.UpdateAsync(TestContext.Current.CancellationToken);

		Assert.Equal(["download", "stop", "apply"], _service.Calls);
		Assert.True(File.Exists(_marker));
	}

	[Fact]
	public async Task DownloadFails_ServiceUntouched()
	{
		_source.FailDownload = new HttpRequestException("offline");

		await Assert.ThrowsAsync<HttpRequestException>(() => _updates.UpdateAsync(TestContext.Current.CancellationToken));

		Assert.Equal(["download"], _service.Calls);
		Assert.Equal(ServiceState.Running, _service.State);
		Assert.False(File.Exists(_marker));
	}

	[Fact]
	public async Task ApplyFails_ServiceStartedAgain()
	{
		_source.FailApply = new InvalidOperationException("locked");

		await Assert.ThrowsAsync<InvalidOperationException>(() => _updates.UpdateAsync(TestContext.Current.CancellationToken));

		Assert.Equal(["download", "stop", "apply", "start"], _service.Calls);
		Assert.Equal(ServiceState.Running, _service.State);
		Assert.False(File.Exists(_marker));
	}

	[Theory]
	[InlineData(ServiceState.Stopped)]
	[InlineData(ServiceState.NotInstalled)]
	public async Task ServiceNotRunning_NotStoppedNorStarted(ServiceState state)
	{
		_service.State = state;
		_source.FailApply = new InvalidOperationException("locked");

		await Assert.ThrowsAsync<InvalidOperationException>(() => _updates.UpdateAsync(TestContext.Current.CancellationToken));

		Assert.Equal(["download", "apply"], _service.Calls);
		Assert.Equal(state, _service.State);
	}

	[Fact]
	public async Task NextTrayStart_AfterInterruptedUpdate_ResumesService()
	{
		await _updates.UpdateAsync(TestContext.Current.CancellationToken);
		_service.Calls.Clear();

		await _updates.ResumeServiceAfterUpdateAsync(TestContext.Current.CancellationToken);

		Assert.Equal(["start"], _service.Calls);
		Assert.False(File.Exists(_marker));
	}

	[Theory]
	[InlineData(false, ServiceState.Stopped)]
	[InlineData(true, ServiceState.Running)]
	public async Task Resume_NoMarkerOrAlreadyRunning_DoesNotStart(bool marker, ServiceState state)
	{
		if (marker)
		{
			await File.WriteAllTextAsync(_marker, string.Empty, TestContext.Current.CancellationToken);
		}

		_service.State = state;

		await _updates.ResumeServiceAfterUpdateAsync(TestContext.Current.CancellationToken);

		Assert.Empty(_service.Calls);
		Assert.False(File.Exists(_marker));
	}

	[Fact]
	public async Task Checks_AtStartAndEvery24Hours_ErrorsReportedNotThrown()
	{
		var time = new FakeTimeProvider();
		var reports = Channel.CreateUnbounded<(string? Version, Exception? Error)>();
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var loop = _updates.RunChecksAsync(time, (v, e) => reports.Writer.TryWrite((v, e)), cts.Token);

		Assert.Equal(("1.2.3", null), await NextAsync(reports));
		Assert.Equal("1.2.3", _updates.AvailableVersion);

		time.Advance(TimeSpan.FromHours(23));
		Assert.False(reports.Reader.TryRead(out _));
		Assert.Equal(1, _source.Checks);

		_source.FailCheck = new HttpRequestException("rate limited");
		time.Advance(TimeSpan.FromHours(1));
		var failed = await NextAsync(reports);
		Assert.Null(failed.Version);
		Assert.IsType<HttpRequestException>(failed.Error);

		_source.FailCheck = null;
		_source.Available = null;
		time.Advance(TimeSpan.FromHours(24));
		Assert.Equal((null, null), await NextAsync(reports));
		Assert.Equal(3, _source.Checks);

		await cts.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
	}

	[Fact]
	public async Task TrayViewModel_UpdateFound_MenuItem_UpdateFailure_ShowsError()
	{
		var vm = new TrayViewModel(_service, _ => Task.FromResult<int?>(0), _updates);
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var checks = vm.RunUpdateChecksAsync(new FakeTimeProvider(), cts.Token);

		Assert.True(vm.IsUpdateAvailable);
		Assert.Equal("Update to v1.2.3", vm.UpdateText);

		_source.FailDownload = new HttpRequestException("offline");
		await vm.UpdateCommand.ExecuteAsync(null);
		Assert.Equal("offline", vm.Error);
		Assert.Equal(ServiceState.Running, vm.State);

		await cts.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checks);
	}

	[Fact]
	public async Task TrayViewModel_CheckFails_ErrorShown_NoUpdateItem()
	{
		_source.FailCheck = new HttpRequestException("rate limited");
		var vm = new TrayViewModel(_service, _ => Task.FromResult<int?>(0), _updates);
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var checks = vm.RunUpdateChecksAsync(new FakeTimeProvider(), cts.Token);

		Assert.False(vm.IsUpdateAvailable);
		Assert.Equal("Update check failed: rate limited", vm.Error);

		await cts.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checks);
	}

	[Theory]
	[InlineData(ServiceState.Stopped, true)]
	[InlineData(ServiceState.Running, false)]
	[InlineData(ServiceState.NotInstalled, false)]
	public void AfterUpdateHook_StartsStoppedService(ServiceState state, bool started)
	{
		_service.State = state;

		VelopackHooks.AfterUpdate(_service);

		Assert.Equal(started ? ["start"] : [], _service.Calls);
	}

	[Fact]
	public void AfterUpdateHook_StartFails_DoesNotThrow()
	{
		_service.State = ServiceState.Stopped;
		_service.FailStart = new InvalidOperationException("denied");

		VelopackHooks.AfterUpdate(_service);

		Assert.Equal(["start"], _service.Calls);
	}

	[Theory]
	[InlineData(ServiceState.Running, true)]
	[InlineData(ServiceState.Stopped, true)]
	[InlineData(ServiceState.NotInstalled, false)]
	public void BeforeUninstallHook_ElevatedUninstallWhenInstalled_RemovesAutoStart(ServiceState state, bool elevated)
	{
		_service.State = state;
		var commands = new List<string>();
		var autoStart = new FakeAutoStart { IsEnabled = true };

		VelopackHooks.BeforeUninstall(
			_service,
			command =>
			{
				commands.Add(command);
				return Task.FromResult<int?>(0);
			},
			autoStart);

		Assert.Equal(elevated ? ["uninstall"] : [], commands);
		Assert.False(autoStart.IsEnabled);
	}

	[Fact]
	public void BeforeUninstallHook_ElevationFails_DoesNotThrow()
	{
		VelopackHooks.BeforeUninstall(_service, _ => throw new InvalidOperationException("no UAC"), new FakeAutoStart());
	}

	public void Dispose() => File.Delete(_marker);

	private static async Task<T> NextAsync<T>(Channel<T> channel) =>
		await channel.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
}
```

- [ ] **Step 2: Run to verify they fail (RED)**

Run: `dotnet build -c Release`
Expected: FAIL — compile errors for the types/members this task introduces (or, where the tests only extend existing types, failing assertions in the next step). Record the first errors in your report.

- [ ] **Step 3: Implement**

Write `src/AiChromeProxy.Tray/AiChromeProxy.Tray.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <ApplicationIcon>Assets\tray.ico</ApplicationIcon>
    <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
    <!-- GitHub repository the tray updates from. The release workflow passes -p:UpdateRepository=https://github.com/<owner>/<repo>; empty = no update checks. -->
    <UpdateRepository Condition="'$(UpdateRepository)' == ''"></UpdateRepository>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Avalonia" Version="12.1.3" />
    <PackageReference Include="Avalonia.Desktop" Version="12.1.3" />
    <PackageReference Include="Avalonia.Themes.Fluent" Version="12.1.3" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
    <PackageReference Include="System.ServiceProcess.ServiceController" Version="10.0.12" />
    <PackageReference Include="Velopack" Version="1.2.161" />
  </ItemGroup>

  <ItemGroup>
    <AssemblyMetadata Include="UpdateRepository" Value="$(UpdateRepository)" />
  </ItemGroup>

  <ItemGroup>
    <AvaloniaResource Include="Assets\**" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\AiChromeProxy.Domain\AiChromeProxy.Domain.csproj" />
    <ProjectReference Include="..\AiChromeProxy.Infrastructure\AiChromeProxy.Infrastructure.csproj" />
  </ItemGroup>

</Project>
```

Write `src/AiChromeProxy.Tray/App.axaml.cs`:

```csharp
using System.Diagnostics;
using System.Reflection;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.Updates;
using AiChromeProxy.Tray.ViewModels;
using AiChromeProxy.Tray.Views;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;

namespace AiChromeProxy.Tray;

public partial class App : Avalonia.Application
{
	private static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(2);

	public override void Initialize() => AvaloniaXamlLoader.Load(this);

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			if (AdminCommand.Parse(desktop.Args) is { Command: AdminCommand.Install } admin)
			{
				ShowInstall(desktop, admin.User);
			}
			else
			{
				StartTray(desktop);
			}
		}

		base.OnFrameworkInitializationCompleted();
	}

	private static NativeMenuItem Item(string header, Action onClick)
	{
		var item = new NativeMenuItem(header);
		item.Click += (_, _) => onClick();
		return item;
	}

	/// <summary>One window per kind: a second click brings the open one to front.</summary>
	private static void ShowSingle<TWindow>(IClassicDesktopStyleApplicationLifetime desktop, Func<TWindow> create)
		where TWindow : Window
	{
		var window = desktop.Windows.OfType<TWindow>().FirstOrDefault() ?? create();
		window.Show();
		window.Activate();
	}

	/// <summary>The elevated <c>--admin install</c> instance: only the password dialog; exit code 0 once installed.</summary>
	private static void ShowInstall(IClassicDesktopStyleApplicationLifetime desktop, string controlUser)
	{
		var vm = new InstallViewModel(new WindowsServiceControl(), controlUser);
		var window = new InstallWindow(vm);
		window.Closed += (_, _) => desktop.Shutdown(vm.Succeeded ? 0 : 1);
		window.Show();
	}

	private static void Open(Uri address) => Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true })?.Dispose();

	private void StartTray(IClassicDesktopStyleApplicationLifetime desktop)
	{
		var dataDir = DataDirectory.FromEnvironment();
		var service = new WindowsServiceControl();
		var repository = typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "UpdateRepository")?.Value;
		var updates = new UpdateOrchestrator(new VelopackUpdateSource(repository), service, Path.Combine(Path.GetTempPath(), "AiChromeProxy.update-pending"));
		var vm = new TrayViewModel(service, AdminCommand.RunElevatedAsync, updates);
		var update = new NativeMenuItem { Command = vm.UpdateCommand };
		var status = new NativeMenuItem { IsEnabled = false };
		var error = new NativeMenuItem { IsEnabled = false };
		var menu = new NativeMenu
		{
			status,
			error,
			new NativeMenuItemSeparator(),
			new NativeMenuItem("Start") { Command = vm.StartCommand },
			new NativeMenuItem("Stop") { Command = vm.StopCommand },
			new NativeMenuItem("Restart") { Command = vm.RestartCommand },
			new NativeMenuItemSeparator(),
			new NativeMenuItem("Install service…") { Command = vm.InstallCommand },
			new NativeMenuItem("Uninstall service") { Command = vm.UninstallCommand },
			new NativeMenuItemSeparator(),
			Item("Settings…", () => ShowSingle(desktop, () => new SettingsWindow { DataContext = new SettingsViewModel(dataDir, new RegistryAutoStart(), service) })),
			Item("Logs…", () => ShowSingle(desktop, () => new LogsWindow { DataContext = new LogsViewModel(dataDir) })),
			Item("Open UI", () => Open(SettingsViewModel.UiAddress(dataDir))),
			update,
			new NativeMenuItemSeparator(),
			Item("Exit", () => desktop.Shutdown()),
		};

		var icon = new TrayIcon
		{
			Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://AiChromeProxy.Tray/Assets/tray.ico"))),
			Menu = menu,
		};

		void Render()
		{
			status.Header = vm.StatusText;
			icon.ToolTipText = "AI Chrome Proxy: " + vm.StatusText;
			error.Header = vm.Error;
			error.IsVisible = vm.Error is not null;
			update.Header = vm.UpdateText;
			update.IsVisible = vm.IsUpdateAvailable;
		}

		vm.PropertyChanged += (_, _) => Render();
		vm.Refresh();
		Render();
		TrayIcon.SetIcons(this, [icon]);
		_ = vm.RunUpdateChecksAsync(TimeProvider.System, CancellationToken.None);

		// ponytail: polls the SCM every 2 s for the tray's lifetime (one cheap query); restrict to "menu open" via NativeMenu.Opening/Closed if it ever matters.
		DispatcherTimer.Run(
			() =>
			{
				vm.Refresh();
				return true;
			},
			StatusPollInterval);
	}
}
```

Write `src/AiChromeProxy.Tray/Program.cs`:

```csharp
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.Updates;
using Avalonia;
using Avalonia.Controls;
using Velopack;

namespace AiChromeProxy.Tray;

internal static class Program
{
	[STAThread]
	public static int Main(string[] args)
	{
		// Must run first: handles Velopack's --veloapp-* hook invocations and exits.
		VelopackApp.Build()
			.SetAutoApplyOnStartup(false) // updates are applied only by UpdateOrchestrator, after it stopped the service
			.OnAfterUpdateFastCallback(_ => VelopackHooks.AfterUpdate(new WindowsServiceControl()))
			.OnBeforeUninstallFastCallback(_ => VelopackHooks.BeforeUninstall(new WindowsServiceControl(), AdminCommand.RunElevatedAsync, new RegistryAutoStart()))
			.Run();

		switch (AdminCommand.Parse(args)?.Command)
		{
			case AdminCommand.Uninstall:
				return AdminCommand.RunUninstall(new WindowsServiceControl());
			case AdminCommand.Install:
				return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
		}

		// One tray per user session (Start with Windows plus a manual launch must not show two icons).
		using var single = new Mutex(initiallyOwned: true, @"Local\AiChromeProxy.Tray", out var isFirst);
		return isFirst ? BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown) : 0;
	}

	/// <summary>Also used by the Avalonia previewer.</summary>
	public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
```

Write `src/AiChromeProxy.Tray/Updates/IUpdateSource.cs`:

```csharp
namespace AiChromeProxy.Tray.Updates;

/// <summary>Release feed + installer seam (Velopack in production, a fake in tests).</summary>
public interface IUpdateSource
{
	/// <returns>The newer version available, or null.</returns>
	Task<string?> CheckAsync(CancellationToken ct);

	/// <summary>Downloads the version found by the last <see cref="CheckAsync"/>.</summary>
	Task DownloadAsync(CancellationToken ct);

	/// <summary>Exits this process, applies the downloaded update and restarts the tray; returns only by throwing.</summary>
	void ApplyAndRestart();
}
```

Write `src/AiChromeProxy.Tray/Updates/UpdateOrchestrator.cs`:

```csharp
using AiChromeProxy.Tray.Services;

namespace AiChromeProxy.Tray.Updates;

/// <summary>
/// Check at start and every 24 h; update = download, stop the service (its exe lives in the folder being replaced), apply and restart the tray.
/// A failed download leaves everything untouched; a failed apply starts the service again.
/// </summary>
/// <param name="pendingMarker">File recording "service stopped for an update", so the next tray start resumes it if Update.exe failed out of process.</param>
public sealed class UpdateOrchestrator(IUpdateSource source, IServiceControl service, string pendingMarker)
{
	public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

	public string? AvailableVersion { get; private set; }

	public async Task<string?> CheckAsync(CancellationToken ct) => AvailableVersion = await source.CheckAsync(ct);

	/// <summary>Checks now and then every <see cref="CheckInterval"/> until cancelled; results and errors go to <paramref name="report"/>, never thrown.</summary>
	public async Task RunChecksAsync(TimeProvider time, Action<string?, Exception?> report, CancellationToken ct)
	{
		using var timer = new PeriodicTimer(CheckInterval, time);
		do
		{
			try
			{
				report(await CheckAsync(ct), null);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				report(null, ex);
			}
		}
		while (await timer.WaitForNextTickAsync(ct));
	}

	public async Task UpdateAsync(CancellationToken ct)
	{
		await source.DownloadAsync(ct);

		var wasRunning = service.GetState() is ServiceState.Running or ServiceState.Starting;
		if (wasRunning)
		{
			await File.WriteAllTextAsync(pendingMarker, string.Empty, ct);
			await service.StopAsync(ct);
		}

		try
		{
			source.ApplyAndRestart();
		}
		catch
		{
			if (wasRunning)
			{
				await service.StartAsync(CancellationToken.None);
				File.Delete(pendingMarker);
			}

			throw;
		}
	}

	/// <summary>At tray start: the service was stopped for an update that never came back (Update.exe failed or was killed), so start it again.</summary>
	public async Task ResumeServiceAfterUpdateAsync(CancellationToken ct)
	{
		if (!File.Exists(pendingMarker))
		{
			return;
		}

		File.Delete(pendingMarker);
		if (service.GetState() == ServiceState.Stopped)
		{
			await service.StartAsync(ct);
		}
	}
}
```

Write `src/AiChromeProxy.Tray/Updates/VelopackHooks.cs`:

```csharp
using AiChromeProxy.Tray.Services;

namespace AiChromeProxy.Tray.Updates;

/// <summary>Velopack fast callbacks: best effort, never throw, and stay inside Velopack's time limits (15 s after update, 30 s before uninstall).</summary>
public static class VelopackHooks
{
	private static readonly TimeSpan AfterUpdateWait = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan BeforeUninstallWait = TimeSpan.FromSeconds(25);

	/// <summary>The new version is in place: start the service the update stopped (the SCM finishes starting it even if this process is cut off).</summary>
	public static void AfterUpdate(IServiceControl service)
	{
		try
		{
			if (service.GetState() == ServiceState.Stopped)
			{
				service.StartAsync(CancellationToken.None).Wait(AfterUpdateWait);
			}
		}
		catch (Exception)
		{
			// The next tray start shows the state; nothing to report to from a hook.
		}
	}

	/// <summary>Removes the service (elevated <c>--admin uninstall</c>) and the "Start with Windows" entry; the data directory is kept.</summary>
	public static void BeforeUninstall(IServiceControl service, Func<string, Task<int?>> runElevated, IAutoStart autoStart)
	{
		try
		{
			autoStart.IsEnabled = false;
			if (service.GetState() != ServiceState.NotInstalled)
			{
				runElevated(AdminCommand.Uninstall).Wait(BeforeUninstallWait);
			}
		}
		catch (Exception)
		{
			// Uninstall must not fail because of the service; it can be removed later with "sc delete AiChromeProxy".
		}
	}
}
```

Write `src/AiChromeProxy.Tray/Updates/VelopackUpdateSource.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using Velopack;
using Velopack.Sources;

namespace AiChromeProxy.Tray.Updates;

/// <summary>
/// GitHub Releases through Velopack. No updates when the tray was not installed by Velopack (dotnet run, tests)
/// or the build has no repository (<c>-p:UpdateRepository=</c>, set by the release workflow).
/// Excluded from coverage: talks to GitHub and runs Velopack's Update.exe; the flow around it is in <see cref="UpdateOrchestrator"/>.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class VelopackUpdateSource(string? repositoryUrl) : IUpdateSource
{
	private UpdateManager? _manager;
	private UpdateInfo? _update;

	public async Task<string?> CheckAsync(CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(repositoryUrl))
		{
			return null;
		}

		_manager ??= new UpdateManager(new GithubSource(repositoryUrl, accessToken: null, prerelease: false));
		if (!_manager.IsInstalled)
		{
			return null;
		}

		_update = await _manager.CheckForUpdatesAsync();
		return _update?.TargetFullRelease.Version.ToString();
	}

	public Task DownloadAsync(CancellationToken ct) => Manager().DownloadUpdatesAsync(Pending(), null, ct);

	public void ApplyAndRestart() => Manager().ApplyUpdatesAndRestart(Pending().TargetFullRelease);

	private UpdateManager Manager() => _manager ?? throw new InvalidOperationException("Check for updates first.");

	private UpdateInfo Pending() => _update ?? throw new InvalidOperationException("No update available.");
}
```

Write `src/AiChromeProxy.Tray/ViewModels/TrayViewModel.cs`:

```csharp
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiChromeProxy.Tray.ViewModels;

/// <summary>Tray menu state: service status line, Start / Stop / Restart, the elevated Install / Uninstall and "Update to vX".</summary>
/// <param name="runElevated">Runs <c>--admin &lt;command&gt;</c> elevated; returns its exit code, or null when UAC was declined.</param>
public sealed partial class TrayViewModel(IServiceControl service, Func<string, Task<int?>> runElevated, UpdateOrchestrator updates) : ObservableObject
{
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(StatusText))]
	[NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(RestartCommand), nameof(UninstallCommand))]
	public partial ServiceState State { get; private set; }

	/// <summary>Last failure of a menu action or status query; cleared when the next action starts.</summary>
	[ObservableProperty]
	public partial string? Error { get; private set; }

	/// <summary>Newer release found by the last check; null hides the menu item.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(UpdateText), nameof(IsUpdateAvailable))]
	public partial string? UpdateVersion { get; private set; }

	public string UpdateText => $"Update to v{UpdateVersion}";

	public bool IsUpdateAvailable => UpdateVersion is not null;

	public string StatusText => State switch
	{
		ServiceState.NotInstalled => "Service: not installed",
		ServiceState.Stopped => "Service: stopped",
		ServiceState.Starting => "Service: starting…",
		ServiceState.Stopping => "Service: stopping…",
		_ => "Service: running",
	};

	public void Refresh()
	{
		try
		{
			State = service.GetState();
		}
		catch (Exception ex)
		{
			Error = ex.Message;
		}
	}

	/// <summary>Tray start-up: resume a service an interrupted update left stopped, then check for updates now and every 24 h.</summary>
	public async Task RunUpdateChecksAsync(TimeProvider time, CancellationToken ct)
	{
		await RunAsync(updates.ResumeServiceAfterUpdateAsync);
		await updates.RunChecksAsync(
			time,
			(version, error) =>
			{
				UpdateVersion = version;
				if (error is not null)
				{
					Error = $"Update check failed: {error.Message}";
				}
			},
			ct);
	}

	[RelayCommand(CanExecute = nameof(CanStart))]
	private Task StartAsync() => RunAsync(service.StartAsync);

	[RelayCommand(CanExecute = nameof(CanStop))]
	private Task StopAsync() => RunAsync(service.StopAsync);

	[RelayCommand(CanExecute = nameof(CanStop))]
	private Task RestartAsync() => RunAsync(async ct =>
	{
		await service.StopAsync(ct);
		await service.StartAsync(ct);
	});

	/// <summary>Always available: on an installed service it updates the account and password (e.g. after a Windows password change).</summary>
	[RelayCommand]
	private Task InstallAsync() => RunAsync(_ => ElevateAsync(AdminCommand.Install));

	[RelayCommand(CanExecute = nameof(CanUninstall))]
	private Task UninstallAsync() => RunAsync(_ => ElevateAsync(AdminCommand.Uninstall));

	/// <summary>Download, stop the service, apply and restart the tray; on failure the current version keeps running and the error is shown.</summary>
	[RelayCommand]
	private Task UpdateAsync() => RunAsync(updates.UpdateAsync);

	private bool CanStart() => State == ServiceState.Stopped;

	private bool CanUninstall() => State != ServiceState.NotInstalled;

	private bool CanStop() => State is ServiceState.Running or ServiceState.Starting;

	private async Task ElevateAsync(string command)
	{
		var exitCode = await runElevated(command);
		if (exitCode is not (null or 0))
		{
			throw new InvalidOperationException($"Service {command} did not complete (exit code {exitCode}).");
		}
	}

	private async Task RunAsync(Func<CancellationToken, Task> action)
	{
		Error = null;
		try
		{
			await action(CancellationToken.None);
		}
		catch (Exception ex)
		{
			Error = ex.Message;
		}

		Refresh();
	}
}
```

- [ ] **Step 4: Build and run the gate (GREEN)**

Run: `dotnet build -c Release` → Expected: `0 Warning(s)`, `0 Error(s)`.
Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total` then `echo $?` → Expected: `total: 203, failed: 0`, exit code 0.

- [ ] **Step 5: Commit**

```bash
git add -A
git status --short   # every file listed above must appear; nothing under a Logs/ folder
git commit -m "feat(tray): Velopack hooks and update orchestration"
```

---

### Task 10: ci: release workflow (tag v*) building the Velopack Setup.exe

**Files:**
- Create: `.github/workflows/release.yml`

Every code block below is the **complete final content** of that file (verified on the prototype: builds with 0 warnings, gate passes). Write it exactly; for modified files replace the whole file.

- [ ] **Step 1: Write the files**

Write `.github/workflows/release.yml`:

```yaml
name: release

on:
  push:
    tags: ['v*']

# Creating the GitHub Release needs write access; ci.yml stays read-only.
permissions:
  contents: write

jobs:
  release:
    runs-on: windows-latest
    env:
      # Keep in step with the Velopack package version in src/AiChromeProxy.Tray.
      VPK_VERSION: 1.2.161
    steps:
      - uses: actions/checkout@v5

      - uses: actions/setup-dotnet@v5
        with:
          global-json-file: global.json

      - name: Version from tag (v1.2.3 -> 1.2.3)
        shell: pwsh
        run: |
          $version = "${{ github.ref_name }}".TrimStart("v")
          "VERSION=$version" >> $env:GITHUB_ENV

      - name: Restore
        run: dotnet restore

      - name: Build (StyleCop errors fail the build)
        run: dotnet build -c Release --no-restore

      - name: Test + coverage gate (line >= 85%)
        run: dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total

      # Package layout: the tray at the root (main exe), the Server in server\ (the service binary path).
      # One command per step: a multi-line pwsh step only fails on the last command's exit code.
      - name: Publish Tray (self-contained, win-x64)
        run: dotnet publish src/AiChromeProxy.Tray -c Release -r win-x64 --self-contained -p:Version=${{ env.VERSION }} -p:UpdateRepository=${{ github.server_url }}/${{ github.repository }} -o publish

      - name: Publish Server into server\ (self-contained, win-x64)
        run: dotnet publish src/AiChromeProxy.Server -c Release -r win-x64 --self-contained -p:Version=${{ env.VERSION }} -o publish/server

      - name: Install vpk
        run: dotnet tool install -g vpk --version ${{ env.VPK_VERSION }}

      - name: Pack (Setup.exe + update packages)
        run: vpk pack --packId AiChromeProxy --packVersion ${{ env.VERSION }} --runtime win-x64 --packDir publish --mainExe AiChromeProxy.Tray.exe --packTitle "AI Chrome Proxy" --packAuthors "AI Chrome Proxy contributors" --icon src/AiChromeProxy.Tray/Assets/tray.ico --outputDir releases

      - name: Upload GitHub Release
        run: vpk upload github --repoUrl ${{ github.server_url }}/${{ github.repository }} --token ${{ secrets.GITHUB_TOKEN }} --publish --releaseName "AI Chrome Proxy ${{ github.ref_name }}" --tag ${{ github.ref_name }} --outputDir releases
```

- [ ] **Step 2: Verify**

Run: `dotnet build -c Release` → `0 Error(s)`; `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total` then `echo $?` → `total: 203, failed: 0`, exit 0.
Validate YAML: `python -c "import yaml;yaml.safe_load(open('.github/workflows/release.yml'));print('ok')"` → `ok`.

- [ ] **Step 3: Commit**

```bash
git add -A
git status --short   # every file listed above must appear; nothing under a Logs/ folder
git commit -m "ci: release workflow (tag v*) building the Velopack Setup.exe"
```

---

### Task 11: test(tray): Avalonia headless smoke test for the three windows

**Files:**
- Create: `tests/AiChromeProxy.Tests/Tray/WindowsSmokeTests.cs`
- Modify: `tests/AiChromeProxy.Tests/AiChromeProxy.Tests.csproj`

Every code block below is the **complete final content** of that file (verified on the prototype: builds with 0 warnings, gate passes). Write it exactly; for modified files replace the whole file.

- [ ] **Step 1: Write the tests**

Write `tests/AiChromeProxy.Tests/AiChromeProxy.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Avalonia.Headless" Version="12.1.3" />
    <PackageReference Include="coverlet.MTP" Version="10.1.0" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.12" />
    <PackageReference Include="Microsoft.AspNetCore.SignalR.Client" Version="10.0.12" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.10.0" />
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
    <ProjectReference Include="..\..\src\AiChromeProxy.Tray\AiChromeProxy.Tray.csproj" />
  </ItemGroup>

</Project>
```

Write `tests/AiChromeProxy.Tests/Tray/WindowsSmokeTests.cs`:

```csharp
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tray;
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.ViewModels;
using AiChromeProxy.Tray.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;

namespace AiChromeProxy.Tests.Tray;

/// <summary>Avalonia headless (no desktop window): each window's XAML loads and binds to its view model.</summary>
public sealed class WindowsSmokeTests : IDisposable
{
	private readonly DataDirectory _dataDir = new(Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N")));

	/// <summary>Entry point for <see cref="HeadlessUnitTestSession.StartNew(Type)"/>.</summary>
	public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());

	[Fact]
	public async Task Windows_LoadXaml_AndBind()
	{
		Directory.CreateDirectory(_dataDir.Logs);
		await File.WriteAllTextAsync(
			Path.Combine(_dataDir.Logs, "server-20261002.clef"),
			"""{"@t":"2026-10-02T10:00:00Z","@m":"Now listening"}""" + "\n",
			TestContext.Current.CancellationToken);

		var session = HeadlessUnitTestSession.StartNew(typeof(WindowsSmokeTests));
		try
		{
			await session.Dispatch(
				() =>
				{
					var settings = new SettingsWindow { DataContext = new SettingsViewModel(_dataDir, new FakeAutoStart(), new FakeServiceControl()) };
					settings.Show();
					Assert.Contains(TextBoxes(settings), t => t.Text == "5180");
					settings.Close();

					var logsVm = new LogsViewModel(_dataDir);
					var logs = new LogsWindow { DataContext = logsVm };
					logs.Show();
					Assert.Same(logsVm.Entries, logs.FindControl<ListBox>("EntryList")!.ItemsSource);
					Assert.Single(logsVm.Entries);
					logs.Close();

					var installVm = new InstallViewModel(new FakeServiceControl(ServiceState.NotInstalled), @"HOME\jane") { Password = "secret" };
					var install = new InstallWindow(installVm);
					var closed = false;
					install.Closed += (_, _) => closed = true;
					install.Show();
					Assert.Contains(TextBoxes(install), t => t.Text == @"HOME\jane");
					Assert.Contains(TextBoxes(install), t => t.PasswordChar == '●' && t.Text == "secret");
					installVm.InstallCommand.Execute(null);
					Assert.True(SpinUntil(() => closed), "install window closes once installed");
				},
				TestContext.Current.CancellationToken);
		}
		finally
		{
			// The await above resumes on the Avalonia dispatcher thread; Dispose joins that thread, so it must run elsewhere.
			await Task.Run(session.Dispose, TestContext.Current.CancellationToken);
		}
	}

	public void Dispose() => Directory.Delete(_dataDir.Root, recursive: true);

	private static List<TextBox> TextBoxes(Window window) => [.. window.GetLogicalDescendants().OfType<TextBox>()];

	private static bool SpinUntil(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (!condition() && DateTime.UtcNow < deadline)
		{
			Avalonia.Threading.Dispatcher.UIThread.RunJobs();
		}

		return condition();
	}
}
```

- [ ] **Step 2: Prove the test can fail (RED, not committed)**

Temporarily break one binding in `src/AiChromeProxy.Tray/Views/SettingsWindow.axaml` (e.g. bind the Port box to `Audience`), run `dotnet build -c Release` and `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total` → Expected: `Windows_LoadXaml_AndBind` fails. Restore the file.

- [ ] **Step 3: GREEN**

Run: `dotnet build -c Release` → `0 Error(s)`; `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total` then `echo $?` → `total: 204, failed: 0`, exit 0.

- [ ] **Step 4: Commit**

```bash
git add -A
git status --short   # every file listed above must appear; nothing under a Logs/ folder
git commit -m "test(tray): Avalonia headless smoke test for the three windows"
```

---

### Task 12: docs: Windows host guide, Server__PublicHost, project list

**Files:**
- Create: `docs/windows-host.md`
- Modify: `CLAUDE.md`, `README.md`, `docs/testing.md`

Every code block below is the **complete final content** of that file (verified on the prototype: builds with 0 warnings, gate passes). Write it exactly; for modified files replace the whole file.

- [ ] **Step 1: Write the files**

Write `CLAUDE.md`:

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
  - `src/AiChromeProxy.Tray` — Windows tray app (Avalonia, `net10.0-windows`, CommunityToolkit.Mvvm, Velopack) → Domain, Infrastructure only: service status/control (`IServiceControl`), elevated `--admin install|uninstall`, settings, logs, updates. See [docs/windows-host.md](docs/windows-host.md).
- Tests: `tests/AiChromeProxy.Tests` (xunit v3, CI; `net10.0-windows` because it covers the tray), `tests/AiChromeProxy.E2E` (Reqnroll + Playwright), `tests/load` (k6), `benchmarks/AiChromeProxy.Benchmarks` (BenchmarkDotNet) — see [docs/testing.md](docs/testing.md).
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
- Logging — `ILogger<T>` via DI; the Server writes through Serilog (console + CLEF files in `<DataDir>\logs` when a data directory is in use).
- Windows interop that needs elevation or changes the machine (SCM, LSA, registry, Velopack) sits behind a seam (`IServiceControl`, `IAutoStart`, `IUpdateSource`) or in a thin `[ExcludeFromCodeCoverage]` class; the decisions it applies stay in tested code (`ServiceSetup`). Tests never install services, grant rights or write the registry.
- Pure logic (SyncEngine, manifest diff, hash-guard, path normalization, stream-json parser) has no browser/IO dependencies and is covered by xUnit.
- Never commit secrets: `appsettings.json` holds non-secret defaults only; per-machine values and secrets come from environment variables or `%ProgramData%\AiChromeProxy\appsettings.json` (written by the tray, outside the repo).

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

Write `README.md`:

````markdown
# ai-chrome-proxy

Use Claude Code on your home server to work on a repo that lives on a locked-down machine where only Chrome is available.

Chrome opens a web app (served via Cloudflare Tunnel + Access) that syncs the repo folder to the home server, gives you a VS Code-like navigator and a Claude chat with mermaid diagrams and code highlighting, and writes Claude's edits back.

**Status:** skeleton — transport + Cloudflare Access, Windows host (service, tray, installer). See [architecture](docs/superpowers/specs/2026-10-02-architecture-design.md) and [setup](docs/setup/cloudflare.md).

## Install on a Windows home server

Download `AiChromeProxy-win-Setup.exe` from Releases: it installs a tray app that sets up the Server as a Windows service, edits its settings, shows its logs and installs updates. See [docs/windows-host.md](docs/windows-host.md).

## Run locally (no Cloudflare)

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:CloudflareAccess__Enabled = "false"       # disables the Access check; honored only in Development
dotnet run --project src/AiChromeProxy.Server --no-launch-profile
# open http://127.0.0.1:5180/
```

> You are responsible for complying with your organization's policies on moving code off a machine.

## License

Apache License 2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE).
````

Write `docs/testing.md`:

````markdown
# Testing

Four layers. Only the first runs in CI; the others are local tools.

| Layer | Location | What it covers | Runs |
|---|---|---|---|
| Unit + integration + architecture | `tests/AiChromeProxy.Tests` (`Domain/`, `Application/`, `Infrastructure/`, `Server/`, `Tray/`, `Architecture/`) | Envelope contract, routing, handlers, Cloudflare Access validation and middleware, the real Server pipeline over SignalR (in-memory `TestServer`), host filtering and startup validation, data directory config + CLEF logging, tray view models / CLEF parser / update orchestration / service setup (fakes for the SCM, registry and Velopack), the tray windows on Avalonia headless, layer dependency rules (NetArchTest) | CI + local, 85% line-coverage gate |
| E2E BDD | `tests/AiChromeProxy.E2E` — Reqnroll + Playwright | The real app in a real browser: the status page connects, Ping round-trips | local |
| Load | `tests/load` — k6 | Concurrent SignalR connections pinging the hub; ping→pong latency and error rate | local |
| Micro-benchmarks | `benchmarks/AiChromeProxy.Benchmarks` — BenchmarkDotNet | Hot paths: `Envelope.Create`/serialization, `EnvelopeRouter.RouteAsync`, Access token validation | local |

Commands below run from the repo root. Shell snippets are Windows PowerShell 5.1 (`pwsh` works the same).

## Unit + integration + architecture (xunit v3)

xunit v3 on Microsoft.Testing.Platform (opted in via `global.json`), coverage via `coverlet.MTP`. The project targets `net10.0-windows` (it references the tray), so it runs on Windows only.

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
- Tray depends only on Domain and Infrastructure (not on Application, Server, Client or ASP.NET Core).

Excluded from coverage (`testconfig.json` and `[ExcludeFromCodeCoverage]`): `Program.cs`, `*.razor`, view code-behind `*.axaml.cs` (UI glue; the windows are smoke-tested on Avalonia headless), and the members that change the machine or the network — `ServiceInstaller` (P/Invoke: SCM, LSA, LogonUser), `WindowsServiceControl` start/stop/install/uninstall, `RegistryAutoStart`, `VelopackUpdateSource`, `AdminCommand.RunElevatedAsync` (UAC). These are covered by the manual checklist in [windows-host.md](windows-host.md).

Tray tests never install services, grant rights, write the registry, show a desktop window or trigger UAC; read-only SCM queries (`EventLog` status, an unknown service) are fine.

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

Write `docs/windows-host.md`:

````markdown
# Windows host

One `Setup.exe` installs everything on a Windows home server. The Server then runs as a Windows service that starts after power-on **without anyone logging in**, under your own account (so `claude` sees your `~/.claude`). A tray app shows the status, starts and stops the service, edits the settings, shows the logs and installs updates.

Before exposing the Server, set up the tunnel and Access: [setup/cloudflare.md](setup/cloudflare.md).

## Where things live

| What | Where |
|---|---|
| Tray app (and `Update.exe`) | `%LocalAppData%\AiChromeProxy\` (per-user Velopack install; the app itself is in `current\`) |
| Server (the service binary) | `%LocalAppData%\AiChromeProxy\current\server\AiChromeProxy.Server.exe` |
| Settings | `%ProgramData%\AiChromeProxy\appsettings.json` |
| Logs | `%ProgramData%\AiChromeProxy\logs\server-YYYYMMDD.clef` (one JSON event per line, daily, 14 files kept) |

`AICP_DATA_DIR` overrides `%ProgramData%\AiChromeProxy` (tests, local runs). The settings file is read only by the service or when `AICP_DATA_DIR` is set; environment variables still override it.

## Install

1. Download `AiChromeProxy-win-Setup.exe` from the project's GitHub Releases and run it. The installer is not code-signed, so SmartScreen warns on first run: **More info → Run anyway**. No admin rights and no .NET install are needed.
2. The tray icon appears (and a Start menu / desktop shortcut **AI Chrome Proxy**).
3. **Settings…** — enter the Cloudflare Access team domain, the application audience (AUD tag), the public host name of the tunnel and the local port (default `5180`). The form checks the values with the same rules the Server uses at startup. Optionally tick **Start the tray with Windows**.
4. **Install service…** — Windows asks for administrator approval (UAC), then a dialog asks for the account the service runs as (default: you) and its **Windows password**:
   - use the account password, not the Windows Hello PIN (for a Microsoft account, its Microsoft account password);
   - an account without a password cannot run a service — set one first;
   - the password goes to the Service Control Manager only; it is never written to a file or a command line.

   The installer grants the account *Log on as a service*, checks the password, creates the `AiChromeProxy` service (automatic start, restart after 10 s up to three times, counter reset after a day), lets you start and stop it without UAC, creates `%ProgramData%\AiChromeProxy` with full control for the account and starts the service.

If you change your Windows password later, the service can no longer log on: run **Install service…** again — on an installed service it updates the account and password.

## Tray menu

| Item | What it does |
|---|---|
| Service: running / stopped / starting… / stopping… / not installed | Status from the Service Control Manager, refreshed every 2 s. A second, greyed line shows the last error, if any. |
| Start / Stop / Restart | Controls the service; no UAC needed after install. |
| Install service… / Uninstall service | The only actions that need administrator approval. |
| Settings… | Edits `appsettings.json`. After **Save**, **Restart service** applies the change. |
| Logs… | The log viewer (below). |
| Open UI | Opens `https://<public host>/` (through the tunnel and Access); without a public host, `http://127.0.0.1:<port>/`. Local requests carry no Access token, so with Access on the loopback address answers `401`. |
| Update to vX | Shown only when a newer release exists (below). |
| Exit | Closes the tray; the service keeps running. |

## Logs

**Logs…** lists the log files, newest first, and shows the selected one: time, level, message and exception. Filter by minimum level and by text (message or exception, case-insensitive). **Follow** appends new entries every second and moves to the next file at midnight. The files are read with shared access while the service writes them.

If the service does not start, the reason is the last `Fatal` entry (for example an invalid `Server:PublicHost`); the Service Control Manager also logs failures in Event Viewer → Windows Logs → System.

## Updates

The tray checks GitHub Releases at start and then every 24 hours. **Update to vX** downloads the release, stops the service (its files are being replaced), applies the update and restarts the tray; the new version starts the service again. A failed download leaves everything as it was; a failed apply starts the service again and the error shows in the tray menu. If an update is interrupted outside the tray, the next tray start resumes the service.

## Uninstall

**Windows Settings → Apps → Installed apps → AI Chrome Proxy → Uninstall.** Windows asks for administrator approval to remove the service. `%ProgramData%\AiChromeProxy` (settings and logs) is kept. To remove everything by hand (elevated PowerShell):

```powershell
sc.exe stop AiChromeProxy; sc.exe delete AiChromeProxy
Remove-Item -Recurse "$env:ProgramData\AiChromeProxy"
```

## Cutting a release

```powershell
git tag v0.1.0
git push origin v0.1.0
```

`.github/workflows/release.yml` (tag `v*`, `windows-latest`) restores, builds, runs the xunit gate, publishes the tray and the Server self-contained for `win-x64`, packs them with Velopack (`vpk pack`) and uploads `AiChromeProxy-win-Setup.exe`, the portable zip and the update packages to a GitHub Release named after the tag. Installed trays update from the releases of the repository the release was built in.

Package layout, to reproduce locally (vpk as a local tool, not global):

```powershell
dotnet tool install vpk --version 1.2.161 --tool-path .tools
dotnet publish src/AiChromeProxy.Tray -c Release -r win-x64 --self-contained -p:Version=0.1.0 -o publish
dotnet publish src/AiChromeProxy.Server -c Release -r win-x64 --self-contained -p:Version=0.1.0 -o publish/server
.tools/vpk pack --packId AiChromeProxy --packVersion 0.1.0 --runtime win-x64 --packDir publish --mainExe AiChromeProxy.Tray.exe --packTitle "AI Chrome Proxy" --icon src/AiChromeProxy.Tray/Assets/tray.ico --outputDir releases
```

`releases\` then holds `AiChromeProxy-win-Setup.exe`, `AiChromeProxy-win-Portable.zip`, `AiChromeProxy-0.1.0-full.nupkg` and the `releases.win.json` feed. Inside the package the tray is at the root and the Server in `server\`, which installs as `%LocalAppData%\AiChromeProxy\current\server\`.

## Manual acceptance checklist

Run on a real Windows machine (none of this runs in CI: it needs a desktop session, UAC and a real service):

1. `Setup.exe` installs; the tray icon appears.
2. **Install service…** (UAC + password) → status *running*; `netstat -ano | findstr :5180` shows only `127.0.0.1:5180`.
3. Reboot and do **not** log in; from another machine the UI loads through the tunnel.
4. Log in; the tray shows *running*; **Logs…** shows the startup entries; the level/text filters and **Follow** work.
5. **Stop** / **Start** / **Restart** from the tray work without a UAC prompt.
6. Publish a newer release → the tray offers **Update to vX** → after the update the service runs the new version.
7. Uninstall from Windows Settings → the service is removed, `%ProgramData%\AiChromeProxy` is kept.

Also check once:

8. **Install service…** with a wrong password → a clear "wrong password" error and no service created; then the right password works.
9. `sc.exe qfailure AiChromeProxy` shows restart / 10000 ms three times, reset period 86400.
10. **Start the tray with Windows** → the tray is there after the next login; unticked → it is not.
````

- [ ] **Step 2: Verify**

Run: `dotnet build -c Release` → `0 Error(s)`; `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total` then `echo $?` → `total: 204, failed: 0`, exit 0.

- [ ] **Step 3: Commit**

```bash
git add -A
git status --short   # every file listed above must appear; nothing under a Logs/ folder
git commit -m "docs: Windows host guide, Server__PublicHost, project list"
```
