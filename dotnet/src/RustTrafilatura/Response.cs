using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RustTrafilatura;

/// <summary>
/// Reads what the native library returned: the extraction result, its metadata, or the failure a
/// non-zero code carries.
/// </summary>
internal static class Response
{
    /// <summary>
    /// Reads one response out of the bytes the native library returned. A non-zero code is a failure,
    /// whose envelope is small enough to read as text; a zero code is the result, read straight from
    /// the native buffer so the envelope never becomes a full-size managed string of its own.
    /// </summary>
    /// <remarks>
    /// Internal so the branches no response produces - a root that is not an object, a document that
    /// is truncated or carries trailing content, a member whose value is not the kind the contract
    /// names - are assertable without a native library that misbehaves.
    /// </remarks>
    internal static ExtractionResult Read(int code, ReadOnlySpan<byte> response)
    {
        if (code != 0)
        {
            throw Failures.Read(code, Encoding.UTF8.GetString(response));
        }

        string? contentText = null;
        string? commentsText = null;
        string? contentHtml = null;
        string? commentsHtml = null;
        Metadata? metadata = null;

        var reader = new Utf8JsonReader(response);

        // Input with no JSON token at all throws here rather than returning false, which is what
        // `JsonDocument.Parse` did with it too.
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("The native library returned a response that is not a JSON object.");
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            // No other token can appear at this depth in a document that parses, and skipping one
            // whole keeps a malformed member from desynchronizing the loop.
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                reader.Skip();
                continue;
            }

            // Each branch reads its own value, because a name can only be compared while the reader is
            // still on it: read first, and ValueTextEquals matches the value's bytes against member names.
            if (reader.ValueTextEquals("contentText"u8))
            {
                reader.Read();
                contentText = ReadStringValue(ref reader);
            }
            else if (reader.ValueTextEquals("commentsText"u8))
            {
                reader.Read();
                commentsText = ReadStringValue(ref reader);
            }
            else if (reader.ValueTextEquals("contentHtml"u8))
            {
                reader.Read();
                contentHtml = ReadStringValue(ref reader);
            }
            else if (reader.ValueTextEquals("commentsHtml"u8))
            {
                reader.Read();
                commentsHtml = ReadStringValue(ref reader);
            }
            else if (reader.ValueTextEquals("metadata"u8))
            {
                reader.Read();
                metadata = ReadMetadata(ref reader);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        // Reading past the root value is what rejects anything after it, which `JsonDocument.Parse`
        // rejected as well. Empty and whitespace-only input never reaches this line: the read at the
        // root threw on it.
        reader.Read();

        return new ExtractionResult
        {
            ContentText = contentText,
            CommentsText = commentsText,
            ContentHtml = contentHtml,
            CommentsHtml = commentsHtml,
            Metadata = metadata,
        };
    }

    /// <summary>
    /// Maps the `metadata` object, or null when it was not requested: the wire contract keeps
    /// "not asked for" (null) apart from "nothing found" (empty strings and empty arrays).
    /// </summary>
    private static Metadata? ReadMetadata(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }

