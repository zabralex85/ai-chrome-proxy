using AiChromeProxy.Infrastructure.Cloudflare;

namespace AiChromeProxy.Tests.Infrastructure;

public sealed class RemoteAccessInputTests
{
	[Theory]
	[InlineData("code")]
	[InlineData("a")]
	[InlineData("my-code-2")]
	[InlineData("0")]
	public void Subdomain_OneLabel_Valid(string value)
	{
		Assert.True(RemoteAccessInput.IsValidSubdomain(value));
	}

	[Fact]
	public void Subdomain_63Characters_Valid_64_Invalid()
	{
		Assert.True(RemoteAccessInput.IsValidSubdomain(new string('a', 63)));
		Assert.False(RemoteAccessInput.IsValidSubdomain(new string('a', 64)));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("-code")]
	[InlineData("code-")]
	[InlineData("Code")]
	[InlineData("a.b")]
	[InlineData("co de")]
	[InlineData("code_1")]
	[InlineData("kód")]
	public void Subdomain_NotOneLowerCaseLabel_Invalid(string? value)
	{
		Assert.False(RemoteAccessInput.IsValidSubdomain(value));
	}

	[Theory]
	[InlineData("jane@example.com")]
	[InlineData("jane.doe+code@mail.example.org")]
	public void Email_Valid(string value)
	{
		Assert.True(RemoteAccessInput.IsValidEmail(value));
	}

	[Theory]
	[InlineData("jane")]
	[InlineData("@example.com")]
	[InlineData("jane@example")]
	[InlineData("jane@.com")]
	[InlineData("jane@example.")]
	[InlineData("jane@@example.com")]
	[InlineData("ja ne@example.com")]
	[InlineData("jane@exa@mple.com")]
	public void Email_Invalid(string value)
	{
		Assert.False(RemoteAccessInput.IsValidEmail(value));
	}

	[Fact]
	public void ParseEmails_CommaOrNewline_Trimmed_EmptyAndDuplicatesDropped()
	{
		Assert.Equal(
			["jane@example.com", "joe@example.com", "ann@example.org"],
			RemoteAccessInput.ParseEmails(" jane@example.com, joe@example.com\r\n\nann@example.org ,JANE@example.com,"));
		Assert.Empty(RemoteAccessInput.ParseEmails(null));
		Assert.Empty(RemoteAccessInput.ParseEmails(" , \n"));
	}

	[Fact]
	public void Validate_Valid_NoErrors()
	{
		Assert.Empty(RemoteAccessInput.Validate("code", "example.com", ["jane@example.com"]));
	}

	[Fact]
	public void Validate_Everything_Wrong_OneMessageEach()
	{
		Assert.Equal(
			[
				"Choose a zone.",
				"The subdomain must be one label of a-z, 0-9 and '-', 1 to 63 characters, not starting or ending with '-'.",
				"Enter at least one email address.",
			],
			RemoteAccessInput.Validate("-x", null, []));
	}

	[Fact]
	public void Validate_BadEmail_Named()
	{
		Assert.Equal(["'jane' is not an email address."], RemoteAccessInput.Validate("code", "example.com", ["joe@example.com", "jane"]));
	}

	[Fact]
	public void Validate_ResultingHostNameNotBare_Rejected()
	{
		Assert.Equal(["code.example.com. is not a valid host name."], RemoteAccessInput.Validate("code", "example.com.", ["jane@example.com"]));
	}
}
