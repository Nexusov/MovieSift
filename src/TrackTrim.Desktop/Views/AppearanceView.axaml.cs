using Avalonia.Controls;

namespace TrackTrim.Desktop.Views;

/// <summary>Hosts compiled appearance bindings without owning storage or workspace behavior.</summary>
public sealed partial class AppearanceView : UserControl
{
    /// <summary>Loads the official color picker and typed presentation controls.</summary>
    public AppearanceView() => InitializeComponent();
}
