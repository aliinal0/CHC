using System.Windows;
using CHC.App.Services;
using Wpf.Ui.Controls;

namespace CHC.App.Views;

/// <summary>Asks what closing the window means: keep recording in the background, or exit CHC. Null means stay open.</summary>
public partial class CloseDialog : FluentWindow
{
    public CloseDialog() => InitializeComponent();

    /// <summary>What the user picked (<see cref="CloseActions"/>); null when they cancelled.</summary>
    public string? Choice { get; private set; }

    public bool Remember => RememberBox.IsChecked == true;

    private void OnKeep(object sender, RoutedEventArgs e) => Finish(CloseActions.Background);

    private void OnExit(object sender, RoutedEventArgs e) => Finish(CloseActions.Exit);

    private void OnCancel(object sender, RoutedEventArgs e) => Finish(null);

    private void Finish(string? choice)
    {
        Choice = choice;
        Close();
    }
}
