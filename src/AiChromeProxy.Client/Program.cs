using AiChromeProxy.Client;
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
builder.Services.AddSingleton<IFolderAccess, JsFolderAccess>();

await builder.Build().RunAsync();
