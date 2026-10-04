Feature: Tree actions
	A context menu on the file tree creates, renames and deletes files and folders in the picked folder; the sync follows and open tabs follow renames.
	The folder picker cannot be automated: the page's own file system stands in for the folder. File renames run with the browser's native move, with the copy fallback, and with a move that refuses (the copy then takes over). Folder renames need FileSystemDirectoryHandle.move, which Chromium lacks in the origin-private file system (the unit tests cover the tab mapping of folders).

Scenario: New File from the menu appears in the tree and opens in a tab
	Given the app is connected with a folder for tree actions
	When I right-click "src" in the file tree and choose "New File"
	And I type the name "New.cs" and press Enter
	Then the file tree shows "src/New.cs"
	And the file tab "New.cs" is active with the viewer open
	And the mirror gets the file "src/New.cs" after the sync

Scenario: New Folder from the empty part of the tree shows an empty folder
	Given the app is connected with a folder for tree actions
	When I right-click the empty part of the tree and choose "New Folder"
	And I type the name "empty-dir" and press Enter
	Then the file tree shows "empty-dir"
	When I click "empty-dir" in the file tree
	Then "empty-dir" has no children in the tree

Scenario: A name that is not allowed shows its reason and creates nothing
	Given the app is connected with a folder for tree actions
	When I right-click the empty part of the tree and choose "New File"
	And I type the name "a/b" and press Enter
	Then the name input says "A name cannot contain / or \."
	When I type the name "A.TXT" and press Enter
	Then the name input says "'A.TXT' already exists here."
	When I press Escape
	Then the file tree does not show "a"
	And the file tree does not show "A.TXT"

Scenario: Leaving the name input cancels: nothing is created
	Given the app is connected with a folder for tree actions
	When I right-click the empty part of the tree and choose "New File"
	And I type the name "ghost.txt" without pressing Enter
	And I click elsewhere
	Then no name input is open
	And the file tree does not show "ghost.txt"
	And the file "ghost.txt" is not in the folder
	When I right-click the empty part of the tree and choose "New File"
	And I type the name "a/b" and press Enter
	Then the name input says "A name cannot contain / or \."
	When I click elsewhere
	Then no name input is open
	And no name error is shown

Scenario: A name sync leaves out is created with a note
	Given the app is connected with a folder for tree actions
	When I right-click the empty part of the tree and choose "New File"
	And I type the name ".env" and press Enter
	Then the tree note says "Created; excluded from sync"
	And the file tab ".env" is active with the viewer open
	And the file tree does not show ".env"

Scenario Outline: F2 renames a file and its open tab follows (<mode>)
	Given the app is connected with a folder for tree actions and a browser that renames by "<mode>"
	When I click "a.txt" in the file tree
	Then the file tab "a.txt" is active with the viewer showing "alpha"
	When I press F2 on "a.txt" and rename it to "b.txt"
	Then the file tree shows "b.txt"
	And the file tree does not show "a.txt"
	And the file tab "b.txt" is active with the viewer showing "alpha"
	And Monaco holds 1 editors and 1 text models
	And the mirror gets the file "b.txt" after the sync
	And the mirror no longer has the file "a.txt"

	Examples:
		| mode    |
		| native  |
		| copy    |
		| refuses |

Scenario Outline: A rename that only changes the case keeps the file and its tab (<mode>)
	Given the app is connected with a folder for tree actions and a browser that renames by "<mode>"
	When I click "a.txt" in the file tree
	Then the file tab "a.txt" is active with the viewer showing "alpha"
	When I press F2 on "a.txt" and rename it to "A.txt"
	Then the file tree shows "A.txt"
	And the file tree does not show "a.txt"
	And the file tab "A.txt" is active with the viewer showing "alpha"
	And Monaco holds 1 editors and 1 text models

	Examples:
		| mode   |
		| native |
		| copy   |

Scenario: Renaming onto a name sync hides is refused and changes nothing
	Given the app is connected with a folder for tree actions that also holds ".env"
	Then the file tree does not show ".env"
	When I press F2 on "a.txt" and rename it to ".env"
	Then the name input says "'.env' already exists here."
	When I press Escape
	Then the file tree shows "a.txt"
	And the file "a.txt" in the folder holds "alpha"
	And the file ".env" in the folder holds "secret"

