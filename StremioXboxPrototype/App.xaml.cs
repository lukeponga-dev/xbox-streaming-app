using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using System;
using System.Diagnostics;

namespace StremioXboxPrototype;
public sealed partial class App : Application
{
    public App()
    {
        UnhandledException += (_, e) => Debug.WriteLine($"Unhandled XAML startup exception: {e.Message}{Environment.NewLine}{e.Exception}");
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        var root = Window.Current.Content as Frame ?? new Frame();
        if (root.Content == null) root.Navigate(typeof(MainPage));
        Window.Current.Content = root; Window.Current.Activate();
    }
}
