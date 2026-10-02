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
