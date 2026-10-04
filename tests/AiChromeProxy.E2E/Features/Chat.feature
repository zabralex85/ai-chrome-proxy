Feature: Chat
	A message goes to Claude on the home server (here a fake agent that prints a recorded stream) and the answer streams back with its
	diagram, tool calls and permission requests. The folder picker cannot be automated: the page's own file system stands in for the folder.

Scenario: An answer with a diagram, a tool call and a permission request
	Given the app is connected with a synced folder open
	Then the chat input is enabled
	When I type "tour please" and press Enter
	Then the Chat tab is active
	And my message "tour please" is shown
	And Claude's answer says "Looking at the build setup."
	And the answer contains a drawn diagram
	And the answer lists "first point"
	And the chat shows a tool row for "Bash"
	And the permission card asks about "dotnet build"
	And the agent status says "Claude working"
	And I save screenshots named "approval"
	When I click "Allow" on the permission card
	Then the permission card is gone
	And Claude's answer says "approve: allow"
	And the agent status says "Claude idle"
	And the last run cost is "$0.12"
	And I save screenshots named "done"

Scenario: Text after a tool call streams in too
	Given the app is connected with a synced folder open
	When I type "twotext please" and press Enter
	Then Claude's answer says "First answer."
	And the chat shows a tool row for "Bash"
	And Claude's streaming answer says "second3"
	And Claude's answer says "Second answer done."
	And the agent status says "Claude idle"

Scenario: A path:line in the answer opens the file's tab
	Given the app is connected with a synced folder open
	When I type "tour please" and press Enter
	And I click the code link "README.md:1" in the answer
	Then a file tab "README.md" is active

Scenario: Stop ends a run in progress
	Given the app is connected with a synced folder open
	When I type "slow please" and press Enter
	Then the send button has become Stop
	And Claude's answer says "word0"
	When I click Stop
	Then the send button is Send again
	And the agent status says "Claude idle"

Scenario: Shift+Enter adds a line instead of sending
	Given the app is connected with a synced folder open
	When I type "first" and press Shift+Enter and type "second"
	Then the chat input holds two lines
	And no message is shown

Scenario: The sessions menu starts a new chat and lists the earlier ones
	Given the app is connected with a synced folder open
	When I type "slow please" and press Enter
	And I click Stop
	And I open the chats menu
	Then the menu lists a chat titled "slow please"
	When I choose New chat from the menu
	Then the New chat tab is active
	And the chat is empty

Scenario: The project settings choose how Claude may act and the model
	Given the app is connected with a synced folder open
	When I click the project settings button
	Then the settings tab offers the three permission options with "Ask before commands (edits are applied)" chosen
	And the model field is empty and says "Claude Code's default"
	When I choose "Allow everything" and type the model "opus" and save
	Then the settings are saved
	And I save screenshots named "settings"
