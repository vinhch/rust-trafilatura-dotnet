using System.Text;
using System.Text.Json;
using Xunit;

namespace RustTrafilatura.Tests;

/// <summary>
/// The request side and the response's metadata side: every option key, the header's own top-level
/// keys, and what the native library returns when only some response fields were asked for.
/// </summary>
public sealed class OptionsTests
{
    [Fact]
    public void Default_Response_Omits_Html()
    {
        var result = Extractor.ExtractFromBytes(Fixtures.Article);

        Assert.Null(result.ContentHtml);
        Assert.Null(result.CommentsHtml);
        Assert.NotNull(result.ContentText);
    }

    /// <summary>
    /// An unset option takes the core's own default rather than a value this layer invented, so the
    /// extraction that leaves the options object out and the one that sends an empty one agree. The
    /// header is asserted too, because "produce identical JSON" is a claim about the request.
    /// </summary>
    [Fact]
    public void DefaultOptions_Equal_CoreDefaults()
    {
        var omitted = Extractor.ExtractFromBytes(Fixtures.Article);
        var explicitDefaults = Extractor.ExtractFromBytes(Fixtures.Article, new ExtractionOptions());

        Assert.Equal(omitted.ContentText, explicitDefaults.ContentText);
        Assert.Equal(omitted.Metadata!.Title, explicitDefaults.Metadata!.Title);

        // An empty options object is written as `{}` rather than as null or as nothing at all.
        var defaults = Extractor.BuildHeaderJson(new ExtractionOptions());
        Assert.Empty(defaults.GetProperty("options").EnumerateObject());
        Assert.False(defaults.TryGetProperty("responseFields", out _));

        // And no options at all writes no options key, so the only default is the native side's.
        var withoutOptions = Extractor.BuildHeaderJson(null);
        Assert.False(withoutOptions.TryGetProperty("options", out _));
        Assert.Equal(defaults.GetProperty("schemaVersion").GetInt32(), withoutOptions.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void Metadata_Date_IsNull_WhenAbsent()
    {
        var result = Extractor.ExtractFromBytes(Fixtures.NoDate, new ExtractionOptions { ResponseFields = ["metadata"] });

        Assert.Null(result.Metadata!.Date);
    }

    /// <summary>
    /// The response carries `categories` and `tags` as arrays whether or not the page has any, so the
    /// empty case is the member's value and not an absence. It has to be the shared empty array: a
    /// list built for it would be one allocation per call for a member the caller reads as empty.
    /// </summary>
    [Fact]
    public void Empty_Categories_And_Tags_Are_The_Shared_Empty_Array()
    {
        var result = Extractor.ExtractFromBytes(Fixtures.Article, new ExtractionOptions { ResponseFields = ["metadata"] });

        Assert.Same(Array.Empty<string>(), result.Metadata!.Categories);
        Assert.Same(Array.Empty<string>(), result.Metadata.Tags);
    }

    [Fact]
    public void Requesting_Html_Returns_It()
    {
        var result = Extractor.ExtractFromBytes(
            Fixtures.Article,
            new ExtractionOptions { ResponseFields = ["contentHtml", "commentsHtml"] });

        Assert.Contains("<h1", result.ContentHtml);
        Assert.Equal(string.Empty, result.CommentsHtml);

        // The HTML fields are opt-in, so asking for them is what turns the text off.
        Assert.Null(result.ContentText);
        Assert.Null(result.Metadata);
    }

    [Fact]
    public void Extract_FromHtml_Equals_ExtractFromBytes()
    {
        var fromBytes = Extractor.ExtractFromBytes(Fixtures.ArticleWithSoftHyphens);
        var fromHtml = Extractor.ExtractFromHtml(Fixtures.ArticleWithSoftHyphensHtml);

        Assert.Equal(fromBytes.ContentText, fromHtml.ContentText);
        Assert.Equal(fromBytes.Metadata!.Title, fromHtml.Metadata!.Title);
    }

    [Fact]
    public void Metadata_Fields_Are_Populated()
    {
        var result = Extractor.ExtractFromBytes(Fixtures.Article, new ExtractionOptions { ResponseFields = ["metadata"] });
        var metadata = result.Metadata!;

        Assert.Equal("Research results", metadata.Title);
        Assert.Equal("Ada Lovelace", metadata.Author);
        Assert.Equal("The research team reports its findings.", metadata.Description);
        Assert.Equal("Example Journal", metadata.Sitename);
        Assert.Equal("en", metadata.Language);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero), metadata.Date);
        Assert.Empty(metadata.Categories);
        Assert.Empty(metadata.Tags);
    }

