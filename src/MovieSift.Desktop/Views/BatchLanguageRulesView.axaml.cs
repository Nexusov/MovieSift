using Avalonia.Controls;

namespace MovieSift.Desktop.Views;

/// <summary>Hosts compiled language-rule bindings without media or selection policy in code-behind.</summary>
public partial class BatchLanguageRulesView : UserControl
{
    /// <summary>Loads the localized rule controls and per-file preview.</summary>
    public BatchLanguageRulesView() => InitializeComponent();
}
