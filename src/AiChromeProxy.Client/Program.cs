using AiChromeProxy.Client;
using AiChromeProxy.Client.Chat;
using AiChromeProxy.Client.Shell;
using AiChromeProxy.Client.Sync;
using AiChromeProxy.Client.Transport;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.SignalR.Client;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var hubUrl = new Uri(new Uri(builder.HostEnvironment.BaseAddress), "hub");
builder.Services.AddSingleton<ITransport>(_ => new SignalRTransport(
	new HubConnectionBuilder().WithUrl(hubUrl).WithAutomaticReconnect(new ForeverRetryPolicy()).Build()));
builder.Services.AddSingleton<JsFolderAccess>();
builder.Services.AddSingleton<JsChatView>();
builder.Services.AddSingleton<IFolderAccess>(s => s.GetRequiredService<JsFolderAccess>());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SyncEngine>();
builder.Services.AddSingleton<ChatEngine>();
builder.Services.AddSingleton(new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

var host = builder.Build();
await host.Services.GetRequiredService<JsFolderAccess>().InitAsync();
await host.RunAsync();
