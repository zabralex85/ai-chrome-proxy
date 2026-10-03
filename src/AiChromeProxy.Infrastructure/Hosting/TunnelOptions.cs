namespace AiChromeProxy.Infrastructure.Hosting;

/// <summary>Section <c>Tunnel</c>: the Cloudflare Tunnel the Server runs <c>cloudflared</c> for. Written by the tray's remote access wizard.</summary>
public sealed class TunnelOptions
{
	public const string Section = "Tunnel";

	/// <summary>Secret: the tunnel's run token. Empty = no tunnel (the Server does not start <c>cloudflared</c>).</summary>
	public string Token { get; set; } = string.Empty;

	/// <summary>Optional full path of <c>cloudflared</c>; empty = the bundled <c>cloudflared.exe</c> next to the Server, else <c>cloudflared</c> on PATH.</summary>
	public string CloudflaredPath { get; set; } = string.Empty;
}
