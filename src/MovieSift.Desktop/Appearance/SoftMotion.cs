using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace MovieSift.Desktop.Appearance;

/// <summary>Provides finite render-only panel and once-per-result entry effects with reduced-motion cancellation.</summary>
public sealed class SoftMotion : AvaloniaObject
{
    private const double PanelOffset = 8;
    private static readonly TimeSpan ResultDuration = TimeSpan.FromMilliseconds(220);
    private static readonly ConditionalWeakTable<Control, MotionState> States = new();
    public static readonly AttachedProperty<bool> VisibleProperty = AvaloniaProperty.RegisterAttached<SoftMotion, Control, bool>("Visible", true);
    public static readonly AttachedProperty<bool> EnterProperty = AvaloniaProperty.RegisterAttached<SoftMotion, Control, bool>("Enter");
    public static readonly AttachedProperty<string?> ResultKeyProperty = AvaloniaProperty.RegisterAttached<SoftMotion, Control, string?>("ResultKey");

    /// <summary>Connects declarative view markers to visibility and immutable publication presentation.</summary>
    static SoftMotion()
    {
        VisibleProperty.Changed.AddClassHandler<Control>((control, arguments) => ChangeVisibility(control, arguments.NewValue is true));
        EnterProperty.Changed.AddClassHandler<Control>((control, arguments) =>
        {
            if (arguments.NewValue is not true)
            {
                return;
            }

            control.AttachedToVisualTree += (_, _) => Animate(control, true);
            control.PropertyChanged += (_, change) =>
            {
                if (change.Property == Visual.IsVisibleProperty && control.IsVisible)
                {
                    Animate(control, true);
                }
            };
        });
        ResultKeyProperty.Changed.AddClassHandler<Control>((control, arguments) =>
        {
            if (arguments.NewValue is not string { Length: > 0 } key)
            {
                return;
            }

            var state = States.GetOrCreateValue(control);

            if (state.ResultKey == key)
            {
                return;
            }

            state.ResultKey = key;
            Animate(control, false);
        });
    }

    /// <summary>Reads the intended cosmetic visibility independently of an outgoing frame.</summary>
    public static bool GetVisible(Control control) => control.GetValue(VisibleProperty);
    /// <summary>Requests an interruptible cosmetic transition without deferring application state.</summary>
    public static void SetVisible(Control control, bool value) => control.SetValue(VisibleProperty, value);

    /// <summary>Fades outgoing panels before hiding them; a new request always wins.</summary>
    private static async void ChangeVisibility(Control control, bool visible)
    {
        var state = States.GetOrCreateValue(control);
        var revision = ++state.VisibilityRevision;
        state.Cancellation?.Cancel();

        if (visible)
        {
            control.IsVisible = true;
            Animate(control, true);
            return;
        }

        if (AppearanceMotionPolicy.Reduced || TopLevel.GetTopLevel(control) is null)
        {
            control.IsVisible = false;
            return;
        }

        using var cancellation = new CancellationTokenSource();
        state.Cancellation = cancellation;
        EventHandler reduce = (_, _) =>
        {
            if (AppearanceMotionPolicy.Reduced)
            {
                cancellation.Cancel();
            }
        };
        AppearanceMotionPolicy.Changed += reduce;
        var animation = new Animation { Duration = AppearanceMotionPolicy.DisclosureDuration, Easing = new CubicEaseOut() };
        var start = new KeyFrame { Cue = new Cue(0) };
        start.Setters.Add(new Setter(Visual.OpacityProperty, 1d));
        var end = new KeyFrame { Cue = new Cue(1) };
        end.Setters.Add(new Setter(Visual.OpacityProperty, 0d));
        animation.Children.Add(start);
        animation.Children.Add(end);

        try
        {
            await animation.RunAsync(control, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // A subsequent open or close supersedes only the outgoing presentation.
        }
        finally
        {
            AppearanceMotionPolicy.Changed -= reduce;

            if (ReferenceEquals(state.Cancellation, cancellation))
            {
                state.Cancellation = null;
            }

            if (revision == state.VisibilityRevision && !GetVisible(control))
            {
                control.IsVisible = false;
            }
        }
    }

    /// <summary>Reads the declarative finite-panel entry marker.</summary>
    public static bool GetEnter(Control control) => control.GetValue(EnterProperty);
    /// <summary>Marks a panel for visibility-based cosmetic entry.</summary>
    public static void SetEnter(Control control, bool value) => control.SetValue(EnterProperty, value);
    /// <summary>Reads the publication identity used by the presentation-only entry effect.</summary>
    public static string? GetResultKey(Control control) => control.GetValue(ResultKeyProperty);
    /// <summary>Assigns a displayed publication identity without changing its recorded result.</summary>
    public static void SetResultKey(Control control, string? value) => control.SetValue(ResultKeyProperty, value);

    /// <summary>Starts a finite visual animation that carries no workflow authority.</summary>
    private static async void Animate(Control control, bool panel)
    {
        var state = States.GetOrCreateValue(control);
        state.Cancellation?.Cancel();
        state.Cancellation = null;

        if (AppearanceMotionPolicy.Reduced || !control.IsVisible)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        state.Cancellation = cancellation;
        EventHandler reduce = (_, _) =>
        {
            if (AppearanceMotionPolicy.Reduced)
            {
                cancellation.Cancel();
            }
        };
        AppearanceMotionPolicy.Changed += reduce;
        var animation = new Animation { Duration = panel ? AppearanceMotionPolicy.DisclosureDuration : ResultDuration, Easing = new CubicEaseOut() };
        var start = new KeyFrame { Cue = new Cue(0) };
        start.Setters.Add(new Setter(Visual.OpacityProperty, 0d));
        var end = new KeyFrame { Cue = new Cue(1) };
        end.Setters.Add(new Setter(Visual.OpacityProperty, 1d));

        if (panel)
        {
            start.Setters.Add(new Setter(TranslateTransform.YProperty, PanelOffset));
            end.Setters.Add(new Setter(TranslateTransform.YProperty, 0d));
        }

        animation.Children.Add(start);
        animation.Children.Add(end);

        try
        {
            await animation.RunAsync(control, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // A presentation-only cancellation never cancels or delays application work.
        }
        finally
        {
            AppearanceMotionPolicy.Changed -= reduce;

            if (ReferenceEquals(state.Cancellation, cancellation))
            {
                state.Cancellation = null;
            }
        }
    }

    /// <summary>Retains only the current effect and displayed result identity for one live control.</summary>
    private sealed class MotionState
    {
        public CancellationTokenSource? Cancellation { get; set; }
        public string? ResultKey { get; set; }
        public long VisibilityRevision { get; set; }
    }
}
