namespace AiChromeProxy.Client.Packages;

/// <summary>Where a package of the repo stands.</summary>
public enum PackageState
{
	OnServer,
	Uploading,
	Public,
	NotOnComputer,
	TooLarge,
	Failed,
}
