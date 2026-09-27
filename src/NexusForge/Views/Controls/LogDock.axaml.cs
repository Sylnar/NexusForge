using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sylnar.ViewModels;

namespace Sylnar.Views.Controls;

public partial class LogDock : UserControl
{
    private MainViewModel? _subscribedVm;

    public LogDock()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_subscribedVm != null)
            _subscribedVm.VisibleLogEntries.CollectionChanged -= OnLogEntriesChanged;

        _subscribedVm = DataContext as MainViewModel;
        if (_subscribedVm != null)
            _subscribedVm.VisibleLogEntries.CollectionChanged += OnLogEntriesChanged;
    }

    // Keep the newest line in view.
    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;

        Dispatcher.UIThread.Post(() =>
        {
            var scrollViewer = LogList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scrollViewer != null)
                scrollViewer.ScrollToEnd();
            else if (LogList.ItemCount > 0)
                LogList.ScrollIntoView(LogList.ItemCount - 1);
        }, DispatcherPriority.Render);
    }
}
