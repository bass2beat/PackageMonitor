using System.Windows;
using System.Windows.Controls;
using IntuneLogMonitor.App.ViewModels;

namespace IntuneLogMonitor.App.Views;

public partial class AppWorkloadView : UserControl
{
    private AppWorkloadViewModel? _vm;

    public AppWorkloadView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
            _vm.VisibleEntriesAppended -= ScrollToEnd;
        _vm = e.NewValue as AppWorkloadViewModel;
        if (_vm is not null)
            _vm.VisibleEntriesAppended += ScrollToEnd;
    }

    private void ScrollToEnd()
    {
        if (_vm is not { AutoScroll: true } || LogGrid.Items.Count == 0)
            return;
        LogGrid.ScrollIntoView(LogGrid.Items[^1]);
    }
}
