namespace AiChromeProxy.Domain;

public static class MessageTypes
{
	public const string Ping = "ping";
	public const string Pong = "pong";
	public const string Error = "error";

	/// <summary>Client → server: start a sync session for a folder (<c>Sync.SyncOpenPayload</c>); reply <see cref="SyncOpened"/>.</summary>
	public const string SyncOpen = "sync.open";

	/// <summary>Server → client: the session is open (<c>Sync.SyncOpenPayload</c> with the sanitized repo name).</summary>
	public const string SyncOpened = "sync.opened";

	/// <summary>Client → server: one page of the full manifest (<c>Sync.SyncManifestPayload</c>); reply <see cref="SyncNeed"/>.</summary>
	public const string SyncManifest = "sync.manifest";

	/// <summary>Server → client: paths to upload (<c>Sync.SyncNeedPayload</c>).</summary>
	public const string SyncNeed = "sync.need";

	/// <summary>Client → server: part of a needed file (<c>Sync.SyncChunkPayload</c>); the last one is answered with <see cref="SyncStored"/>.</summary>
	public const string SyncChunk = "sync.chunk";

	/// <summary>Server → client: a file is in the mirror (<c>Sync.SyncStoredPayload</c>).</summary>
	public const string SyncStored = "sync.stored";

	/// <summary>Client → server: changes since the last manifest (<c>Sync.SyncDeltaPayload</c>); reply <see cref="SyncNeed"/>.</summary>
	public const string SyncDelta = "sync.delta";

	/// <summary>Server → client: files changed in the mirror (<c>Sync.SyncRemotePayload</c>).</summary>
	public const string SyncRemote = "sync.remote";

	/// <summary>Client → server: asks for a mirror file (<c>Sync.SyncFetchPayload</c>); reply <see cref="SyncData"/>.</summary>
	public const string SyncFetch = "sync.fetch";

	/// <summary>Server → client: part of a mirror file (<c>Sync.SyncDataPayload</c>).</summary>
	public const string SyncData = "sync.data";

	/// <summary>Client → server: a remote change is applied (<c>Sync.SyncAckPayload</c>); echoed back.</summary>
	public const string SyncAck = "sync.ack";

	/// <summary>Client → server: asks for the project settings (<c>Sync.ProjectSettingsPayload</c>); reply <see cref="ProjectSettings"/>.</summary>
	public const string ProjectSettingsGet = "project.settings.get";

	/// <summary>Client → server: saves the project settings (<c>Sync.ProjectSettingsPayload</c>); reply <see cref="ProjectSettings"/>.</summary>
	public const string ProjectSettingsSet = "project.settings.set";

	/// <summary>Server → client: the project settings (<c>Sync.ProjectSettingsPayload</c>).</summary>
	public const string ProjectSettings = "project.settings";

	/// <summary>Client → server: subscribes to a repo's chat (<c>Chat.ChatOpenPayload</c>); reply <see cref="ChatSessions"/>.</summary>
	public const string ChatOpen = "chat.open";

	/// <summary>Server → client: the repo's sessions (<c>Chat.ChatSessionsPayload</c>).</summary>
	public const string ChatSessions = "chat.sessions";

	/// <summary>Client → server: asks for stored events (<c>Chat.ChatHistoryPayload</c>); reply <see cref="ChatEvents"/>.</summary>
	public const string ChatHistory = "chat.history";

	/// <summary>Server → client: one page of stored events (<c>Chat.ChatEventsPayload</c>).</summary>
	public const string ChatEvents = "chat.events";

	/// <summary>Client → server: a user message (<c>Chat.ChatSendPayload</c>); reply <see cref="ChatStarted"/>, or <c>busy</c> while a run is going.</summary>
	public const string ChatSend = "chat.send";

	/// <summary>Server → client: the run was accepted (<c>Chat.ChatStartedPayload</c>).</summary>
	public const string ChatStarted = "chat.started";

	/// <summary>Client → server: stops a run (<c>Chat.ChatCancelPayload</c>); echoed back.</summary>
	public const string ChatCancel = "chat.cancel";

	/// <summary>Client → server: answers a permission request (<c>Chat.ChatApprovePayload</c>); echoed back.</summary>
	public const string ChatApprove = "chat.approve";

	/// <summary>Server → client: a live event of a run (<c>Chat.ChatEvent</c>).</summary>
	public const string ChatEvent = "chat.event";

	/// <summary>Client → server: asks for the repo's Claude tools as last recorded (<c>Chat.ClaudeToolsRequest</c>); reply <see cref="AgentTools"/>.</summary>
	public const string AgentToolsGet = "agent.tools.get";

	/// <summary>Client → server: checks the repo's Claude tools now (<c>Chat.ClaudeToolsRequest</c>); reply <see cref="AgentTools"/>.</summary>
	public const string AgentToolsCheck = "agent.tools.check";

	/// <summary>Server → client: the repo's MCP servers and plugins (<c>Chat.ClaudeToolsPayload</c>).</summary>
	public const string AgentTools = "agent.tools";
}
