using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VSL.ViewModels;

namespace VSL.Views;

/// <summary>Add New Item: closes with the chosen template and name, or null.</summary>
public partial class AddItemWindow : Window
{
    public AddItemWindow()
    {
        InitializeComponent();
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            // The name is what one usually types: focused with the base name selected ("Class1" of "Class1.cs").
            NameBox.Focus();
            var name = NameBox.Text ?? "";
            NameBox.SelectionStart = 0;
            NameBox.SelectionEnd = Path.GetFileNameWithoutExtension(name).Length;
        });
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Vm?.Result is { } result)
            {
                Close(result);
                e.Handled = true;
            }
            else if (e.Key == Key.E && e.KeyModifiers == KeyModifiers.Control)
            {
                SearchBox.Focus();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    AddItemViewModel? Vm => DataContext as AddItemViewModel;

    void OnAdd(object? sender, RoutedEventArgs e) => Close(Vm?.Result);

    void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    void OnTemplateDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm?.Result is { } result)
            Close(result);
    }
}
