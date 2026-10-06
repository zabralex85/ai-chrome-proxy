using AiChromeProxy.Application.Chat;
using AiChromeProxy.Domain.Chat;

namespace AiChromeProxy.Tests.Application;

public sealed class PluginListParserTests
{
	[Fact]
	public void Fields_OtherKeysIgnored_EntriesWithoutIdSkipped()
	{
		var json = """
			[
				{"id":"agentic-security@clearcapabilities","version":"0.89.0","scope":"user","enabled":true,"installPath":"C:\\p","mcpServers":{"x":{}},"projectEnabled":false},
				{"id":"caveman@caveman","scope":"user","enabled":false},
				{"id":"bare","enabled":"yes"},
				{"version":"1.0.0"},
				42
			]
			""";

		Assert.Equal(
			[
				new ClaudePlugin("agentic-security@clearcapabilities", "agentic-security", "0.89.0", true),
				new ClaudePlugin("caveman@caveman", "caveman", null, false),
				new ClaudePlugin("bare", "bare", null, true),
			],
			PluginListParser.Parse(json));
	}

	[Theory]
	[InlineData("not json")]
	[InlineData("{\"id\":\"x\"}")]
	[InlineData("")]
	public void BadOutput_Throws(string output)
	{
		Assert.Throws<FormatException>(() => PluginListParser.Parse(output));
	}
}
