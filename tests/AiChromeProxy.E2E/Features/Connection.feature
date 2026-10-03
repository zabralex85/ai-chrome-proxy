Feature: Connection
	The app shell connects to the server hub over SignalR and shows the connection in the top bar.

Scenario: App connects
	Given the server is running
	When I open the app
	Then the connection state is "Connected"
	And the page loads the fingerprinted Blazor script

Scenario: The ping latency is shown
	Given the app is connected
	Then the connection pill's tooltip shows the ping in milliseconds
	And the connection status box shows the ping in milliseconds
	And no update banner is shown
