using Avalonia.Controls;

namespace MovieSift.Desktop.Views;

/// <summary>Shows update controls within the existing settings panel.</summary>
public sealed partial class UpdatesView : UserControl
{
    /// <summary>Loads typed controls without performing update I/O.</summary>
    public UpdatesView() => InitializeComponent();
}
