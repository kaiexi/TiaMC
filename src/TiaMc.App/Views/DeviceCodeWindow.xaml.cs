using System.Windows;
using TiaMc.App.ViewModels;

namespace TiaMc.App.Views;

/// <summary>
/// Shows the device code while the login runs in the background. The window is
/// modal so the user cannot start a second login, but the polling itself happens
/// on the caller's task.
/// </summary>
public partial class DeviceCodeWindow : Window
{
    public DeviceCodeWindow()
    {
        InitializeComponent();
    }

    public DeviceCodeViewModel ViewModel => (DeviceCodeViewModel)DataContext;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
