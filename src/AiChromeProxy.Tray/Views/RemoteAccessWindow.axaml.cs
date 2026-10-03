using AiChromeProxy.Tray.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AiChromeProxy.Tray.Views;

public partial class RemoteAccessWindow : Window
{
	public RemoteAccessWindow() => InitializeComponent();

	protected override void OnClosed(EventArgs e)
	{
		(DataContext as RemoteAccessViewModel)?.ForgetToken();
		base.OnClosed(e);
	}

	private void OnCreateToken(object? sender, RoutedEventArgs e) => App.Open(new Uri(RemoteAccessViewModel.CreateTokenUrl));

	private void OnOpen(object? sender, RoutedEventArgs e)
	{
		if (DataContext is RemoteAccessViewModel { PublicUrl.Length: > 0 } vm)
		{
			App.Open(new Uri(vm.PublicUrl));
		}
	}
}
