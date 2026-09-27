using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Sylnar.ViewModels;

namespace Sylnar.Views.Controls;

public partial class FlashPanel : UserControl
{
    public FlashPanel()
    {
        InitializeComponent();

        // Drop a firmware file anywhere on the card; validation reports bad types.
        Card.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        Card.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        Card.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private static string? FirstLocalPath(DragEventArgs e) =>
        e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p != null);

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        bool accept = DataContext is FlashViewModel { IsFlashing: false } && FirstLocalPath(e) != null;
        e.DragEffects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        DropZone.Classes.Set("drag-over", accept);
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        DropZone.Classes.Set("drag-over", false);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropZone.Classes.Set("drag-over", false);
        if (DataContext is FlashViewModel vm && FirstLocalPath(e) is { } path)
            vm.LoadDroppedFile(path);
        e.Handled = true;
    }
}
