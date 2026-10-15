using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VSL.ViewModels;

namespace VSL.Views;

public partial class NuGetView : UserControl
{
    public NuGetView() => InitializeComponent();

    NuGetViewModel? Vm => DataContext as NuGetViewModel;

    void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Vm?.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    void OnProjectUrlClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedPackage?.ProjectUrl is { } url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
            }
            catch (Exception)
            {
                // No browser registered.
            }
        }
    }
}
