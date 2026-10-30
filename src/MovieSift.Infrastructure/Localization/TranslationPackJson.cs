using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MovieSift.Application.Localization;

namespace MovieSift.Infrastructure.Localization;

/// <summary>Validates bounded complete text dictionaries against the shipped English resource contract.</summary>
internal static partial class TranslationPackJson
{
    internal const int MaximumPackBytes = 262_144;
    internal const int MaximumCatalogBytes = 65_536;
    private const int MaximumTextLength = 8_192;
    private const int MaximumKeys = 2_048;

    /// <summary>Rejects unknown fields, duplicate keys and incompatible complete resource sets before publication.</summary>
    internal static TranslationPack Read(byte[] bytes, string locale, IReadOnlyDictionary<string, string> english)
    {
        using var document = Parse(bytes, MaximumPackBytes);
        var fields = Fields(document.RootElement, ["version", "locale", "messages"]);

        if (fields["version"].GetInt32() != 1 || fields["locale"].GetString() != locale)
        {
            throw new TranslationPackException(TranslationErrorCode.Incompatible, "The translation version or locale differs.");
        }

        var messages = Fields(fields["messages"], null);

        foreach (var element in messages.Values)
        {
            var value = element.GetString();

            if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumTextLength || value.Contains('\0'))
            {
                throw Invalid("A translated value is missing or exceeds its limit.");
            }

            _ = CompositeFormat.Parse(value);
        }

        if (english.Count > MaximumKeys || messages.Count > MaximumKeys)
        {
            throw Invalid("The translation exceeds its resource limit.");
        }

        if (messages.Count != english.Count || messages.Keys.Any(key => !english.ContainsKey(key)))
        {
            throw new TranslationPackException(TranslationErrorCode.ResourceContractMismatch,
                "The translation does not match this application's resource keys.");
        }

        var translated = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        foreach (var (key, element) in messages)
        {
            var value = element.GetString()!;

            if (Signature(value) != Signature(english[key]))
            {
                throw Invalid("A translation changes a formatting placeholder.");
            }

            translated.Add(key, value);
        }

        return new(locale, translated.ToImmutable());
    }

    /// <summary>Requires one fixed filename and exact digest/length for every downloaded catalog entry.</summary>
    internal static (string Hash, long Bytes) ReadCatalog(byte[] bytes, string locale)
    {
        using var document = Parse(bytes, MaximumCatalogBytes);
        var root = Fields(document.RootElement, ["version", "packs"]);

        if (root["version"].GetInt32() != 1 || root["packs"].GetArrayLength() > TranslationCatalog.Languages.Length)
        {
            throw Invalid("Invalid translation catalog version or entry count.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        (string Hash, long Bytes)? selected = null;

        foreach (var entry in root["packs"].EnumerateArray())
        {
            var fields = Fields(entry, ["locale", "file", "sha256", "bytes"]);
            var code = fields["locale"].GetString();
            var hash = fields["sha256"].GetString();
            var count = fields["bytes"].GetInt64();
            var valid = code is not null && code != "en" && TranslationCatalog.IsSupported(code) && seen.Add(code) &&
                fields["file"].GetString() == code + ".json" && hash is not null && DigestPattern().IsMatch(hash) &&
                count > 0 && count <= MaximumPackBytes;

            if (!valid)
            {
                throw Invalid("Invalid or duplicate translation catalog entry.");
            }

            if (code == locale)
            {
                selected = (hash!, count);
            }
        }

        return selected ?? throw new TranslationPackException(TranslationErrorCode.ResourceContractMismatch, "This supported locale is absent from the snapshot catalog.");
    }

    /// <summary>Bounds nesting and disables permissive JSON syntax.</summary>
    private static JsonDocument Parse(byte[] bytes, int limit)
    {
        if (bytes.Length > limit)
        {
            throw Invalid("The translation record exceeds its byte budget.");
        }

        return JsonDocument.Parse(bytes, new() { MaxDepth = 4 });
    }

    /// <summary>Rejects duplicate and unexpected JSON members instead of accepting the last value.</summary>
    private static Dictionary<string, JsonElement> Fields(JsonElement element, string[]? expected)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var field in element.EnumerateObject())
        {
            if (!fields.TryAdd(field.Name, field.Value) || fields.Count > MaximumKeys)
            {
                throw Invalid("Duplicate or excessive translation members.");
            }
        }

        if (expected is not null && (fields.Count != expected.Length || expected.Any(field => !fields.ContainsKey(field))))
        {
            throw Invalid("Missing or unknown translation members.");
        }

        return fields;
    }

    /// <summary>Preserves placeholder identity, multiplicity, alignment and format rather than accepting a highest-index match.</summary>
    private static string Signature(string value) => string.Join("|", PlaceholderPattern().Matches(value)
        .Select(match => match.Value).Order(StringComparer.Ordinal));

    /// <summary>Creates a stable translation validation failure.</summary>
    internal static TranslationPackException Invalid(string message) => new(TranslationErrorCode.InvalidPack, message);

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();

    [GeneratedRegex(@"(?<!\{)\{[0-9]+(?:,[+-]?[0-9]+)?(?::[^{}]*)?\}(?!\})", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();
}
