namespace MovieSift.Application.Presets;

/// <summary>Bounds the logical catalog independently from its storage format and byte budget.</summary>
public static class RulePresetLimits
{
    public const int MaximumEntries = 100;
    public const int MaximumNameLength = 80;
    public const int MaximumLanguageValues = 32;
    public const int MaximumLanguageLength = 64;
    public const int MaximumTitleLength = 1024;
}
