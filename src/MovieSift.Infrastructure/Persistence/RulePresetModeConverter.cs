using System.Text.Json;
using System.Text.Json.Serialization;
using MovieSift.Core.Rules;

namespace MovieSift.Infrastructure.Persistence;

/// <summary>Restricts stored modes to the exact versioned string values rather than numeric or case-folded enums.</summary>
internal sealed class RulePresetModeConverter : JsonConverter<LanguageRuleMode>
{
    /// <summary>Rejects undefined, numeric and incorrectly cased mode values.</summary>
    public override LanguageRuleMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? reader.GetString() switch
        {
            "Unchanged" => LanguageRuleMode.Unchanged,
            "KeepMatching" => LanguageRuleMode.KeepMatching,
            _ => throw new JsonException("The preset rule mode is not supported."),
        } : throw new JsonException("The preset rule mode must be a string.");

    /// <summary>Writes only the two supported immutable configuration modes.</summary>
    public override void Write(Utf8JsonWriter writer, LanguageRuleMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            LanguageRuleMode.Unchanged => "Unchanged",
            LanguageRuleMode.KeepMatching => "KeepMatching",
            _ => throw new JsonException("The preset rule mode is not supported."),
        });
}
