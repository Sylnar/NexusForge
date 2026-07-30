using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Sylnar.ViewModels;

namespace Sylnar.Views.Controls;

public partial class DmaTestPanel : UserControl
{
    public DmaTestPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is DmaTestViewModel vm)
            vm.MmapReadyToSave = SaveMmapAsync;
    }

    private async Task SaveMmapAsync(string content)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title           = "Save memory map",
            SuggestedFileName = "mmap.txt",
            DefaultExtension  = "txt",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Memory map") { Patterns = new[] { "mmap.txt", "memmap.txt", "*.txt" } },
                new FilePickerFileType("All files")  { Patterns = new[] { "*" } }
            }
        });

        if (file == null) return;

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content);

        if (DataContext is DmaTestViewModel vm)
        {
            vm.MmapStatus = $"Saved to {file.Name}";
            vm.MmapStatusColor = "#00D4AA";
        }
    }
}
