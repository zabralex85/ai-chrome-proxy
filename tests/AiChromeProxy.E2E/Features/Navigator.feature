Feature: Navigator
	Files open in the read-only code viewer: from the tree, from path:line and path#Symbol links in the chat, and the viewer follows changes of the file.
	The folder picker cannot be automated: the page's own file system stands in for the folder (its files are written by the steps, also later on).

Background:
	Given the app is connected with a folder holding the sample files

Scenario: A file in the tree opens as highlighted code with line numbers
	When I click "src" and then "A.cs" in the file tree
	Then a file tab "A.cs" is active
	And the viewer shows the code of "A.cs" with line numbers and highlighting
	And I save navigator screenshots named "tree"

Scenario: A path:line link in the answer reveals that line
	When I type "navigate please" and press Enter
	And I click the code link "src/A.cs:3" in the answer
	Then a file tab "A.cs" is active
	And the viewer shows the code of "A.cs" with line numbers and highlighting
	And line 3 of the viewer is highlighted

Scenario: A path#Symbol link in the answer reveals the declaration
	When I type "navigate please" and press Enter
	And I click the code link "src/A.cs#Foo" in the answer
	Then a file tab "A.cs" is active
	And line 5 of the viewer is highlighted
	And the viewer line 5 reads "public sealed class Foo"
	And I save navigator screenshots named "symbol"

Scenario: A symbol that is not in the file shows a note
	When I type "navigate please" and press Enter
	And I click the code link "src/A.cs#Missing" in the answer
	Then a file tab "A.cs" is active
	And the viewer shows the code of "A.cs" with line numbers and highlighting
	And the viewer says "Symbol not found"

Scenario: A change of the file in the folder shows up with the changed line marked
	When I click "src" and then "A.cs" in the file tree
	Then the viewer shows the code of "A.cs" with line numbers and highlighting
	When the file "src/A.cs" changes in the folder
	Then the viewer marks line 3 as changed and shows the new text

Scenario: A binary file is not shown
	When I click "notes.bin" in the file tree
	Then the file note says "Binary file — not shown"
	And the viewer has no editor

Scenario: A file deleted from the folder says so
	When I click "README.md" in the file tree
	Then the viewer shows "# e2e repo"
	When the file "README.md" is deleted from the folder
	Then the file note says "This file is no longer in the folder"
	And the viewer has no editor
	And Monaco holds 0 editors and 0 text models

Scenario: Closing a file tab disposes its editor and model
	When I click "src" and then "A.cs" in the file tree
	Then Monaco holds 1 editors and 1 text models
	When I close the tab of "src/A.cs"
	Then Monaco holds 0 editors and 0 text models

Scenario: Changing the folder disposes the editors and the open file comes back with one
	When I click "src" and then "A.cs" in the file tree
	Then Monaco holds 1 editors and 1 text models
	When I change the folder
	Then the viewer shows the code of "A.cs" with line numbers and highlighting
	And Monaco holds 1 editors and 1 text models

Scenario: At most ten editors stay alive: the least recently shown is disposed
	When I open the text files f01 to f11 from the file tree
	Then Monaco holds 10 editors and 10 text models
	When I close the tab of "f11.txt"
	Then Monaco holds 9 editors and 9 text models
