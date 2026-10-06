Feature: Claude tools
	The Project settings tab lists the MCP servers and plugins of the home computer's Claude Code with their status and an On in this project
	switch. The fake agent answers Check now from Fixtures/mcp-list.txt and plugin-list.json, and a "tools" message's init line lists them too.

Scenario: Check now lists the servers and plugins with their statuses, problems first
	Given the app is connected with a synced folder open
	When I click the project settings button
	Then the Claude tools section says "No data yet — send a message or press Check now."
	When I click Check now
	Then the Claude tools section says "(by Check now)"
	And the MCP servers are "blender: Failed, linear: Needs sign-in, team-db: Waiting for approval, github: Connected, figma: Connected"
	And the plugins are "design, notes"
	And the hint under "blender" says "Check the server on the home computer: claude mcp get blender."
	And the failed server "blender" shows the reason "connect ECONNREFUSED 127.0.0.1:9876"
	And the hint under "linear" says "Sign in on the home computer: run claude, then /mcp."
	And the server "plugin:design:figma" has no switch and says "plugin design"
	And I save Claude tools screenshots

Scenario: A message records the statuses
	Given the app is connected with a synced folder open
	When I type "tools please" and press Enter
	Then Claude's answer says "Tools answer."
	When I click the project settings button
	Then the Claude tools section says "(from a message)"
	And the MCP servers are "blender: Failed, linear: Needs sign-in, github: Connected, figma: Connected"

Scenario: A server and a plugin switched off are left out of the next run
	Given the app is connected with a synced folder open
	When I click the project settings button
	And I click Check now
	And I switch "github: On in this project" off
	And I switch "design: On in this project" off
	And I save the settings
	And I type "tools please" and press Enter
	Then Claude's answer says "Tools answer."
	And the run's --settings deny the MCP server "github" and turn off the plugin "design@example-market"
	When I click the project settings button
	Then the MCP server "github" says "Off in this project"
	And the switch "github: On in this project" is off

Scenario: An approved .mcp.json server runs until its entry changes
	Given the app is connected with a synced folder open
	And the mirror's .mcp.json runs "npx -y db-mcp" as "team-db"
	When I click the project settings button
	And I click Check now
	Then the server "team-db" shows the command "npx -y db-mcp"
	When I switch "team-db: On in this project" on
	And I save the settings
	And I type "tools please" and press Enter
	Then Claude's answer says "Tools answer."
	And the run's --settings approve the .mcp.json server "team-db"
	When the mirror's .mcp.json runs "cmd /c evil" as "team-db"
	And I forget the last run's arguments
	And I type "tools again" and press Enter
	Then the run's --settings do not approve the .mcp.json server "team-db"
	When I click the project settings button
	And I click Check now
	Then the MCP server "team-db" says "Waiting for approval"
	And the server "team-db" shows the command "cmd /c evil"
	And the hint under "team-db" says "Changed since you approved it — check .mcp.json and approve again."
