using System.Text.Json.Nodes;
using AiChromeProxy.Application.Chat;
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;
using AiChromeProxy.Domain.Sync;
using AiChromeProxy.Tests.Server;
using Microsoft.Extensions.Time.Testing;

namespace AiChromeProxy.Tests.Application;

public sealed class PermissionBrokerTests
{
	private const string Repo = "repo";
	private const string RunId = "run1";

	private readonly FakeTimeProvider _time = new();
	private readonly MemoryProjectStore _projects = new();
	private readonly ListLogger<PermissionBroker> _logger = new();
	private readonly PermissionBroker _broker;
	private readonly List<ChatEvent> _published = [];
	private readonly string _token;

	public PermissionBrokerTests()
	{
		_broker = new PermissionBroker(_projects, _time, _logger);
		_token = _broker.StartRun(RunId, Repo, "C:/mirror/repo", Publish);
	}

	private static JsonObject Ls => new() { ["command"] = "ls -la" };

	private List<ChatEvent> Published
	{
		get
		{
			lock (_published)
			{
				return [.. _published];
			}
		}
	}

	[Fact]
	public async Task Allow_PermissionPushed_AnsweredAllow_Resolved()
	{
		var request = _broker.RequestAsync(RunId, "Bash", Ls, TestContext.Current.CancellationToken);

		var permission = await PermissionAsync();
		Assert.False(request.IsCompleted);
		Assert.Equal((ChatEventKinds.Permission, RunId, "Bash", "ls -la"), (permission.Kind, permission.RunId, permission.Name, permission.Summary));
		Assert.Matches("^[0-9a-f]{32}$", permission.RequestId!);

		_broker.Answer(RunId, permission.RequestId, ChatDecisions.Allow);

		Assert.True(await request);
		Assert.Equal(new ChatEvent(string.Empty, RunId, 0, ChatEventKinds.PermissionResolved, RequestId: permission.RequestId, Decision: ChatDecisions.Allow), Published[^1]);
		Assert.Null(_projects.GetSettings(Repo).AgentAllowedTools);
	}

	[Fact]
	public async Task Deny_AnsweredDeny_Resolved()
	{
		var request = _broker.RequestAsync(RunId, "WebFetch", new JsonObject { ["url"] = "https://example.com" }, TestContext.Current.CancellationToken);
		var permission = await PermissionAsync();

		_broker.Answer(RunId, permission.RequestId, ChatDecisions.Deny);

		Assert.False(await request);
		Assert.Equal(ChatDecisions.Deny, Published[^1].Decision);
	}

	[Fact]
	public async Task AllowAlways_BashRuleSaved_SameCommandAllowedAtOnce_OtherCommandAsks()
	{
		_projects.SaveSettings(Repo, new ProjectSettings { AgentModel = "opus", AgentAllowedTools = ["Read"] });
		var request = _broker.RequestAsync(RunId, "Bash", Ls, TestContext.Current.CancellationToken);
		var permission = await PermissionAsync();

		_broker.Answer(RunId, permission.RequestId, ChatDecisions.AllowAlways);

		Assert.True(await request);
		Assert.Equal(ChatDecisions.AllowAlways, Published[^1].Decision);
		Assert.Equal(["Read", "Bash(ls -la)"], _projects.GetSettings(Repo).AgentAllowedTools!);
		Assert.Equal("opus", _projects.GetSettings(Repo).AgentModel);
		var count = Published.Count;

		Assert.True(await _broker.RequestAsync(RunId, "Bash", Ls, TestContext.Current.CancellationToken));
		Assert.Equal(count, Published.Count);

		var other = _broker.RequestAsync(RunId, "Bash", new JsonObject { ["command"] = "rm -rf x" }, TestContext.Current.CancellationToken);
		Assert.Equal(count + 1, Published.Count);
		Assert.False(other.IsCompleted);
		_broker.CancelRun(RunId);
		Assert.False(await other);
	}

