using AiChromeProxy.Infrastructure.Hosted;

namespace AiChromeProxy.Tests.Infrastructure;

public sealed class InviteLinkTests
{
	private const string Code = "abcdefghij_KLMN-0123";

	[Theory]
	[InlineData($"https://example.com/invite/{Code}", "https://example.com/")]
	[InlineData($"  https://example.com/invite/{Code}/  ", "https://example.com/")]
	[InlineData($"https://example.com:8443/invite/{Code}", "https://example.com:8443/")]
	[InlineData($"http://localhost:5000/invite/{Code}", "http://localhost:5000/")]
	[InlineData($"http://127.0.0.1/invite/{Code}", "http://127.0.0.1/")]
	public void TryParse_Valid(string text, string origin)
	{
		Assert.True(InviteLink.TryParse(text, out var link));
		Assert.Equal(new Uri(origin), link!.Origin);
		Assert.Equal(Code, link.Code);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("not a url")]
	[InlineData($"http://example.com/invite/{Code}")]
	[InlineData($"ftp://example.com/invite/{Code}")]
	[InlineData("https://example.com/invite/short")]
	[InlineData("https://example.com/invite/abcdefghij_KLMN-0123!")]
	[InlineData("https://example.com/invite/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
	[InlineData($"https://example.com/other/{Code}")]
	[InlineData($"https://example.com/invite/{Code}/extra")]
	[InlineData($"https://example.com/invite/{Code}//")]
	[InlineData("https://example.com/invite/")]
	[InlineData($"https://example.com/invite/{Code}?x=1")]
	[InlineData($"https://example.com/invite/{Code}#frag")]
	[InlineData($"https://user@example.com/invite/{Code}")]
	public void TryParse_Invalid(string? text)
	{
		Assert.False(InviteLink.TryParse(text, out var link));
		Assert.Null(link);
	}
}
