using Avalonia.Controls;

namespace MovieSift.Desktop.Views;

/// <summary>Shows one accepted row's actual tracks without an aggregate grouping or duplicated editor.</summary>
public sealed partial class SingleFileTracksView : UserControl
{
    /// <summary>Loads only presentation bindings to the existing per-file editor.</summary>
    public SingleFileTracksView() => InitializeComponent();
}
