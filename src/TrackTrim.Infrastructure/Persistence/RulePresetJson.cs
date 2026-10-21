using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TrackTrim.Application.Presets;
using TrackTrim.Core.Rules;

namespace TrackTrim.Infrastructure.Persistence;

/// <summary>Encodes the complete bounded preset schema without accepting missing or surplus fields.</summary>
internal static class RulePresetJson
{
    internal const int CurrentVersion = 1;
    internal const int MaximumBytes = 65_536;
    private const int MaximumDepth = 16;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = MaximumDepth,
        WriteIndented = true,
        Converters = { new RulePresetModeConverter() },
    };

    /// <summary>Rejects malformed structure before converting validated storage records into immutable rules.</summary>
    internal static RulePresetCatalogResult Read(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            _ = StrictUtf8.GetCharCount(bytes.Span);
        }
        catch (DecoderFallbackException exception)
        {
            throw new JsonException("The preset catalog must contain valid UTF-8.", exception);
        }

        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = MaximumDepth });
        RejectDuplicateProperties(document.RootElement);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version))
        {
            throw new JsonException("The preset catalog must contain an integer version.");
        }

        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var versionNumber))
        {
            throw new JsonException("The preset catalog must contain an integer version.");
        }

        if (versionNumber != CurrentVersion)
        {
            return new(null, new(RulePresetErrorCode.UnsupportedVersion, "The preset catalog version is not supported."));
        }

        var stored = JsonSerializer.Deserialize<RulePresetDocument>(bytes.Span, Options)
            ?? throw new JsonException("The preset catalog cannot be null.");

        if (stored.Presets.IsDefault)
        {
            throw new JsonException("The preset array must be present and cannot be null.");
        }

        var catalog = new RulePresetCatalog(stored.Presets.Select(ReadPreset).ToImmutableArray());
        var failure = RulePresetCatalogPolicy.Validate(catalog);
        return failure is null ? new(catalog) : new(null, failure);
    }

    /// <summary>Measures the actual persisted UTF-8 representation after pure catalog validation.</summary>
    internal static byte[] Write(RulePresetCatalog catalog)
    {
        var document = new RulePresetDocument(CurrentVersion, catalog.Items.Select(WritePreset).ToImmutableArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Options);

        if (bytes.Length > MaximumBytes)
        {
            throw new RulePresetStorageException(new(RulePresetErrorCode.LimitExceeded,
                "The serialized preset catalog exceeds 65536 UTF-8 bytes."));
        }

        return bytes;
    }

    /// <summary>Rejects duplicate names at every object level instead of accepting the last JSON value.</summary>
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new JsonException("The preset catalog contains a duplicate JSON property.");
            }

            RejectDuplicateProperties(property.Value);
        }
    }

    /// <summary>Converts only complete non-null records accepted by the strict serializer.</summary>
    private static RulePreset ReadPreset(RulePresetRecord stored)
    {
        if (stored is null || stored.Rules is null)
        {
            throw new JsonException("A preset entry cannot be null.");
        }

        return new(stored.Id, stored.Name, new(ReadTrack(stored.Rules.Audio), ReadTrack(stored.Rules.Subtitles),
            stored.Rules.KeepOnlyForcedSubtitles));
    }

    /// <summary>Retains raw language order and inactive title text without matching or normalization.</summary>
    private static TrackLanguageRule ReadTrack(RulePresetTrackRule stored)
    {
        if (stored is null || stored.TitleFilter is null)
        {
            throw new JsonException("A track-rule configuration cannot be null.");
        }

        return new(stored.Mode, stored.Languages, new(stored.TitleFilter.Enabled, stored.TitleFilter.Text));
    }

    /// <summary>Writes every configuration field, including inactive values.</summary>
    private static RulePresetRecord WritePreset(RulePreset preset) => new(preset.Id, preset.Name,
        new(WriteTrack(preset.Rules.Audio), WriteTrack(preset.Rules.Subtitles), preset.Rules.KeepOnlyForcedSubtitles));

    /// <summary>Writes an explicit disabled-empty filter for the equivalent optional in-memory default.</summary>
    private static RulePresetTrackRule WriteTrack(TrackLanguageRule rule) => new(rule.Mode, rule.Languages,
        rule.TitleFilter is { } filter ? new(filter.Enabled, filter.Text) : new(false, string.Empty));
}
