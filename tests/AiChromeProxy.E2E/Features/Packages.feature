Feature: Packages
	Private NuGet packages from the NuGet cache folder reach the home server; nothing in the project changes.

Scenario: A private package is uploaded and a public one is left to nuget.org
	Given the app is connected with a synced folder that restored a private and a public package
	When I open the Packages tab
	And I add the NuGet cache folder
	Then the package "corp.private" "1.2.3" says "On the server"
	And the package "humanizer.core" "2.14.1" says "Public"
	And the server holds "corp.private" "1.2.3"
	And I save package screenshots named "packages"

Scenario: A deleted NuGet cache folder is not found
	Given the app is connected with a synced folder that restored a private and a public package
	When I open the Packages tab
	And I add the NuGet cache folder
	Then the package "corp.private" "1.2.3" says "On the server"
	When the NuGet cache folder is deleted
	Then the NuGet cache source says "Not found"