        var title = string.Empty;
        var author = string.Empty;
        var url = string.Empty;
        var hostname = string.Empty;
        var description = string.Empty;
        var sitename = string.Empty;
        DateTimeOffset? date = null;
        IReadOnlyList<string> categories = Array.Empty<string>();
        IReadOnlyList<string> tags = Array.Empty<string>();
        var id = string.Empty;
        var fingerprint = string.Empty;
        var license = string.Empty;
        var language = string.Empty;
        var image = string.Empty;
        var pageType = string.Empty;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                reader.Skip();
                continue;
            }

            // The value is read inside each branch, as in Read.
            if (reader.ValueTextEquals("title"u8))
            {
                reader.Read();
                title = ReadStringValue(ref reader) ?? string.Empty;
            }
            else if (reader.ValueTextEquals("author"u8))
            {
                reader.Read();
                author = ReadStringValue(ref reader) ?? string.Empty;
            }
            else if (reader.ValueTextEquals("url"u8))
            {
                reader.Read();
                url = ReadStringValue(ref reader) ?? string.Empty;
            }
            else if (reader.ValueTextEquals("hostname"u8))
            {
                reader.Read();
                hostname = ReadStringValue(ref reader) ?? string.Empty;
            }
            else if (reader.ValueTextEquals("description"u8))
            {
                reader.Read();
                description = ReadStringValue(ref reader) ?? string.Empty;
            }
            else if (reader.ValueTextEquals("sitename"u8))
            {
                reader.Read();
                sitename = ReadStringValue(ref reader) ?? string.Empty;
            }
            else if (reader.ValueTextEquals("date"u8))
            {
                reader.Read();
                date = ReadDate(ref reader);
            }
            else if (reader.ValueTextEquals("categories"u8))
            {
                reader.Read();
                categories = ReadStringArray(ref reader);
            }
            else if (reader.ValueTextEquals("tags"u8))
            {
                reader.Read();
                tags = ReadStringArray(ref reader);
            }
            else if (reader.ValueTextEquals("id"u8))
            {
                reader.Read();
                id = ReadStringValue(ref reader) ?? string.Empty;
            }
            else if (reader.ValueTextEquals("fingerprint"u8))
            {
                reader.Read();
                fingerprint = ReadStringValue(ref reader) ?? string.Empty;
            }
            else if (reader.ValueTextEquals("license"u8))
            {
                reader.Read();
                license = ReadStringValue(ref reader) ?? string.Empty;
            }
            else if (reader.ValueTextEquals("language"u8))
            {
                reader.Read();
                language = ReadStringValue(ref reader) ?? string.Empty;
            }
            else if (reader.ValueTextEquals("image"u8))
            {
                reader.Read();
                image = ReadStringValue(ref reader) ?? string.Empty;
            }
            else if (reader.ValueTextEquals("pageType"u8))
            {
                reader.Read();
                pageType = ReadStringValue(ref reader) ?? string.Empty;
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        return new Metadata
        {
            Title = title,
            Author = author,
            Url = url,
            Hostname = hostname,
            Description = description,
            Sitename = sitename,
            Date = date,
            Categories = categories,
            Tags = tags,
            Id = id,
            Fingerprint = fingerprint,
            License = license,
            Language = language,
            Image = image,
            PageType = pageType,
        };
    }

    /// <summary>
    /// A string member, or null when the value is not a string. A field that was not requested is
    /// JSON null and a requested field that found nothing is an empty string, so the JSON kind is
    /// the whole difference between them.
    /// </summary>
    /// <remarks>
    /// A value of another kind is skipped whole rather than left where it is: the reader has to return
    /// to the level its caller was reading, or a nested member would be seen as one of theirs.
    /// </remarks>
    private static string? ReadStringValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString();
        }

        reader.Skip();
        return null;
    }

    /// <summary>
    /// Null is "the page carries no date"; any other value is an RFC 3339 timestamp, parsed with
    /// round-trip style so the offset the library reported survives.
    /// </summary>
    private static DateTimeOffset? ReadDate(ref Utf8JsonReader reader) =>
        ReadStringValue(ref reader) is { } date
            ? DateTimeOffset.Parse(date, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null;

    /// <summary>
    /// The strings an array member holds, or the shared empty array when the member is absent, is not
    /// an array, or holds no string. Both empty results are the same instance, so "the page has no
    /// tags" costs no list.
    /// </summary>
    private static IReadOnlyList<string> ReadStringArray(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return Array.Empty<string>();
        }

        List<string>? items = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                // The first string is what allocates; an array that holds none never reaches this.
                (items ??= []).Add(reader.GetString()!);
                continue;
            }

            reader.Skip();
        }

        return items is null ? Array.Empty<string>() : items;
    }
}
