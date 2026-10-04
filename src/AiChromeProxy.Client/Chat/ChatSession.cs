using System.Text;
using AiChromeProxy.Domain.Chat;

namespace AiChromeProxy.Client.Chat;

/// <summary>
/// One chat as the browser knows it: the stored events by <c>seq</c> (a replay of an event already held is dropped) plus the live
/// text deltas of the runs in progress. <see cref="Items"/> and <see cref="Approvals"/> are derived from them, so a catch-up after a
/// reconnect, a live push and a page loaded from history all end in the same view whatever order they arrive in.
/// </summary>
public sealed class ChatSession
{
	private const int TitleChars = 60;

	private readonly Lock _gate = new();
	private readonly SortedDictionary<long, ChatEvent> _events = [];
	private readonly Dictionary<string, Run> _runs = new(StringComparer.Ordinal);
	private readonly List<ChatItem> _local = [];
	private IReadOnlyList<ChatItem>? _items;
	private IReadOnlyList<ChatApproval> _approvals = [];
	private long _watermark;
	private long _costSeq;
	private bool _listedRunning;

	public ChatSession(string? id)
	{
		Id = id;
	}

	/// <summary>Gets the server's session id; null for the draft of a new chat (nothing sent yet).</summary>
	public string? Id { get; }

	public bool IsDraft => Id is null;

	public string Title { get; private set; } = string.Empty;

	public DateTimeOffset? Updated { get; private set; }

	/// <summary>Gets the cost of the session's last run that reported one.</summary>
	public decimal? LastCost { get; private set; }

	/// <summary>Gets the rows in display order: stored events by <c>seq</c>, then the streaming text, then local error lines.</summary>
	public IReadOnlyList<ChatItem> Items
	{
		get
		{
			lock (_gate)
			{
				return _items ??= Build();
			}
		}
	}

	/// <summary>Gets the permission requests still waiting for an answer.</summary>
	public IReadOnlyList<ChatApproval> Approvals
	{
		get
		{
			lock (_gate)
			{
				_items ??= Build();
				return _approvals;
			}
		}
	}

	/// <summary>Gets the run in progress (the session's newest run, while it has no result), or null.</summary>
	public string? ActiveRunId
	{
		get
		{
			lock (_gate)
			{
				return Active()?.Id;
			}
		}
	}

	public bool Running
	{
		get
		{
			lock (_gate)
			{
				return Active() is not null || _listedRunning;
			}
		}
	}

	/// <summary>Gets or sets a value indicating whether the history was asked for (then reconnects catch it up).</summary>
	internal bool Loaded { get; set; }

	/// <summary>Gets the highest <c>seq</c> such that every stored event up to it is held: what <c>chat.history</c> continues from.</summary>
	internal long Watermark
	{
		get
		{
			lock (_gate)
			{
				return _watermark;
			}
		}
	}

	/// <summary>Adds a stored event (<c>seq</c> of at least 1); false when it is a replay.</summary>
	/// <param name="e">The event.</param>
	/// <param name="live">It was pushed (not read from history): a run that looked abandoned is alive.</param>
	/// <returns>Whether the event was new.</returns>
	internal bool AddStored(ChatEvent e, bool live)
	{
		lock (_gate)
		{
			if (e.Seq < 1 || !_events.TryAdd(e.Seq, e))
			{
				return false;
			}

			while (_events.ContainsKey(_watermark + 1))
			{
				_watermark++;
			}

			var run = RunOf(e.RunId);
			run.FirstSeq = Math.Min(run.FirstSeq, e.Seq);
			if (live && e.Kind != ChatEventKinds.Result)
			{
				run.Abandoned = false;
			}

			switch (e.Kind)
			{
				case ChatEventKinds.Prompt when Title.Length == 0 && e.Text is { } text:
					Title = Shorten(text);
					break;
				case ChatEventKinds.Message or ChatEventKinds.Tool or ChatEventKinds.ToolResult:
					run.Replace(e.Seq);
					break;
				case ChatEventKinds.Result:
					run.Replace(e.Seq);
					run.Ended = true;
					_listedRunning = false;
					if (e.CostUsd is { } cost && e.Seq > _costSeq)
					{
						(LastCost, _costSeq) = (cost, e.Seq);
					}

					break;
			}

			_items = null;
			return true;
		}
	}

	/// <summary>Adds a live <c>text</c> delta (it carries the <c>seq</c> of the last stored event of the session).</summary>
	/// <param name="e">The delta.</param>
	internal void AddText(ChatEvent e)
	{
		lock (_gate)
		{
			var run = RunOf(e.RunId);
			if (run.Ended || e.Seq < run.ClearedSeq)
			{
				return;
			}

			run.Abandoned = false;
			if (run.LiveSeq != e.Seq)
			{
				run.Live.Clear();
				run.LiveSeq = e.Seq;
			}

			run.Live.Append(e.Text);
			_items = null;
		}
	}

