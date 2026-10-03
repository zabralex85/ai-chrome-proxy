using AiChromeProxy.Tray.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AiChromeProxy.Tray.Views;

public partial class InstallWindow : Window
{
	public InstallWindow() => InitializeComponent();

	public InstallWindow(InstallViewModel vm)
		: this()
	{
		DataContext = vm;
		vm.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(InstallViewModel.Succeeded) && vm.Succeeded)
			{
				Close();
			}
		};
	}

	private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
