using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sylnar.ViewModels;

namespace Sylnar.Views.Controls;

public partial class StatusLogPanel : UserControl
{
    public StatusLogPanel()
    {
        InitializeComponent();
    }

    private MainViewModel? _subscribedVm;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        // Unhook the previous view model; a fresh lambda used to be added on
        // every DataContext change and never removed.
        if (_subscribedVm != null)
            _subscribedVm.LogEntries.CollectionChanged -= OnLogEntriesChanged;

        _subscribedVm = DataContext as MainViewModel;
        if (_subscribedVm != null)
            _subscribedVm.LogEntries.CollectionChanged += OnLogEntriesChanged;
    }

    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var list = this.FindControl<ListBox>("LogList");
            if (list == null) return;

            var scrollViewer = list.GetVisualDescendants()
                                  .OfType<ScrollViewer>()
                                  .FirstOrDefault();
            if (scrollViewer != null)
            {
                scrollViewer.ScrollToEnd();
            }
            else if (list.ItemCount > 0)
            {
                list.ScrollIntoView(list.ItemCount - 1);
            }
        }, DispatcherPriority.Render);
    }
}
