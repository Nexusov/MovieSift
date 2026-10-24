using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using TrackTrim.Application.Appearance;

namespace TrackTrim.Desktop.Appearance;

/// <summary>Updates semantic resources in place; never replaces workspace state or media authority.</summary>
public sealed class AppearanceApplier
{
    private readonly Avalonia.Application? application;

    /// <summary>Targets the already running Avalonia application, or a no-display test composition.</summary>
    public AppearanceApplier(Avalonia.Application? application) => this.application = application;

    public AppearanceSettings Current { get; private set; } = AppearanceSettings.Default;
    public AppearancePaletteResult Palettes { get; private set; } = AppearancePalettePolicy.Resolve(AppearanceSettings.Default);

    /// <summary>Applies an already validated pair atomically on the UI thread without rebuilding controls.</summary>
    public void Apply(AppearanceSettings settings, AppearancePaletteResult palettes)
    {
        if (!palettes.IsValid)
        {
            return;
        }

        Current = settings;
        Palettes = palettes;
        AppearanceMotionPolicy.Configure(settings.Motion);

        if (application is null)
        {
            return;
        }

        application.Resources.ThemeDictionaries[ThemeVariant.Light] = Resources(palettes.Light!);
        application.Resources.ThemeDictionaries[ThemeVariant.Dark] = Resources(palettes.Dark!);
        application.RequestedThemeVariant = settings.Theme switch
        {
            AppearanceTheme.Light => ThemeVariant.Light,
            AppearanceTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    /// <summary>Creates the explicit semantic palette and the Fluent aliases used by native Avalonia controls.</summary>
    private static ResourceDictionary Resources(AppearancePalette palette)
    {
        var resources = new ResourceDictionary();
        var colors = new Dictionary<string, Color>(StringComparer.Ordinal)
        {
            ["TTWindowBrush"] = palette.Window,
            ["TTPanelBrush"] = palette.Panel,
            ["TTSecondaryBrush"] = palette.Secondary,
            ["TTTextBrush"] = palette.Text,
            ["TTMutedBrush"] = palette.Muted,
            ["TTBorderBrush"] = palette.Border,
            ["TTAccentBrush"] = palette.Accent,
            ["TTAccentHoverBrush"] = palette.AccentHover,
            ["TTAccentPressedBrush"] = palette.AccentPressed,
            ["TTOnAccentBrush"] = palette.OnAccent,
            ["TTSelectionBrush"] = palette.Selection,
            ["TTFocusBrush"] = palette.Focus,
            ["TTErrorBrush"] = palette.Error,
            ["TTWarningBrush"] = palette.Warning,
            ["SystemControlHighlightAccentBrush"] = palette.Accent,
            ["SystemControlForegroundBaseLowBrush"] = palette.Border,
            ["TextControlForeground"] = palette.Text,
            ["TextControlBackground"] = palette.Panel,
            ["TextControlBorderBrush"] = palette.Border,
            ["TextControlForegroundFocused"] = palette.Text,
            ["TextControlBackgroundFocused"] = palette.Panel,
            ["TextControlBorderBrushFocused"] = palette.Focus,
            ["TextControlPlaceholderForeground"] = palette.Muted,
            ["MenuFlyoutPresenterBackground"] = palette.Panel,
            ["MenuFlyoutItemForeground"] = palette.Text,
            ["ToolTipForeground"] = palette.Text,
            ["ToolTipBackground"] = palette.Panel,
            ["CheckBoxCheckBackgroundFillChecked"] = palette.Accent,
            ["CheckBoxCheckGlyphForegroundChecked"] = palette.OnAccent
        };

        foreach (var (key, color) in colors)
        {
            resources[key] = new SolidColorBrush(color);
        }

        foreach (var key in new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
            "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3" })
        {
            resources[key] = palette.Accent;
        }

        ApplyControlAliases(resources, palette);
        return resources;
    }

    /// <summary>Maps the pinned Fluent template state resources to the same validated semantic pairs.</summary>
    private static void ApplyControlAliases(ResourceDictionary resources, AppearancePalette palette)
    {
        foreach (var state in new[] { string.Empty, "PointerOver", "Pressed", "Disabled", "Focused", "FocusedPressed" })
        {
            var foreground = state == "Disabled" ? palette.Muted : palette.Text;
            resources[$"TextControlForeground{state}"] = new SolidColorBrush(foreground);
            resources[$"TextControlBackground{state}"] = new SolidColorBrush(palette.Panel);
            resources[$"TextControlBorderBrush{state}"] = new SolidColorBrush(state == "Focused" ? palette.Focus : palette.Border);
            resources[$"TextControlButtonForeground{state}"] = new SolidColorBrush(foreground);
            resources[$"TextControlButtonBackground{state}"] = new SolidColorBrush(palette.Panel);
            resources[$"TextControlButtonBorderBrush{state}"] = new SolidColorBrush(palette.Border);
            resources[$"ComboBoxForeground{state}"] = new SolidColorBrush(foreground);
            resources[$"ComboBoxBackground{state}"] = new SolidColorBrush(palette.Panel);
            resources[$"ComboBoxBorderBrush{state}"] = new SolidColorBrush(palette.Border);
            resources[$"ComboBoxDropDownGlyphForeground{state}"] = new SolidColorBrush(foreground);
            resources[$"MenuFlyoutItemForeground{state}"] = new SolidColorBrush(foreground);
            resources[$"MenuFlyoutItemKeyboardAcceleratorTextForeground{state}"] = new SolidColorBrush(foreground);
            resources[$"MenuFlyoutItemBackground{state}"] = new SolidColorBrush(state == "PointerOver" ? palette.Selection : palette.Panel);
        }

        foreach (var state in new[] { string.Empty, "Disabled", "PointerOver", "Pressed", "Selected", "SelectedDisabled", "SelectedPointerOver", "SelectedPressed" })
        {
            resources[$"ComboBoxItemForeground{state}"] = new SolidColorBrush(state.Contains("Disabled", StringComparison.Ordinal) ? palette.Muted : palette.Text);
            resources[$"ComboBoxItemBackground{state}"] = new SolidColorBrush(state.Length == 0 ? palette.Panel : palette.Selection);
            resources[$"ComboBoxItemBorderBrush{state}"] = new SolidColorBrush(palette.Border);
        }

        foreach (var value in new[] { "Unchecked", "Checked", "Indeterminate" })
        {
            foreach (var state in new[] { string.Empty, "Disabled", "PointerOver", "Pressed" })
            {
                var checkedState = value != "Unchecked";
                resources[$"CheckBoxForeground{value}{state}"] = new SolidColorBrush(state == "Disabled" ? palette.Muted : palette.Text);
                resources[$"CheckBoxBackground{value}{state}"] = new SolidColorBrush(Colors.Transparent);
                resources[$"CheckBoxBorderBrush{value}{state}"] = new SolidColorBrush(Colors.Transparent);
                resources[$"CheckBoxCheckBackgroundFill{value}{state}"] = new SolidColorBrush(checkedState ? palette.Accent : palette.Panel);
                resources[$"CheckBoxCheckBackgroundStroke{value}{state}"] = new SolidColorBrush(checkedState ? palette.Accent : palette.Border);
                resources[$"CheckBoxCheckGlyphForeground{value}{state}"] = new SolidColorBrush(palette.OnAccent);
            }
        }

        foreach (var state in new[] { string.Empty, "PointerOver", "Pressed", "Disabled" })
        {
            var foreground = state == "Disabled" ? palette.Muted : palette.Text;
            var background = state == "PointerOver" ? palette.Selection :
                state == "Pressed" ? palette.Secondary : palette.Panel;
            resources[$"ExpanderHeaderBackground{state}"] = new SolidColorBrush(background);
            resources[$"ExpanderHeaderForeground{state}"] = new SolidColorBrush(foreground);
            resources[$"ExpanderHeaderBorderBrush{state}"] = new SolidColorBrush(palette.Border);
            resources[$"ExpanderChevronBackground{state}"] = new SolidColorBrush(Colors.Transparent);
            resources[$"ExpanderChevronForeground{state}"] = new SolidColorBrush(foreground);
            resources[$"ExpanderChevronBorderBrush{state}"] = new SolidColorBrush(Colors.Transparent);
        }

        foreach (var value in new[] { string.Empty, "Checked", "Indeterminate" })
        {
            foreach (var state in new[] { string.Empty, "PointerOver", "Pressed", "Disabled" })
            {
                var selected = value.Length != 0 || state == "PointerOver";
                resources[$"ToggleButtonBackground{value}{state}"] = new SolidColorBrush(selected ? palette.Selection : palette.Secondary);
                resources[$"ToggleButtonForeground{value}{state}"] = new SolidColorBrush(state == "Disabled" ? palette.Muted : palette.Text);
                resources[$"ToggleButtonBorderBrush{value}{state}"] = new SolidColorBrush(palette.Border);
            }
        }

        resources["ExpanderContentBackground"] = new SolidColorBrush(palette.Panel);
        resources["ExpanderContentBorderBrush"] = new SolidColorBrush(palette.Border);
        resources["ComboBoxDropDownBackground"] = new SolidColorBrush(palette.Panel);
        resources["ComboBoxDropDownBorderBrush"] = new SolidColorBrush(palette.Border);
        resources["ComboBoxPlaceHolderForeground"] = new SolidColorBrush(palette.Muted);
        resources["ComboBoxBackgroundBorderBrushFocused"] = new SolidColorBrush(palette.Focus);
        resources["ComboBoxBackgroundBorderBrushUnfocused"] = new SolidColorBrush(palette.Border);
        resources["MenuFlyoutPresenterBorderBrush"] = new SolidColorBrush(palette.Border);
        resources["ToolTipBorderBrush"] = new SolidColorBrush(palette.Border);
        resources["SystemControlErrorTextForegroundBrush"] = new SolidColorBrush(palette.Error);
        resources["SystemControlFocusVisualPrimaryBrush"] = new SolidColorBrush(palette.Focus);
        resources["SystemControlFocusVisualSecondaryBrush"] = new SolidColorBrush(palette.Panel);
    }
}
