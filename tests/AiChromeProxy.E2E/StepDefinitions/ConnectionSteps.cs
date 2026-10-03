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

	[Then("the connection pill's tooltip shows the ping in milliseconds")]
	public async Task ThenThePillShowsThePingAsync()
	{
		await Expect(page.GetByTestId("connection-state")).ToHaveAttributeAsync("title", PingTitle());
	}

	[Then("the connection status box shows the ping in milliseconds")]
	public async Task ThenTheStatusBoxShowsThePingAsync()
	{
		await Expect(page.GetByTestId("connection-status")).ToContainTextAsync(PingText());
	}

	/// <summary>TopBar.razor: "Ping {ms} ms" once a ping was answered (the first one is sent when the connection comes up).</summary>
	[GeneratedRegex(@"^Ping \d+ ms$")]
	private static partial Regex PingTitle();

	/// <summary>ActionsHistory.razor: "Connected" and "{ms} ms".</summary>
	[GeneratedRegex(@"Connected\s*\d+ ms")]
	private static partial Regex PingText();
}
