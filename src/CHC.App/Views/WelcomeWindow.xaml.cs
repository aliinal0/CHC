using CHC.App.ViewModels;
using Wpf.Ui.Controls;

namespace CHC.App.Views;

/// <summary>The guide on first start. Closing it at any point just leaves everything as it is.</summary>
public partial class WelcomeWindow : FluentWindow
{
    public WelcomeWindow(WelcomeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Finished += Close;
    }
}
