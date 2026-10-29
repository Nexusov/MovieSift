using Avalonia.Controls;

namespace MovieSift.Desktop.Views;

/// <summary>Shows update controls in the independent updates panel.</summary>
public sealed partial class UpdatesView : UserControl
{
    /// <summary>Loads typed controls without performing update I/O.</summary>
    public UpdatesView() => InitializeComponent();
}
