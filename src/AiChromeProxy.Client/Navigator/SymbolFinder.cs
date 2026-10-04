using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AiChromeProxy.Client.Navigator;

/// <summary>Finds where a symbol is declared, without a language server: a whole-word match after a declaration keyword, else the first whole-word match.</summary>
public static partial class SymbolFinder
{
	private const int MaxDeclarationLine = 10_000;

	private const string Keywords = "class|interface|record|struct|enum|def|function|func|fn|void|public|private|protected|internal|static|const|let|var|type";

	private static readonly TimeSpan Budget = TimeSpan.FromSeconds(1);

	/// <summary>Searches <paramref name="text"/> for <paramref name="symbol"/>.</summary>
	/// <param name="text">The file's text.</param>
	/// <param name="symbol">A name matching <c>[A-Za-z_][\w.]*</c>.</param>
	/// <returns>The 1-based line, or null when the symbol is invalid or not found (or the search runs out of its 1 s budget). Never throws.</returns>
	public static int? Find(string text, string symbol)
	{
		if (!ValidSymbol().IsMatch(symbol))
		{
			return null;
		}

		var name = Regex.Escape(symbol);
		var word = new Regex($@"(?<!\w){name}(?!\w)", RegexOptions.CultureInvariant, Budget);
		var declaration = new Regex($@"\b(?:{Keywords})\b[\w<>\[\],.?\s]*?(?<!\w){name}(?!\w)", RegexOptions.CultureInvariant, Budget);
		var lines = text.Split('\n');
		var clock = Stopwatch.StartNew();
		int? first = null;
		for (var i = 0; i < lines.Length; i++)
		{
			if (clock.Elapsed >= Budget)
			{
				break; // out of budget: the cheapest result so far (the first whole-word match, or null)
			}

			try
			{
				if (lines[i].Length <= MaxDeclarationLine && declaration.IsMatch(lines[i]))
				{
					return i + 1;
				}
			}
			catch (RegexMatchTimeoutException)
			{
				// A pathological line: skip the declaration test for it and fall back to the whole-word search.
			}

			try
			{
				if (first is null && word.IsMatch(lines[i]))
				{
					first = i + 1;
				}
			}
			catch (RegexMatchTimeoutException)
			{
				break;
			}
		}

		return first;
	}

	[GeneratedRegex(@"^[A-Za-z_][\w.]*$")]
	private static partial Regex ValidSymbol();
}
