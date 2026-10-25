using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MovieSift.Application.QueueLists;

namespace MovieSift.Infrastructure.Persistence;

/// <summary>Encodes a bounded listing schema that cannot contain trusted inspections, selections or jobs.</summary>
internal static class QueueListJson
{
    internal const int CurrentVersion = 1;
    internal const int MaximumBytes = 65_536;
    internal const int MaximumDepth = 16;
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
    };

    /// <summary>Rejects incomplete, duplicate, unknown and invalid Unicode fields before returning any entries.</summary>
    internal static QueueListResult Read(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            _ = StrictUtf8.GetCharCount(bytes.Span);
        }
        catch (DecoderFallbackException exception)
        {
            throw new JsonException("The queue list must contain valid UTF-8.", exception);
        }

        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = MaximumDepth });
        RejectDuplicateProperties(document.RootElement);
        var root = document.RootElement;
        var validVersion = root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number &&
            version.TryGetInt32(out _);

        if (!validVersion)
        {
            throw new JsonException("The queue list must contain an integer version.");
        }

        if (root.GetProperty("version").GetInt32() != CurrentVersion)
        {
            return new(null, null, new(QueueListErrorCode.UnsupportedVersion, "The queue-list version is not supported."));
        }

        var stored = JsonSerializer.Deserialize<QueueListDocument>(bytes.Span, Options)
            ?? throw new JsonException("The queue list cannot be null.");

        if (stored.Revision == Guid.Empty || stored.Entries.IsDefault)
        {
            throw new JsonException("The queue list requires a nonempty revision and entries array.");
        }

        var list = new QueueList(stored.Entries.Select(ReadEntry).ToImmutableArray());
        var failure = QueueListPolicy.Validate(list);
        return failure is null ? new(list, stored.Revision) : new(null, null, failure);
    }

    /// <summary>Measures the actual persisted UTF-8 bytes before any temporary file is created.</summary>
    internal static byte[] Write(QueueList list, Guid revision)
    {
        var failure = QueueListPolicy.Validate(list);

        if (failure is not null)
        {
            throw new QueueListStorageException(failure);
        }

        if (revision == Guid.Empty)
        {
            throw new QueueListStorageException(new(QueueListErrorCode.InvalidRecord, "The queue revision cannot be empty."));
        }

        var document = new QueueListDocument(CurrentVersion, revision, list.Entries.Select(entry => new QueueListRecord(
            entry.SourcePath, entry.DestinationPath, entry.Included, entry.FolderOrigin, entry.OutputDirectoryRoot)).ToImmutableArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Options);

        if (bytes.Length > MaximumBytes)
        {
            throw new QueueListStorageException(new(QueueListErrorCode.LimitExceeded,
                "The serialized queue list exceeds 65536 UTF-8 bytes."));
        }

        return bytes;
    }

    /// <summary>Rejects an explicit null array member instead of loading a partial listing.</summary>
    private static QueueListEntry ReadEntry(QueueListRecord stored)
    {
        if (stored is null)
        {
            throw new JsonException("A queue entry cannot be null.");
        }

        return new(stored.SourcePath, stored.DestinationPath, stored.Included, stored.FolderOrigin, stored.OutputDirectoryRoot);
    }

    /// <summary>Rejects duplicate object properties at every level and malformed escaped Unicode names.</summary>
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
            if (!names.Add(ReadPropertyName(property)))
            {
                throw new JsonException("The queue list contains a duplicate JSON property.");
            }

            RejectDuplicateProperties(property.Value);
        }
    }

    /// <summary>Translates malformed property-name escapes at the JSON decoding boundary.</summary>
    private static string ReadPropertyName(JsonProperty property)
    {
        try
        {
            return property.Name;
        }
        catch (InvalidOperationException exception)
        {
            throw new JsonException("A queue property name contains invalid escaped Unicode.", exception);
        }
    }
}