Scenario: Renaming a folder is disabled with its reason when the browser cannot move folders
	Given the app is connected with a folder for tree actions
	When I right-click "docs" in the file tree
	Then the menu item "Rename" is disabled with the tooltip "Your browser cannot rename folders"

Scenario: The copy keeps the content of a renamed file
	Given the app is connected with a folder for tree actions and a browser that renames by "copy"
	When I press F2 on "keep.txt" and rename it to "kept.txt"
	Then the file tree shows "kept.txt"
	And the file "kept.txt" in the folder holds "original"
	And the file "keep.txt" is not in the folder

Scenario: A failed write in the copy fallback leaves the original untouched
	Given the app is connected with a folder for tree actions and a browser that renames by "copy" and cannot write "blocked.txt"
	When I press F2 on "keep.txt" and rename it to "blocked.txt"
	Then the name input says a failure
	When I press Escape
	Then the file tree shows "keep.txt"
	And the file "keep.txt" in the folder holds "original"
	And the file "blocked.txt" is not in the folder

Scenario: Delete a file asks first and removes it from the tree and the mirror
	Given the app is connected with a folder for tree actions
	When I click "a.txt" in the file tree
	Then the file tab "a.txt" is active with the viewer showing "alpha"
	When I right-click "a.txt" in the file tree and choose "Delete"
	Then the dialog asks "Delete a.txt?"
	And the dialog says "This cannot be undone. The server's copy is deleted at the next sync."
	And the dialog has the focus on "Cancel"
	When I press the "Delete" button of the dialog
	Then the file tree does not show "a.txt"
	And no file tab "a.txt" is open
	And the mirror no longer has the file "a.txt"

Scenario: Delete a folder shows how many files go with it and Escape cancels
	Given the app is connected with a folder for tree actions
	When I right-click "docs" in the file tree and choose "Delete"
	Then the dialog asks "Delete the folder docs and its 2 files?"
	When I press Escape
	Then no dialog is open
	And the file tree shows "docs"
	When I right-click "docs" in the file tree and choose "Delete"
	And I press the "Delete" button of the dialog
	Then the file tree does not show "docs"
	And the mirror no longer has the file "docs/guide.md"

Scenario: The delete dialog is modal: clicking its text and Escape cancel, Tab stays inside
	Given the app is connected with a folder for tree actions
	When I right-click "docs" in the file tree and choose "Delete"
	And I click the text of the dialog
	Then the dialog asks "Delete the folder docs and its 2 files?"
	When I press Tab 5 times, the focus never reaches the page behind the dialog
	When I press Escape
	Then no dialog is open
	And the file tree shows "docs"
	And the file "docs/guide.md" is in the mirror folder

Scenario: Clicking the dimmed part of the dialog cancels
	Given the app is connected with a folder for tree actions
	When I right-click "docs" in the file tree and choose "Delete"
	And I click the dimmed part of the dialog
	Then no dialog is open
	And the file tree shows "docs"

Scenario: Keyboard only: Shift+F10, arrows, Enter, F2, Delete and Escape
	Given the app is connected with a folder for tree actions
	When I focus the tree row "keep.txt"
	And I press Shift+F10
	Then the menu is open with "New File" focused
	When I press ArrowDown
	And I press Enter
	And I type the name "kb-dir" and press Enter
	Then the file tree shows "kb-dir"
	And the tree row "kb-dir" has the focus
	When I press Shift+F10
	And I press Escape
	Then the menu is closed
	And the tree row "kb-dir" has the focus
	When I press Delete
	Then the dialog asks "Delete the folder kb-dir and its 0 files?"
	When I press Escape
	Then no dialog is open
	And the tree row "kb-dir" has the focus
	When I press Delete
	And I press Tab
	And I press Enter
	Then the file tree does not show "kb-dir"

Scenario: The menu and the dialog look right in both themes
	Given the app is connected with a folder for tree actions
	Then I save tree screenshots named "menu"
	And I save tree screenshots named "dialog"
