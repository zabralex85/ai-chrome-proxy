namespace AiChromeProxy.Infrastructure.Cloudflare;

/// <summary>An active zone the API token can read, with the account that owns it.</summary>
public sealed record CloudflareZone(string Id, string Name, CloudflareAccount Account);
