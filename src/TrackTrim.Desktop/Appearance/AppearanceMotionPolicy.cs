using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using TrackTrim.Application.Appearance;

namespace TrackTrim.Desktop.Appearance;

/// <summary>Applies cosmetic motion preferences without delaying any command, cancellation or close.</summary>
public static class AppearanceMotionPolicy
{
    private const uint GetClientAreaAnimation = 0x1042;
    private static readonly TimeSpan PressDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan HoverDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan DropDuration = TimeSpan.FromMilliseconds(300);
    private static readonly List<WeakReference<Window>> Windows = [];
    private static AppearanceMotion configured = AppearanceMotion.System;
    public static bool Reduced { get; private set; } = true;
    public static bool SystemPreferenceAvailable { get; private set; }
    public static event EventHandler? Changed;

    /// <summary>Tracks only application windows and refreshes the read-only system preference on activation.</summary>
    public static void Track(Window window)
    {
        Windows.RemoveAll(reference => !reference.TryGetTarget(out _));
        Windows.Add(new(window));
        window.Activated += (_, _) => Configure(configured);
        window.Classes.Set("reduced-motion", Reduced);
    }

    /// <summary>Uses the Windows accessibility preference where available; System otherwise reduces motion.</summary>
    public static void Configure(AppearanceMotion motion)
    {
        configured = motion;
        var systemReduced = ReadSystemPreference();
        Reduced = motion == AppearanceMotion.Reduced || motion == AppearanceMotion.System && systemReduced;

        foreach (var reference in Windows.ToArray())
        {
            if (reference.TryGetTarget(out var window))
            {
                window.Classes.Set("reduced-motion", Reduced);
            }
        }

        if (Avalonia.Application.Current is { } application)
        {
            application.Resources["TTPressDuration"] = Reduced ? TimeSpan.Zero : PressDuration;
            application.Resources["TTHoverDuration"] = Reduced ? TimeSpan.Zero : HoverDuration;
            application.Resources["TTDropDuration"] = Reduced ? TimeSpan.Zero : DropDuration;
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Reads only SPI_GETCLIENTAREAANIMATION; never changes the owner's desktop preferences.</summary>
    private static bool ReadSystemPreference()
    {
        SystemPreferenceAvailable = false;

        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            if (SystemParametersInfo(GetClientAreaAnimation, 0, out var enabled, 0))
            {
                SystemPreferenceAvailable = true;
                return enabled == 0;
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return true;
        }

        return true;
    }

    /// <summary>Queries one documented Win32 BOOL without requiring unsafe code or another UI framework.</summary>
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out int value, uint flags);
}
