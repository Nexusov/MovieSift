using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TrackTrim.Application.Appearance;

namespace TrackTrim.Infrastructure.Persistence;

/// <summary>Encodes one bounded cosmetic record independently from the language-only settings document.</summary>
internal static class AppearanceJson
{
    internal const int CurrentVersion = 1;
    internal const int MaximumBytes = 65_536;
    private const int MaximumDepth = 8;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = MaximumDepth,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<AppearanceTheme>(allowIntegerValues: false),
            new JsonStringEnumConverter<AppearanceMotion>(allowIntegerValues: false) }
    };

    /// <summary>Rejects malformed/incompatible records without selecting fallback values for a later rewrite.</summary>
    internal static AppearanceLoadResult Read(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            _ = StrictUtf8.GetCharCount(bytes.Span);
        }
        catch (DecoderFallbackException exception)
        {
            throw new JsonException("Appearance settings must use valid UTF-8.", exception);
        }

        using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = MaximumDepth });
        RejectDuplicateProperties(parsed.RootElement);
        var root = parsed.RootElement;
        var hasVersion = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("version", out var version) &&
            version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out _);

        if (!hasVersion)
        {
            throw new JsonException("Appearance settings require an integer schema version.");
        }

        if (root.GetProperty("version").GetInt32() != CurrentVersion)
        {
            return new(null, new(AppearanceErrorCode.UnsupportedVersion, "The appearance schema version is unsupported."));
        }

        var stored = JsonSerializer.Deserialize<AppearanceDocument>(bytes.Span, Options)
            ?? throw new JsonException("Appearance settings cannot be null.");
        var settings = new AppearanceSettings(stored.Theme, stored.AccentHex,
            stored.LightOverrides, stored.DarkOverrides, stored.Motion);

        if (!AppearanceSettingsPolicy.IsValid(settings))
        {
            throw new JsonException("Appearance settings contain invalid values.");
        }

        return new(settings);
    }

    /// <summary>Checks the complete serialized byte budget before any temporary file is created.</summary>
    internal static byte[] Write(AppearanceSettings settings)
    {
        var document = new AppearanceDocument(CurrentVersion, settings.Theme, settings.AccentHex,
            settings.LightOverrides, settings.DarkOverrides, settings.Motion);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Options);

        if (bytes.Length > MaximumBytes)
        {
            throw new JsonException("Appearance settings exceed 65536 UTF-8 bytes.");
        }

        return bytes;
    }

    /// <summary>Rejects repeated field names rather than silently accepting a last-value interpretation.</summary>
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject())
        {
            string name;

            try
            {
                name = property.Name;
            }
            catch (InvalidOperationException exception)
            {
                throw new JsonException("Appearance settings contain an invalid property name.", exception);
            }

            if (!names.Add(name))
            {
                throw new JsonException("Appearance settings contain a duplicate property.");
            }

            RejectDuplicateProperties(property.Value);
        }
    }
}
