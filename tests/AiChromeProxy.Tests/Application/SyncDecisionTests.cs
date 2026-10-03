namespace AiChromeProxy.Tests.Application;

using AiChromeProxy.Application.Sync;
using Xunit;

public sealed class SyncDecisionTests
{
	[Theory]
	// both equal
	[InlineData("a", "a", null, true, SyncAction.InSync)]
	[InlineData("a", "a", "x", true, SyncAction.InSync)]
	[InlineData(null, null, "x", true, SyncAction.InSync)]
	// only the client changed
	[InlineData("b", "a", "a", true, SyncAction.Upload)]
	[InlineData("b", null, null, true, SyncAction.Upload)] // new on the client (or Keep mine after a server delete)
	[InlineData(null, "a", "a", true, SyncAction.DeleteMirror)]
	// only the mirror changed
	[InlineData("a", "b", "a", true, SyncAction.Push)]
	[InlineData("a", null, "a", true, SyncAction.Push)] // deleted on the server
	[InlineData(null, "a", null, true, SyncAction.Push)] // created on the server
	// both changed: conflict candidates, pushed
	[InlineData("b", "c", "a", true, SyncAction.Push)]
	[InlineData("b", "c", null, true, SyncAction.Push)] // created on both sides
	[InlineData(null, "c", "a", true, SyncAction.Push)] // deleted here, modified there
	[InlineData("b", null, "a", true, SyncAction.Push)] // modified here, deleted there
	// before the baseline the client wins (3a)
	[InlineData("b", "c", "a", false, SyncAction.Upload)]
	[InlineData("b", "c", null, false, SyncAction.Upload)]
	[InlineData(null, "c", null, false, SyncAction.DeleteMirror)]
	[InlineData("a", "a", null, false, SyncAction.InSync)]
	public void Decide(string? client, string? mirror, string? baseHash, bool baselined, SyncAction expected)
	{
		Assert.Equal(expected, SyncDecision.Decide(client, mirror, baseHash, baselined));
	}
}
