using AiChromeProxy.Tray.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiChromeProxy.Tray.ViewModels;

/// <summary>The elevated install dialog: the account the service runs as (default: the tray user) and its password.</summary>
public sealed partial class InstallViewModel : ObservableObject
{
	private readonly IServiceControl _service;
	private readonly string _controlUser;

	public InstallViewModel(IServiceControl service, string controlUser)
	{
		_service = service;
		_controlUser = controlUser;
		Account = controlUser;
	}

	[ObservableProperty]
	public partial string Account { get; set; }

	[ObservableProperty]
	public partial string Password { get; set; } = string.Empty;

	[ObservableProperty]
	public partial string? Error { get; private set; }

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(InstallCommand))]
	public partial bool IsBusy { get; private set; }

	/// <summary>Set once the service is installed and started; the window then closes with exit code 0.</summary>
	[ObservableProperty]
	public partial bool Succeeded { get; private set; }

	/// <summary>Exit code of the elevated instance: 0 once installed, otherwise "cancelled" (the dialog already showed any error).</summary>
	public int ExitCode => Succeeded ? 0 : AdminCommand.Cancelled;

	[RelayCommand(CanExecute = nameof(CanInstall))]
	private async Task InstallAsync()
	{
		Error = null;
		if (string.IsNullOrWhiteSpace(Account) || Password.Length == 0)
		{
			Error = "Enter the account and its Windows password.";
			return;
		}

		IsBusy = true;
		try
		{
			var account = Account.Trim();
			var password = Password;
			await Task.Run(() => _service.Install(account, password, _controlUser));
			Password = string.Empty;
			Succeeded = true;
		}
		catch (Exception ex)
		{
			Error = ex.InnerException?.Message ?? ex.Message;
		}
		finally
		{
			IsBusy = false;
		}
	}

	private bool CanInstall() => !IsBusy;
}
