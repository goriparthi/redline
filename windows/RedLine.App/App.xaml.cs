using Microsoft.UI.Xaml;

namespace RedLine.App;

public partial class App : Application
{
    private Window? window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Created but not activated: this belongs in the tray, and a window that appears at
        // login is not what a status app is for.
        window = new MainWindow();
    }
}
