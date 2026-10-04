Feature: Diagram tools
	Every drawn diagram in the chat has a toolbar: Open shows it in a full-window viewer with zoom and pan, Save… writes it into the
	project, Download… saves an SVG or a PNG through the browser (Scripts/diagrams.ts). The fake agent answers "classes" with a class diagram.

Scenario: A diagram opens full window, zooms, fits and closes back to its toolbar
	Given the app is connected with a synced folder open
	When I type "classes please" and press Enter
	Then the answer contains a drawn diagram
	And the diagram toolbar offers Open, Save… and Download…
	And the Save… menu lists "Source (.mmd), SVG, PNG" and closes with Escape
	And I save screenshots of the diagram tools
	When I open the diagram
	Then the diagram viewer shows the fitted zoom level
	When I press "+" in the diagram viewer
	Then the zoom level has grown
	When I click "Fit" in the diagram viewer
	Then the zoom level is the fitted one again
	When I press "Escape" in the diagram viewer
	Then the diagram viewer is closed and Open has the focus

Scenario: A diagram downloads as SVG and as PNG
	Given the app is connected with a synced folder open
	When I type "classes please" and press Enter
	Then the answer contains a drawn diagram
	When I download the diagram as "SVG"
	Then the download is named "class-diagram-*.svg" and starts with the text "<?xml"
	When I download the diagram as "PNG"
	Then the download is named "class-diagram-*.png" and starts with the PNG signature
	And the diagram drawn again in the other theme still has HTML labels

Scenario: A diagram's source is saved into the project, never over an existing file, and opens from the note
	Given the app is connected with a synced folder open
	When I type "classes please" and press Enter
	Then the answer contains a drawn diagram
	And I save screenshots of the save dialog
	When I choose "Source (.mmd)" from the diagram's Save… menu
	Then the save dialog "Save diagram source" offers "docs/diagrams/class-diagram-<yyyyMMdd-HHmm>.mmd" with the name selected
	When I click Save in the save dialog
	Then no save dialog is open
	And the chat says the diagram was saved with an Open link
	And the saved file in the folder holds the diagram source
	And the mirror gets the saved file after the sync
	And I save screenshots of the saved note
	When I choose "Source (.mmd)" from the diagram's Save… menu
	And I click Save in the save dialog
	Then the save dialog says the saved file already exists here
	And the saved file in the folder holds the diagram source
	When I press Escape
	Then no save dialog is open
	When I click Open in the saved note
	Then the saved file's tab is active with the viewer showing "classDiagram"

Scenario: A diagram is saved as PNG, and as SVG under a typed name with another extension
	Given the app is connected with a synced folder open
	When I type "classes please" and press Enter
	Then the answer contains a drawn diagram
	When I choose "PNG" from the diagram's Save… menu
	Then the save dialog "Save diagram as PNG" offers "docs/diagrams/class-diagram-<yyyyMMdd-HHmm>.png" with the name selected
	When I click Save in the save dialog
	Then the chat says the diagram was saved without an Open link
	And the saved file in the folder starts with the PNG signature
	When I choose "SVG" from the diagram's Save… menu
	And I type the path "docs/diagrams/mine.txt" in the save dialog and press Enter
	Then the chat says "Saved to docs/diagrams/mine.svg" with an Open link
	And the saved file in the folder starts with the text "<?xml"
