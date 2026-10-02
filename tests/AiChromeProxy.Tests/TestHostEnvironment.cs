using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AiChromeProxy.Tests;

public sealed class TestHostEnvironment(string name) : IHostEnvironment
{
	public string EnvironmentName { get; set; } = name;

	public string ApplicationName { get; set; } = "test";

	public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

	public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
