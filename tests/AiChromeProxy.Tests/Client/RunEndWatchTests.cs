using AiChromeProxy.Client.Projects;

namespace AiChromeProxy.Tests.Client;

public sealed class RunEndWatchTests
{
	[Fact]
	public void Ended_FiresOnceWhenTheViewedRunEnds()
	{
		var watch = new RunEndWatch();
		Assert.False(watch.Ended("Other", "Other", true));
		Assert.True(watch.Ended("Other", "Other", false));

		// The refresh it starts raises Changed again at once: no second refresh.
		Assert.False(watch.Ended("Other", "Other", false));
	}

	[Fact]
	public void Ended_IgnoresTheLocalRunWhileTheViewedChatOpens()
	{
		var watch = new RunEndWatch();

		// Viewing starts while the folder's own run is on; the chat still shows the folder's repo.
		Assert.False(watch.Ended("Other", "Local", true));
		Assert.False(watch.Ended("Other", "other", false));
	}

	[Fact]
	public void Ended_StartsOverWhenTheViewChanges()
	{
		var watch = new RunEndWatch();
		Assert.False(watch.Ended("A", "A", true));
		Assert.False(watch.Ended("B", "A", true));
		Assert.False(watch.Ended("B", "B", false));
		Assert.False(watch.Ended("B", "B", true));
		Assert.False(watch.Ended(null, "Local", false));
		Assert.False(watch.Ended("B", "B", false));
	}
}
