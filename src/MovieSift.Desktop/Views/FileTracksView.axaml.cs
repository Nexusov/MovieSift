using Avalonia.Controls;

namespace MovieSift.Desktop.Views;

/// <summary>Presents the existing file-local selection editor in both direct and expanded contexts.</summary>
public sealed partial class FileTracksView : UserControl
{
    /// <summary>Loads compiled bindings without owning track choices or workflow authority.</summary>
    public FileTracksView() => InitializeComponent();
}
