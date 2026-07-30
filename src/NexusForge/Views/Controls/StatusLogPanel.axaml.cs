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

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is MainViewModel vm)
        {
            vm.LogEntries.CollectionChanged += (s, args) =>
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
            };
        }
    }
}