    [Fact]
    public void NonUtf8_Bytes_AreDetected()
    {
        var result = Extractor.ExtractFromBytes(Fixtures.Windows1252);

        Assert.Contains("Caf\u00E9 review", result.ContentText);
        Assert.Contains("Z\u00FCrich", result.ContentText);
    }

    /// <summary>
    /// `inputEncoding` is a header key that reaches the decoder, so a label contradicting detection
    /// changes the text. These are the article's UTF-8 bytes read as windows-1252: U+00AD and U+0308
    /// are one byte each there and two bytes as UTF-8, so the core neither composes the NFD umlaut nor
    /// drops the soft hyphen, and the result says so. Detection would have picked UTF-8 and produced
    /// the other result, which is why the two have to differ before either is read.
    /// </summary>
    [Fact]
    public void Input_Encoding_Names_The_Decoder()
    {
        var detected = Extractor.ExtractFromBytes(Fixtures.ArticleWithSoftHyphens);
        var declared = Extractor.ExtractFromBytes(
            Fixtures.ArticleWithSoftHyphens,
            new ExtractionOptions { InputEncoding = "windows-1252" });

        Assert.NotEqual(detected.ContentText, declared.ContentText);
        Assert.Contains("research", detected.ContentText);
        Assert.DoesNotContain("research", declared.ContentText);
        Assert.Contains("re\u00C2", declared.ContentText);
    }

    /// <summary>`url` is a header key that reaches the metadata, and the core validates it.</summary>
    [Fact]
    public void Url_Reaches_The_Metadata()
    {
        var result = Extractor.ExtractFromBytes(
            Fixtures.Article,
            new ExtractionOptions
            {
                Url = "https://example.org/article",
                ResponseFields = ["metadata"],
            });

        Assert.Equal("https://example.org/article", result.Metadata!.Url);
    }

    [Fact]
    public void Gzip_Bytes_Are_Decoded()
    {
        var result = Extractor.ExtractFromBytes(Fixtures.Gzip);

        Assert.Equal(Extractor.ExtractFromBytes(Fixtures.Article).ContentText, result.ContentText);
    }

    /// <summary>
    /// The payload is a page, not text: a result that still carried markup would contain these, so
    /// the extraction asserts on text that only a parsed document can hold.
    /// </summary>
    [Fact]
    public void Extracted_Text_Is_Text_Not_Markup()
    {
        var result = Extractor.ExtractFromBytes(Fixtures.Article);

        Assert.DoesNotContain("<article", result.ContentText);
        Assert.DoesNotContain("<p>", result.ContentText);
        Assert.DoesNotContain("</", result.ContentText);
    }

