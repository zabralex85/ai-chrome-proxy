Feature: Shell
	The app looks like a small VS Code: top bar, file tree, tabs with the main area, chat input, actions history and two status boxes.
	The folder picker cannot be automated: sync is covered by the hub tests and the manual checklist (docs/sync.md).

Scenario: The shell renders with no folder open
	Given the app is connected
	Then I see the top bar, the file tree, the tabs, the main area and the chat input
	And I see the actions history, the sync status and the connection status
	And the user is "local"
	And the file tree offers "Open folder"
	And the Welcome tab is active and shows the welcome page
	And the chat input is disabled
	And the sync status says "No folder open"

Scenario: The theme toggle cycles system, light and dark and is remembered
	Given the app is connected
	Then the theme is "system"
	When I click the theme toggle
	Then the theme is "light"
	When I click the theme toggle
	Then the theme is "dark"
	When I reload the app
	Then the theme is "dark"
	When I click the theme toggle
	Then the theme is "system"

Scenario: The left panel collapses and opens again
	Given the app is connected
	When I click the file tree toggle
	Then the file tree is hidden
	When I click the file tree toggle
	Then the file tree is visible
	And the file tree offers "Open folder"

Scenario: The error list opens from the sync status
	Given the app is connected
	When I click the error count in the sync status
	Then the error list says "No errors."

Scenario: The project settings gear needs an open folder
	Given the app is connected
	Then the project settings button is disabled

Scenario: Project settings tab opens from the gear and shows its fields
	Given the app is connected with a folder open
	When I click the project settings button
	Then the Project settings tab is active
	And the settings tab shows the excludes, the automatic apply option and a disabled Save button
