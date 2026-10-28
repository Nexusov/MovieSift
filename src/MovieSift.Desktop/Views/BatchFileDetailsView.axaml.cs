using Avalonia;
using Avalonia.Controls;

namespace MovieSift.Desktop.Views;

/// <summary>Presents individual file choices without changing the aggregate scope.</summary>
public sealed partial class BatchFileDetailsView : UserControl
{
    public static readonly StyledProperty<bool> ShowTrackSelectionProperty =
        AvaloniaProperty.Register<BatchFileDetailsView, bool>(nameof(ShowTrackSelection), true);

    public bool ShowTrackSelection
    {
        get => GetValue(ShowTrackSelectionProperty);
        set => SetValue(ShowTrackSelectionProperty, value);
    }

    /// <summary>Loads the compiled presentation bindings.</summary>
    public BatchFileDetailsView()
    {
        InitializeComponent();
    }
}
