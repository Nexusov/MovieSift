using Avalonia.Controls;

namespace TrackTrim.Desktop.Views;

/// <summary>Hosts typed batch presentation without taking ownership of media work.</summary>
public sealed partial class BatchView : UserControl
{
    /// <summary>Loads the compiled file-list and independent inspector bindings.</summary>
    public BatchView()
    {
        InitializeComponent();
    }
}