	[Fact]
	public async Task AllowAlways_OtherTool_ToolNameRule_NoDuplicates()
	{
		var first = _broker.RequestAsync(RunId, "WebFetch", new JsonObject { ["url"] = "https://example.com/a" }, TestContext.Current.CancellationToken);
		var second = _broker.RequestAsync(RunId, "WebFetch", new JsonObject { ["url"] = "https://example.com/b" }, TestContext.Current.CancellationToken);
		var ids = Published.Where(e => e.Kind == ChatEventKinds.Permission).Select(e => e.RequestId).ToList();
		Assert.Equal(2, ids.Count);

		_broker.Answer(RunId, ids[0], ChatDecisions.AllowAlways);
		_broker.Answer(RunId, ids[1], ChatDecisions.AllowAlways);

		Assert.True(await first);
		Assert.True(await second);
		Assert.Equal(["WebFetch"], _projects.GetSettings(Repo).AgentAllowedTools!);
	}

	[Fact]
	public async Task BashWithoutCommand_NoRuleSaved()
	{
		var request = _broker.RequestAsync(RunId, "Bash", new JsonObject(), TestContext.Current.CancellationToken);

		_broker.Answer(RunId, (await PermissionAsync()).RequestId, ChatDecisions.AllowAlways);

		Assert.True(await request);
		Assert.Null(_projects.GetSettings(Repo).AgentAllowedTools);
	}

	[Fact]
	public async Task NoAnswerFor10Minutes_Deny()
	{
		var request = _broker.RequestAsync(RunId, "Bash", Ls, TestContext.Current.CancellationToken);
		await PermissionAsync();

		_time.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
		Assert.False(request.IsCompleted);
		_time.Advance(TimeSpan.FromSeconds(1));

		Assert.False(await request);
		Assert.Equal((ChatEventKinds.PermissionResolved, ChatDecisions.Deny), (Published[^1].Kind, Published[^1].Decision));
	}

	[Fact]
	public async Task CancelRun_PendingDenied_TokenForgotten_LaterRequestsDenied()
	{
		var request = _broker.RequestAsync(RunId, "Bash", Ls, TestContext.Current.CancellationToken);
		var permission = await PermissionAsync();

		_broker.CancelRun(RunId);

		Assert.False(await request);
		Assert.Equal((permission.RequestId, ChatDecisions.Deny), (Published[^1].RequestId, Published[^1].Decision));
		Assert.Null(_broker.FindRun(_token));
		Assert.False(await _broker.RequestAsync(RunId, "Bash", Ls, TestContext.Current.CancellationToken));
		_broker.CancelRun(RunId);
	}

	[Fact]
	public async Task RequestCancelled_Deny()
	{
		using (var cts = new CancellationTokenSource())
		{
			var request = _broker.RequestAsync(RunId, "Bash", Ls, cts.Token);
			await PermissionAsync();

			await cts.CancelAsync();

			Assert.False(await request);
			Assert.Equal(ChatDecisions.Deny, Published[^1].Decision);
		}
	}

