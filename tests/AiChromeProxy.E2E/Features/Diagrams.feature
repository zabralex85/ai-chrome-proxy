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
