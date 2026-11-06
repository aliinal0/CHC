using System.Windows.Controls;
using CHC.App.ViewModels;

namespace CHC.App.Views;

public partial class LookBackView : UserControl
{
    public LookBackView()
    {
        InitializeComponent();
        Grid.HourClicked += (day, hour) => (DataContext as LookBackViewModel)?.Select(day, hour);
    }
}
