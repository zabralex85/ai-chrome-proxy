using AiChromeProxy.Tray.ViewModels;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AiChromeProxy.Tray.Views;

public partial class LogsWindow : Window
{
	private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };

	public LogsWindow()
	{
		InitializeComponent();
		_poll.Tick += (_, _) =>
		{
			if (DataContext is LogsViewModel { Follow: true } vm)
			{
				vm.Poll();
				if (vm.Entries.Count > 0)
				{
					EntryList.ScrollIntoView(vm.Entries[^1]);
				}
			}
		};
		Opened += (_, _) => _poll.Start();
		Closed += (_, _) => _poll.Stop();
	}
}
