using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MovieSift.Desktop.Views;

/// <summary>Offers adjacent help on native hover or keyboard focus without opening a click flyout.</summary>
public sealed partial class HelpHintView : UserControl
{
    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<HelpHintView, string>(nameof(Text), string.Empty);
    public static readonly StyledProperty<string> LabelProperty = AvaloniaProperty.Register<HelpHintView, string>(nameof(Label), string.Empty);
    private TopLevel? host;

    /// <summary>Loads typed presentation bindings; help never invokes a workspace command.</summary>
    public HelpHintView() => InitializeComponent();

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>Allows Escape to dismiss hover help even when keyboard focus remains in the editor.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs eventArguments)
    {
        base.OnAttachedToVisualTree(eventArguments);
        host = TopLevel.GetTopLevel(this);
        host?.AddHandler(KeyDownEvent, OnHostKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Closes the cosmetic popup and releases its host subscription when the view leaves the workspace.</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs eventArguments)
    {
        ToolTip.SetIsOpen(HintButton, false);
        host?.RemoveHandler(KeyDownEvent, OnHostKeyDown);
        host = null;
        base.OnDetachedFromVisualTree(eventArguments);
    }

    /// <summary>Makes the same explanatory text available without requiring pointer input.</summary>
    private void OnHintFocused(object? sender, FocusChangedEventArgs eventArguments) => ToolTip.SetIsOpen(HintButton, true);

    /// <summary>Shows hover help immediately, without requiring a click or an asynchronous delay.</summary>
    private void OnHintPointerEntered(object? sender, PointerEventArgs eventArguments) => ToolTip.SetIsOpen(HintButton, true);

    /// <summary>Releases keyboard help without changing the user's selections or current plan.</summary>
    private void OnHintBlurred(object? sender, RoutedEventArgs eventArguments) => ToolTip.SetIsOpen(HintButton, false);

    /// <summary>Consumes only Escape for an open tooltip; subsequent Escape retains its existing workspace meaning.</summary>
    private void OnHostKeyDown(object? sender, KeyEventArgs eventArguments)
    {
        var dismissesHelp = eventArguments.Key == Key.Escape && ToolTip.GetIsOpen(HintButton);

        if (!dismissesHelp)
        {
            return;
        }

        ToolTip.SetIsOpen(HintButton, false);
        eventArguments.Handled = true;
    }
}