	/// <summary>Applies the session's entry of <c>chat.sessions</c>; a session that is not running has no run in progress, whatever its events say.</summary>
	/// <param name="info">The entry.</param>
	internal void SetListed(ChatSessionInfo info)
	{
		lock (_gate)
		{
			Title = info.Title;
			Updated = info.Updated;
			_listedRunning = info.Running;
			if (!info.Running)
			{
				foreach (var run in _runs.Values.Where(r => !r.Ended))
				{
					run.Abandoned = true;
					run.Live.Clear();
				}
			}
			else if (Latest() is { Ended: false } latest)
			{
				latest.Abandoned = false;
			}

			_items = null;
		}
	}

	/// <summary>Adds an error line that is not an event (a refused request); it stays after the stored rows.</summary>
	/// <param name="text">What went wrong.</param>
	internal void AddError(string text)
	{
		lock (_gate)
		{
			_local.Add(new ChatItem(ChatItemKind.Error, string.Empty, text, IsError: true));
			_items = null;
		}
	}

	internal void ClearErrors()
	{
		lock (_gate)
		{
			if (_local.Count > 0)
			{
				_local.Clear();
				_items = null;
			}
		}
	}

	private static string Shorten(string text)
	{
		var line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
		return line.Length <= TitleChars ? line : line[..(char.IsHighSurrogate(line[TitleChars - 1]) ? TitleChars - 1 : TitleChars)];
	}

	/// <summary>A long prompt or message arrives as several consecutive events: they are one block.</summary>
	private static void Append(List<ChatItem> items, ChatItemKind kind, ChatEvent e)
	{
		var text = e.Text ?? string.Empty;
		if (items.Count > 0 && items[^1] is { Streaming: false } last && last.Kind == kind && last.RunId == e.RunId)
		{
			items[^1] = last with { Text = last.Text + text };
			return;
		}

		items.Add(new ChatItem(kind, e.RunId, text));
	}

	private Run RunOf(string runId)
	{
		if (!_runs.TryGetValue(runId, out var run))
		{
			_runs[runId] = run = new Run(runId);
		}

		return run;
	}

	private Run? Latest() => _runs.Values.MaxBy(r => r.FirstSeq);

	private Run? Active() => Latest() is { Ended: false, Abandoned: false } latest ? latest : null;

	private List<ChatItem> Build()
	{
		var items = new List<ChatItem>();
		var tools = new Dictionary<string, int>(StringComparer.Ordinal);
		var requests = new Dictionary<string, ChatApproval>(StringComparer.Ordinal);
		foreach (var e in _events.Values)
		{
			switch (e.Kind)
			{
				case ChatEventKinds.Prompt:
					Append(items, ChatItemKind.User, e);
					break;
				case ChatEventKinds.Message:
					Append(items, ChatItemKind.Assistant, e);
					break;
				case ChatEventKinds.Tool:
					tools[e.ToolId ?? string.Empty] = items.Count;
					items.Add(new ChatItem(ChatItemKind.Tool, e.RunId, e.Summary ?? string.Empty, e.Name, e.ToolId));
					break;
				case ChatEventKinds.ToolResult when tools.TryGetValue(e.ToolId ?? string.Empty, out var index):
					items[index] = items[index] with { Result = e.Summary ?? string.Empty, IsError = e.IsError == true };
					break;
				case ChatEventKinds.Permission when e.RequestId is { } request:
					requests[request] = new ChatApproval(e.RunId, request, e.Name ?? string.Empty, e.Summary ?? string.Empty);
					break;
				case ChatEventKinds.PermissionResolved when e.RequestId is { } request:
					requests.Remove(request);
					break;
				case ChatEventKinds.Result when e.Ok == false:
					items.Add(new ChatItem(ChatItemKind.Error, e.RunId, e.Error ?? "The run failed.", IsError: true));
					break;
			}
		}

		foreach (var run in _runs.Values.OrderBy(r => r.FirstSeq))
		{
			if (!run.Ended && !run.Abandoned && run.Live.Length > 0)
			{
				items.Add(new ChatItem(ChatItemKind.Assistant, run.Id, run.Live.ToString(), Streaming: true));
			}
		}

		items.AddRange(_local);
		_approvals = [.. requests.Values.Where(r => _runs.TryGetValue(r.RunId, out var run) && !run.Ended)];
		return items;
	}

	private sealed class Run(string id)
	{
		public string Id => id;

		public long FirstSeq { get; set; } = long.MaxValue;

		public bool Ended { get; set; }

		/// <summary>Gets or sets a value indicating whether the server says the session is idle although this run has no result (it died with the server).</summary>
		public bool Abandoned { get; set; }

		/// <summary>Gets the seq below which deltas were replaced by a stored event.</summary>
		public long ClearedSeq { get; private set; }

		public long LiveSeq { get; set; } = -1;

		public StringBuilder Live { get; } = new();

		/// <summary>A stored event of this run with <paramref name="seq"/>: it replaces the deltas that came before it.</summary>
		/// <param name="seq">The event's seq.</param>
		public void Replace(long seq)
		{
			ClearedSeq = Math.Max(ClearedSeq, seq);
			if (LiveSeq < seq)
			{
				Live.Clear();
			}
		}
	}
}
