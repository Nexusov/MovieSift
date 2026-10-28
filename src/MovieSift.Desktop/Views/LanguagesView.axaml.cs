using Avalonia.Controls;

namespace MovieSift.Desktop.Views;

/// <summary>Hosts compiled presentation bindings for explicit translation downloads.</summary>
public sealed partial class LanguagesView : UserControl
{
    /// <summary>Loads the language view without performing network or filesystem work.</summary>
    public LanguagesView() => InitializeComponent();
}