    /// <summary>
    /// Pins the request header's shape: camelCase keys, `responseFields` and `url` and
    /// `inputEncoding` at the top level, every option inside `options`, and no key at all for a
    /// member the caller left unset.
    /// </summary>
    [Fact]
    public void Header_Json_Has_The_Wire_Shape()
    {
        var header = Extractor.BuildHeaderJson(new ExtractionOptions
        {
            ResponseFields = ["contentText"],
            Url = "https://example.org/article",
            InputEncoding = "windows-1252",
            TargetLanguage = "en",
            Focus = ExtractionFocus.FavorRecall,
            HtmlDateMode = HtmlDateMode.Fast,
            BlacklistedAuthors = ["Ada Lovelace"],
            MaxTreeSize = 4000,
            Config = new ExtractionConfig { MinOutputSize = 2 },
        });

        Assert.Equal(1, header.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("https://example.org/article", header.GetProperty("url").GetString());
        Assert.Equal("windows-1252", header.GetProperty("inputEncoding").GetString());
        Assert.Equal(["contentText"], header.GetProperty("responseFields").EnumerateArray().Select(field => field.GetString()));

        var options = header.GetProperty("options");
        Assert.Equal("en", options.GetProperty("targetLanguage").GetString());
        Assert.Equal("favorRecall", options.GetProperty("focus").GetString());
        Assert.Equal("fast", options.GetProperty("htmlDateMode").GetString());
        Assert.Equal("Ada Lovelace", options.GetProperty("blacklistedAuthors")[0].GetString());
        Assert.Equal(4000, options.GetProperty("maxTreeSize").GetInt64());
        Assert.Equal(2, options.GetProperty("config").GetProperty("minOutputSize").GetInt64());

        // The three header keys are header keys: a copy inside `options` is an unknown key, which the
        // native side rejects outright instead of ignoring.
        Assert.False(options.TryGetProperty("url", out _));
        Assert.False(options.TryGetProperty("inputEncoding", out _));
        Assert.False(options.TryGetProperty("responseFields", out _));

        // An unset member is absent, not null and not a value this layer invented.
        Assert.False(options.TryGetProperty("pruneSelector", out _));
    }

    /// <summary>
    /// Pins the header's bytes for one fixed option set. The literal was recorded from the
    /// reflection-based serializer before the source-generated one replaced it, so it measures the
    /// contract as it was rather than restating the new implementation; the test runs on both assets,
    /// where the enum spellings come from different code. Both enums are spelled away from their
    /// defaults and a header key, a string, a bool, a number and a nested object are present, so a
    /// reordered key, a quoting change or an enum spelling cannot pass unnoticed.
    /// </summary>
    [Fact]
    public void Header_Json_Is_Identical_Across_TargetFrameworks()
    {
        const string Expected =
            """{"schemaVersion":1,"url":"https://example.test/page","inputEncoding":"utf-8","responseFields":["contentText"],"options":{"targetLanguage":"en","focus":"favorPrecision","excludeComments":true,"maxTreeSize":4000,"htmlDateMode":"extensive","config":{"minOutputSize":2}}}""";

        var header = Extractor.BuildHeader(new ExtractionOptions
        {
            ResponseFields = ["contentText"],
            Url = "https://example.test/page",
            InputEncoding = "utf-8",
            TargetLanguage = "en",
            Focus = ExtractionFocus.FavorPrecision,
            HtmlDateMode = HtmlDateMode.Extensive,
            ExcludeComments = true,
            MaxTreeSize = 4000,
            Config = new ExtractionConfig { MinOutputSize = 2 },
        });

        Assert.Equal(Encoding.UTF8.GetBytes(Expected), header);
    }

    /// <summary>
    /// A value outside the declared members has no spelling the core accepts, so it has to cross as the
    /// number the caller cast: the core then rejects that request, while a substituted name would be a
    /// different request it answers. Both assets must produce those same bytes, which is why the
    /// expectation is one literal asserted on each of them.
    /// </summary>
    [Fact]
    public void Header_Json_Is_Identical_Across_TargetFrameworks_For_Unknown_Enum_Values()
    {
        const string Expected =
            """{"schemaVersion":1,"options":{"focus":99,"htmlDateMode":42}}""";

        var header = Extractor.BuildHeader(new ExtractionOptions
        {
            Focus = (ExtractionFocus)99,
            HtmlDateMode = (HtmlDateMode)42,
        });

        Assert.Equal(Encoding.UTF8.GetBytes(Expected), header);
    }

    /// <summary>
    /// The enums travel by name, and these are the names: the native side rejects any other
    /// spelling, so a value it accepts is the only value worth emitting. Every member is asserted
    /// because each asset spells the two enums in its own code (`[JsonStringEnumMemberName]` from
    /// `net9.0` on, the converters in HeaderContext.cs before that) and the two tables can drift.
    /// </summary>
    [Fact]
    public void Enum_Options_Use_The_Core_Spellings()
    {
        Assert.Equal("balanced", FocusWire(ExtractionFocus.Balanced));
        Assert.Equal("favorRecall", FocusWire(ExtractionFocus.FavorRecall));
        Assert.Equal("favorPrecision", FocusWire(ExtractionFocus.FavorPrecision));

        Assert.Equal("default", DateModeWire(HtmlDateMode.Default));
        Assert.Equal("fast", DateModeWire(HtmlDateMode.Fast));
        Assert.Equal("extensive", DateModeWire(HtmlDateMode.Extensive));
        Assert.Equal("disabled", DateModeWire(HtmlDateMode.Disabled));

        static string FocusWire(ExtractionFocus focus) =>
            Extractor.BuildHeaderJson(new ExtractionOptions { Focus = focus })
                .GetProperty("options").GetProperty("focus").GetString()!;

        static string DateModeWire(HtmlDateMode mode) =>
            Extractor.BuildHeaderJson(new ExtractionOptions { HtmlDateMode = mode })
                .GetProperty("options").GetProperty("htmlDateMode").GetString()!;
    }

    /// <summary>
    /// Putting a header key inside `options` is an unknown key there, which the native side rejects
    /// outright instead of ignoring. Reaching that needs a header no caller of the public surface can
    /// build, so the test makes one through the ABI: the managed side's own header has to keep the
    /// three keys at the top level.
    /// </summary>
    [Fact]
    public void Header_Keys_Are_Unknown_Inside_Options()
    {
        var (code, response) = Interop.Extract(
            Encoding.UTF8.GetBytes("""{"schemaVersion":1,"options":{"url":"https://example.org/article"}}"""),
            Fixtures.Article,
            RawResponse.Read);

        Assert.Equal(2, code);
        using var document = JsonDocument.Parse(response);
        Assert.Equal("InvalidRequest", document.RootElement.GetProperty("error").GetProperty("kind").GetString());
    }
}
