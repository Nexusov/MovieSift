using Avalonia.Controls;

namespace MovieSift.Desktop.Views;

/// <summary>Hosts compiled preset catalog controls without filesystem or configuration policy.</summary>
public partial class RulePresetsView : UserControl
{
    /// <summary>Loads explicit catalog management and confirmation bindings.</summary>
    public RulePresetsView() => InitializeComponent();
}
