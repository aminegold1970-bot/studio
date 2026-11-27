using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Storage;

namespace AssetStudioMobile;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		UserAppTheme = Preferences.Get("AssetStudio.NightMode", false)
			? AppTheme.Dark
			: AppTheme.Light;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}
}