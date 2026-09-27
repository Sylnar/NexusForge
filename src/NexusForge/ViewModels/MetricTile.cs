namespace Sylnar.ViewModels;

/// <summary>One result tile on the DMA Test page (label over a mono value).</summary>
public sealed class MetricTile : BaseViewModel
{
    private string _label;
    private string _value = "—";

    public MetricTile(string label) => _label = label;

    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }
}
