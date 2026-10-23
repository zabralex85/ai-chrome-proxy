Feature: Server projects
	Projects synced from other computers can be looked at read-only: their files and chat come from the server.
	Another computer's project stands for itself as a folder written straight into the server's mirror.

Background:
	Given the app is connected with a folder open
	And the server holds the project "other-pc" with the file "src/A.cs" reading "class FromTheOtherComputer {}"

Scenario: Another computer's project opens read-only and the way back restores my folder
	When I open the projects menu
	Then the projects menu lists "other-pc" as "no chat yet"
	When I pick the project "other-pc"
	Then the shell shows "other-pc" read-only
	When I click "src" and then "A.cs" in the file tree
	Then a file tab "A.cs" is active
	And the viewer shows "FromTheOtherComputer"
	When I go back to my folder
	Then the shell shows my folder "e2e-repo" again
