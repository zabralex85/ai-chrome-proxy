Feature: Connection
	The status page connects to the server hub over SignalR and round-trips a ping.

Scenario: Status page connects
	Given the server is running
	When I open the app
	Then the connection state is "Connected"

Scenario: Ping shows a round trip
	Given the app is connected
	When I click "Ping"
	Then I see "Pong in <n> ms" with a server time
