namespace AiChromeProxy.Tray.Clef;

/// <summary>Serilog levels as written to CLEF <c>@l</c> (absent means Information).</summary>
public enum ClefLevel
{
	Verbose,
	Debug,
	Information,
	Warning,
	Error,
	Fatal,
}