	[Fact]
	public async Task Answer_UnknownRunOrRequest_NotFound_InvalidDecision_BadRequest_AnsweredTwice_NotFound()
	{
		var request = _broker.RequestAsync(RunId, "Bash", Ls, TestContext.Current.CancellationToken);
		var id = (await PermissionAsync()).RequestId;

		Assert.Equal(ErrorCodes.NotFound, Assert.Throws<EnvelopeException>(() => _broker.Answer("other", id, ChatDecisions.Allow)).Code);
		Assert.Equal(ErrorCodes.NotFound, Assert.Throws<EnvelopeException>(() => _broker.Answer(RunId, "nope", ChatDecisions.Allow)).Code);
		Assert.Equal(ErrorCodes.NotFound, Assert.Throws<EnvelopeException>(() => _broker.Answer(null, null, ChatDecisions.Allow)).Code);
		Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<EnvelopeException>(() => _broker.Answer(RunId, id, "maybe")).Code);
		Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<EnvelopeException>(() => _broker.Answer(RunId, id, null)).Code);
		Assert.False(request.IsCompleted);

		_broker.Answer(RunId, id, ChatDecisions.Allow);
		Assert.Equal(ErrorCodes.NotFound, Assert.Throws<EnvelopeException>(() => _broker.Answer(RunId, id, ChatDecisions.Deny)).Code);
		Assert.True(await request);
	}

	[Fact]
	public void FindRun_OnlyTheRunsOwnToken()
	{
		var other = _broker.StartRun("run2", "other", null, _ => { });

		Assert.Equal(RunId, _broker.FindRun(_token));
		Assert.Equal("run2", _broker.FindRun(other));
		Assert.NotEqual(_token, other);
		Assert.Null(_broker.FindRun(null));
		Assert.Null(_broker.FindRun(string.Empty));
		Assert.Null(_broker.FindRun(_token[..^1]));
		Assert.Null(_broker.FindRun(_token + "x"));
	}

	[Fact]
	public async Task UnknownRun_DeniedWithoutAPrompt()
	{
		Assert.False(await _broker.RequestAsync("nope", "Bash", Ls, TestContext.Current.CancellationToken));
		Assert.Empty(Published);
	}

	[Fact]
	public async Task PublishFails_DeniedAtOnce_Logged()
	{
		_broker.StartRun("broken", Repo, null, _ => throw new IOException("disk full"));

		Assert.False(await _broker.RequestAsync("broken", "Bash", Ls, TestContext.Current.CancellationToken));
		Assert.Contains(_logger.Messages, m => m.Contains("broken", StringComparison.Ordinal));
	}

	[Fact]
	public void Rule_BashCommand_OtherwiseToolName()
	{
		Assert.Equal("Bash(git status)", PermissionBroker.Rule("Bash", new JsonObject { ["command"] = "git status" }));
		Assert.Equal("WebFetch", PermissionBroker.Rule("WebFetch", new JsonObject { ["url"] = "https://example.com" }));
		Assert.Equal("mcp__server__tool_2", PermissionBroker.Rule("mcp__server__tool_2", null));
		Assert.Null(PermissionBroker.Rule("Bash", null));
	}

	[Fact]
	public void Rule_OtherShellTools_ScopedToTheirCommand_NeverTheBareName()
	{
		Assert.Equal("PowerShell(Get-ChildItem)", PermissionBroker.Rule("PowerShell", new JsonObject { ["command"] = "Get-ChildItem" }));
		Assert.Null(PermissionBroker.Rule("PowerShell", null));
		Assert.Null(PermissionBroker.Rule("PowerShell", new JsonObject { ["command"] = "Remove-Item *.log" }));
	}

	[Fact]
	public void Rule_NonShellToolWithACommand_None()
	{
		// Only Bash and PowerShell rules are known to be scoped by their command; any other tool would get its bare name.
		Assert.Null(PermissionBroker.Rule("NewShell", new JsonObject { ["command"] = "make test" }));
		Assert.Null(PermissionBroker.Rule("NewShell", new JsonObject { ["command"] = 3 }));
	}

	[Theory]
	[InlineData("a),Bash,(b")]
	[InlineData("a) Bash (b")]
	[InlineData("x)")]
	[InlineData("(")]
	[InlineData("echo (a")]
	[InlineData("a)(b")]
	public void Rule_CommandThatClosesTheRuleEarlyOrLeavesItOpen_None(string command)
	{
		// The CLI splits --allowedTools on commas and spaces outside parentheses: "Bash(a),Bash,(b)" would allow every command.
		Assert.Null(PermissionBroker.Rule("Bash", new JsonObject { ["command"] = command }));
		Assert.Null(PermissionBroker.Rule("PowerShell", new JsonObject { ["command"] = command }));
	}

	[Theory]
	[InlineData("echo f(x)")]
	[InlineData("echo $(date) (a (b))")]
	[InlineData("git log --format=%h,%s")]
	public void Rule_BalancedParentheses_Allowed(string command)
	{
		Assert.Equal($"Bash({command})", PermissionBroker.Rule("Bash", new JsonObject { ["command"] = command }));
	}

	[Theory]
	[InlineData("rm -f build/*.o")]
	[InlineData("git:*")]
	[InlineData("npm run test:*")]
	[InlineData("echo a\nrm -rf /")]
	[InlineData("echo a\rrm -rf /")]
	[InlineData("")]
	public void Rule_BashCommandThatCouldMatchMore_None(string command)
	{
		Assert.Null(PermissionBroker.Rule("Bash", new JsonObject { ["command"] = command }));
	}

	[Theory]
	[InlineData("Web Fetch")]
	[InlineData("Bash(ls)")]
	[InlineData("Read*")]
	[InlineData("")]
	[InlineData("mcp__x__y(z)")]
	public void Rule_ToolNameOutsideLettersDigitsUnderscores_None(string tool)
	{
		Assert.Null(PermissionBroker.Rule(tool, new JsonObject()));
	}

	[Theory]
	[InlineData("Bash", "rm -f build/*.o")]
	[InlineData("Bash", "git:*")]
	[InlineData("Bash", "ls\nrm x")]
	[InlineData("Bash", "echo ok),Bash,(x")]
	[InlineData("NewShell", "make test")]
	[InlineData("Web Fetch", "x")]
	public async Task AllowAlways_WithoutASafeRule_AllowedOnce_ReportedAllow_NothingSaved(string tool, string command)
	{
		var input = new JsonObject { ["command"] = command };
		var request = _broker.RequestAsync(RunId, tool, input, TestContext.Current.CancellationToken);

		_broker.Answer(RunId, (await PermissionAsync()).RequestId, ChatDecisions.AllowAlways);

		Assert.True(await request);
		Assert.Equal(ChatDecisions.Allow, Published[^1].Decision);
		Assert.Null(_projects.GetSettings(Repo).AgentAllowedTools);
		Assert.False(_broker.RequestAsync(RunId, tool, input.DeepClone(), TestContext.Current.CancellationToken).IsCompleted);
	}

	[Fact]
	public async Task SettingsUnreadable_DeniedWithoutAPrompt_Logged()
	{
		var broker = new PermissionBroker(new BrokenProjectStore(), _time, _logger);
		var events = new List<ChatEvent>();
		broker.StartRun("r", Repo, null, events.Add);

		Assert.False(await broker.RequestAsync("r", "Bash", Ls, TestContext.Current.CancellationToken));
		Assert.Empty(events);
		Assert.Contains(_logger.Messages, m => m.Contains("denied", StringComparison.Ordinal));
	}

	[Fact]
	public async Task RunEndsWhilePublishingTheCard_DenyFollowsTheCard()
	{
		var events = new List<ChatEvent>();
		Task? cancel = null;
		_broker.StartRun("r", Repo, null, e =>
		{
			lock (events)
			{
				events.Add(e);
			}

			if (e.Kind == ChatEventKinds.Permission)
			{
				// The run ends on another thread while the card is being published.
				cancel = Task.Run(() => _broker.CancelRun("r"), TestContext.Current.CancellationToken);
				Thread.Sleep(100);
			}
		});

		Assert.False(await _broker.RequestAsync("r", "Bash", Ls, TestContext.Current.CancellationToken));
		await cancel!;
		lock (events)
		{
			Assert.Equal([ChatEventKinds.Permission, ChatEventKinds.PermissionResolved], events.Select(e => e.Kind));
			Assert.Equal(ChatDecisions.Deny, events[1].Decision);
		}
	}

	private void Publish(ChatEvent e)
	{
		lock (_published)
		{
			_published.Add(e);
		}
	}

	private async Task<ChatEvent> PermissionAsync()
	{
		for (var i = 0; i < 500 && !Published.Any(e => e.Kind == ChatEventKinds.Permission); i++)
		{
			await Task.Delay(10, TestContext.Current.CancellationToken);
		}

		return Published.Last(e => e.Kind == ChatEventKinds.Permission);
	}

	/// <summary>A project store whose reads fail (e.g. the database is locked).</summary>
	private sealed class BrokenProjectStore : IProjectStore
	{
		public bool IsBaselined(string repo) => throw new IOException("locked");

		public void SetBaselined(string repo) => throw new IOException("locked");

		public IReadOnlyDictionary<string, string> GetBases(string repo) => throw new IOException("locked");

		public void SetBases(string repo, IReadOnlyCollection<KeyValuePair<string, string?>> changes) => throw new IOException("locked");

		public void ForgetBases(string repo) => throw new IOException("locked");

		public ProjectSettings GetSettings(string repo) => throw new IOException("locked");

		public ProjectSettings SaveSettings(string repo, ProjectSettings settings) => throw new IOException("locked");
	}
}
