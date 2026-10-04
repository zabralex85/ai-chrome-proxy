Feature: Viewer
	Files are shown read-only in Monaco (vendored under /lib/monaco, Scripts/viewer.ts). Until the file tree opens files in it,
	the scenarios call the viewer module directly in the running app, under the page's Content-Security-Policy.

Scenario Outline: The viewer highlights code and reveals a line
	Given the app is connected
	When the viewer opens "<path>" with line 2 revealed
	Then the viewer shows the code with line numbers and syntax highlighting
	And the viewer's language is "<language>"
	And line 2 is highlighted
	And the editor worker runs
	And no request for Monaco's files failed

Examples:
	| path          | language   |
	| src/Sample.cs | csharp     |
	| web/sample.ts | typescript |
