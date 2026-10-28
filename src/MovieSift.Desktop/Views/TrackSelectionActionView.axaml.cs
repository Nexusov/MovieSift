using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace MovieSift.Desktop.Views;

/// <summary>Shares a readable vector Exclude/Restore button without owning selection state or commands.</summary>
public sealed partial class TrackSelectionActionView : UserControl
{
    public static readonly StyledProperty<ICommand?> CommandProperty = AvaloniaProperty.Register<TrackSelectionActionView, ICommand?>(nameof(Command));
    public static readonly StyledProperty<bool> IsRestoreProperty = AvaloniaProperty.Register<TrackSelectionActionView, bool>(nameof(IsRestore));
    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<TrackSelectionActionView, string>(nameof(Text), string.Empty);
    public static readonly StyledProperty<string> AccessibleNameProperty = AvaloniaProperty.Register<TrackSelectionActionView, string>(nameof(AccessibleName), string.Empty);
    public static readonly StyledProperty<string> HelpProperty = AvaloniaProperty.Register<TrackSelectionActionView, string>(nameof(Help), string.Empty);

    public ICommand? Command { get => GetValue(CommandProperty); set => SetValue(CommandProperty, value); }
    public bool IsRestore { get => GetValue(IsRestoreProperty); set => SetValue(IsRestoreProperty, value); }
    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string AccessibleName { get => GetValue(AccessibleNameProperty); set => SetValue(AccessibleNameProperty, value); }
    public string Help { get => GetValue(HelpProperty); set => SetValue(HelpProperty, value); }

    /// <summary>Loads a typed presentation-only control reused by file, group and occurrence cards.</summary>
    public TrackSelectionActionView() => InitializeComponent();
}
