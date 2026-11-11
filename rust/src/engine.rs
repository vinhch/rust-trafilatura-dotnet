//! Core invocation and response assembly.
//!
//! [`build`] is the only path from a validated [`Request`] to a [`Response`]: it maps the wire
//! options onto the core's `Options`/`Config`, runs the extraction and fills in exactly the fields
//! the request named. A core error is not returned as `Err`; it lands in [`Response::error`], so
//! success and failure keep one envelope shape.

use crate::contract::{self, ConfigJson, Request, ResponseField, SCHEMA_VERSION};
use serde::Serialize;
use trafilatura_rust::{Call, Config, Document, ExtractError, Focus, Input, Options};

/// The response `metadata` object: the metadata fields of the core [`Document`] under their wire
/// names. A field the page does not carry is an empty string, except `date`.
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MetadataJson {
    pub title: String,
    pub author: String,
    pub url: String,
    pub hostname: String,
    pub description: String,
    pub sitename: String,
    /// `None` when the page carries no date.
    pub date: Option<String>,
    pub categories: Vec<String>,
    pub tags: Vec<String>,
    pub license: String,
    pub language: String,
    pub image: String,
    pub page_type: String,
}

/// The response `error` object.
///
/// `context` is present only for the kinds that carry a payload, so an omitted key and an empty
/// object stay distinguishable.
#[derive(Debug, Serialize)]
pub struct ErrorEnvelope {
    pub kind: String,
    pub message: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub context: Option<serde_json::Value>,
}

/// The response envelope, schema version 2.
///
/// Every key is always serialized. A content field the request did not name stays `None` and
/// becomes JSON `null`, which is what separates "not asked for" from a requested field that found
/// nothing (an empty string). `error` is `null` on success and populated on failure, and on
/// failure the requested content fields are empty strings while `metadata` stays `null`.
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Response {
    pub schema_version: u32,
    pub content_text: Option<String>,
    pub comments_text: Option<String>,
    pub metadata: Option<MetadataJson>,
    pub error: Option<ErrorEnvelope>,
}

/// The page as the request delivers it. The core runs its decompression, charset detection and binary
/// check on `Bytes` only, so `Text` is parsed as it is.
pub enum Payload<'a> {
    Bytes(&'a [u8]),
    Text(&'a str),
}

/// Extracts from a payload and assembles the response. Infallible: core errors are mapped onto
/// [`Response::error`].
pub fn build(request: &Request, payload: Payload<'_>) -> Response {
    let extractor = options_from(request).build();
    let mut input = match payload {
        Payload::Bytes(bytes) => Input::bytes(bytes),
        Payload::Text(text) => Input::text(text),
    };
    if let Some(url) = request.header.url.as_deref().filter(|url| !url.is_empty()) {
        input = input.url(url);
    }
    assemble(request, extractor.bare_extraction(&input, &Call::default()))
}

/// Fills in the requested fields of a response from the core's document, or the error envelope.
fn assemble(request: &Request, extracted: Result<Document, ExtractError>) -> Response {
    let fields = &request.fields;
    let mut response = Response {
        schema_version: SCHEMA_VERSION,
        content_text: None,
        comments_text: None,
        metadata: None,
        error: None,
    };
    match extracted {
        Ok(mut document) => {
            if fields.contains(&ResponseField::ContentText) {
                response.content_text = Some(document.text.take().unwrap_or_default());
            }
            if fields.contains(&ResponseField::CommentsText) {
                response.comments_text = Some(document.comments.take().unwrap_or_default());
            }
            if fields.contains(&ResponseField::Metadata) {
                response.metadata = Some(metadata_json(document));
            }
        }
        Err(error) => {
            for field in fields {
                match field {
                    ResponseField::ContentText => response.content_text = Some(String::new()),
                    ResponseField::CommentsText => response.comments_text = Some(String::new()),
                    ResponseField::Metadata => {}
                }
            }
            let (kind, context) = contract::error_kind(&error);
            response.error = Some(ErrorEnvelope {
                kind: kind.to_owned(),
                message: error.to_string(),
                context,
            });
        }
    }
    response
}

/// Maps the request onto the core options. An absent key keeps the core's own default, so an empty
/// `options` object gives the same options as no `options` key. `with_metadata` follows the
/// requested `metadata` field, not the options: a blacklist or an essential-metadata check still
/// extracts metadata, and the response then leaves it out.
fn options_from(request: &Request) -> Options {
    let mut options =
        Options::new().with_metadata(request.fields.contains(&ResponseField::Metadata));
    let Some(json) = &request.header.options else {
        return options;
    };
    let mut config = Config::default();
    if let Some(sizes) = &json.config {
        config_sizes_from(sizes, &mut config);
    }
    if let Some(value) = json.max_tree_size {
        config.max_tree_size = usize::try_from(value).ok().filter(|size| *size > 0);
    }
    if let Some(value) = json.html_date_mode {
        config.extensive_date_search = extensive_date_search_from(value);
    }
    options = options.config(config);
    if let Some(value) = &json.target_language {
        options = options.target_language(value);
    }
    if let Some(value) = json.focus {
        options = options.focus(focus_from(value));
    }
    if let Some(value) = json.exclude_comments {
        options = options.comments(!value);
    }
    if let Some(value) = json.exclude_tables {
        options = options.tables(!value);
    }
    if let Some(value) = json.include_images {
        options = options.images(value);
    }
    if let Some(value) = json.include_links {
        options = options.links(value);
    }
    if let Some(value) = &json.blacklisted_authors {
        options = options.author_blacklist(value);
    }
    if let Some(value) = json.has_essential_metadata {
        options = options.only_with_metadata(value);
    }
    options
}

/// Copies the `options.config` sizes onto the core [`Config`], each omitted key keeping its value.
/// A negative size is no size, so it reads as zero.
fn config_sizes_from(json: &ConfigJson, config: &mut Config) {
    config.min_extracted_size = json
        .min_extracted_size
        .map_or(config.min_extracted_size, size_from);
    config.min_output_size = json
        .min_output_size
        .map_or(config.min_output_size, size_from);
    config.min_output_comm_size = json
        .min_output_comment_size
        .map_or(config.min_output_comm_size, size_from);
}

/// A wire size as the core's `usize`; a negative value reads as zero.
fn size_from(value: i64) -> usize {
    usize::try_from(value).unwrap_or(0)
}

/// The wire `focus` enum is not the core enum, so no `From` shortcut applies.
fn focus_from(focus: contract::FocusJson) -> Focus {
    match focus {
        contract::FocusJson::Balanced => Focus::Balanced,
        contract::FocusJson::FavorRecall => Focus::Recall,
        contract::FocusJson::FavorPrecision => Focus::Precision,
    }
}

/// The core has one switch for the date search, so `htmlDateMode` maps onto it: `fast` turns the
/// extensive search off, `extensive` turns it on, and `default` keeps the core's own default.
fn extensive_date_search_from(mode: contract::HtmlDateModeJson) -> bool {
    match mode {
        contract::HtmlDateModeJson::Default => Config::default().extensive_date_search,
        contract::HtmlDateModeJson::Fast => false,
        contract::HtmlDateModeJson::Extensive => true,
    }
}

/// Moves the core metadata into its wire shape. Absent fields become empty strings, the date stays
/// absent, and the core's `pagetype` is the wire `pageType`.
fn metadata_json(document: Document) -> MetadataJson {
    MetadataJson {
        title: document.title.unwrap_or_default(),
        author: document.author.unwrap_or_default(),
        url: document.url.unwrap_or_default(),
        hostname: document.hostname.unwrap_or_default(),
        description: document.description.unwrap_or_default(),
        sitename: document.sitename.unwrap_or_default(),
        date: document.date,
        categories: document.categories.unwrap_or_default(),
        tags: document.tags.unwrap_or_default(),
        license: document.license.unwrap_or_default(),
        language: document.language.unwrap_or_default(),
        image: document.image.unwrap_or_default(),
        page_type: document.pagetype.unwrap_or_default(),
    }
}

/// The inline documents the engine tests extract. Nothing here reaches the network or the
/// filesystem, and all eleven extract with the core defaults, so the byte-path comparison has a
/// real result to compare against.
#[cfg(test)]
const FIXTURES: [&str; 11] = [
    r#"<html lang="en"><head><title>Plain article</title></head><body><article><h1>Plain article</h1><p>The council approved the measure after a lengthy debate, citing the need for clearer rules on how public data is published. Officials said the new framework will take effect next spring and that compliance will be reviewed annually, with a public report published each year.</p><p>A second paragraph adds enough body text for the extractor to keep the article instead of discarding it as too short.</p></article></body></html>"#,
    r#"<html lang="en"><head><title>Filed article</title><meta name="author" content="Ada Lovelace"><meta name="description" content="A filed article."><meta property="og:site_name" content="Example Journal"><meta property="article:published_time" content="2026-09-29T00:00:00Z"></head><body><article><h1>Filed article</h1><time datetime="2026-09-29">29 September 2026</time><p>The council approved the measure after a lengthy debate, citing the need for clearer rules on how public data is published. Officials said the new framework will take effect next spring and that compliance will be reviewed annually, with a public report published each year.</p><p>A second paragraph adds enough body text for the extractor to keep the article instead of discarding it as too short.</p></article></body></html>"#,
    r#"<html lang="en"><head><title>Article between navigation</title></head><body><nav><a href="/">Home</a><a href="/news">News</a></nav><main><article><h1>Article between navigation</h1><p>The council approved the measure after a lengthy debate, citing the need for clearer rules on how public data is published. Officials said the new framework will take effect next spring and that compliance will be reviewed annually, with a public report published each year.</p><p>A second paragraph adds enough body text for the extractor to keep the article instead of discarding it as too short.</p></article></main><aside><p>Related stories and a newsletter sign-up form.</p></aside><footer><p>Copyright 2026 Example Journal. All rights reserved.</p></footer></body></html>"#,
    r#"<html lang="en"><head><title>Article with comments</title></head><body><article><h1>Article with comments</h1><p>The council approved the measure after a lengthy debate, citing the need for clearer rules on how public data is published. Officials said the new framework will take effect next spring and that compliance will be reviewed annually, with a public report published each year.</p><p>A second paragraph adds enough body text for the extractor to keep the article instead of discarding it as too short.</p></article><div id="comments"><div class="comment"><p>I read the proposal twice and the reporting requirement is the part that worries me most, because it lands on publishers who have the least staff to spare.</p></div><div class="comment"><p>The annual review is a sensible compromise, and I would rather see the framework start next spring than wait for a perfect version that never arrives.</p></div></div></body></html>"#,
    r#"<html lang="en"><head><title>Article with a table</title></head><body><article><h1>Article with a table</h1><p>The council approved the measure after a lengthy debate, citing the need for clearer rules on how public data is published. Officials said the new framework will take effect next spring and that compliance will be reviewed annually, with a public report published each year.</p><table><tr><th>Year</th><th>Reports</th></tr><tr><td>2026</td><td>12</td></tr><tr><td>2027</td><td>14</td></tr></table><p>A second paragraph adds enough body text for the extractor to keep the article instead of discarding it as too short.</p></article></body></html>"#,
    r#"<html lang="en"><head><title>Article with media</title></head><body><article><h1>Article with media</h1><figure><img src="/photo.jpg" alt="A council chamber"><figcaption>The council chamber.</figcaption></figure><p>The council approved the measure after a lengthy debate, citing the need for <a href="https://example.org/rules">clearer rules</a> on how public data is published. Officials said the new framework will take effect next spring and that compliance will be reviewed annually, with a public report published each year.</p><p>A second paragraph adds enough body text for the extractor to keep the article instead of discarding it as too short.</p></article></body></html>"#,
    r#"<html lang="en"><head><title>Article with scripting</title><style>body { color: #333; }</style><script>window.tracker = function () { return 1; };</script></head><body><article><h1>Article with scripting</h1><p>The council approved the measure after a lengthy debate, citing the need for clearer rules on how public data is published. Officials said the new framework will take effect next spring and that compliance will be reviewed annually, with a public report published each year.</p><p>A second paragraph adds enough body text for the extractor to keep the article instead of discarding it as too short.</p></article><script>window.tracker();</script><noscript>Enable JavaScript to read the comments.</noscript></body></html>"#,
    r#"<html lang="en"><head><title>Article with entities</title></head><body><article><h1>Article with entities</h1><p>The council approved the measure after a lengthy debate, citing the need for clearer rules on how public data is published &amp; stored. Officials said the new framework&#8217;s effect starts next spring and that compliance will be reviewed annually, with a public report published each year.</p><p>A second paragraph&nbsp;adds enough body text for the extractor to keep the article instead of discarding it as too short &lt;usually&gt;.</p></article></body></html>"#,
    r#"<html lang="en"><head><title>Article in sections</title></head><body><main><section><h2>Background</h2><p>The council approved the measure after a lengthy debate, citing the need for clearer rules on how public data is published. Officials said the new framework will take effect next spring and that compliance will be reviewed annually, with a public report published each year.</p></section><section><h2>Response</h2><p>A second paragraph adds enough body text for the extractor to keep the article instead of discarding it as too short.</p></section></main></body></html>"#,
    r#"<html lang="en"><head><title>Article accentué</title></head><body><article><h1>Article accentué</h1><p>Le conseil a approuvé la mesure après un long débat, invoquant la nécessité de règles plus claires sur la publication des données publiques. Les responsables ont précisé que le nouveau cadre entrera en vigueur au printemps et que la conformité sera révisée chaque année, avec un rapport public publié annuellement.</p><p>Un deuxième paragraphe ajoute suffisamment de texte pour que l'extracteur conserve l'article au lieu de le rejeter comme trop court.</p></article></body></html>"#,
    r#"<html lang="en"><head><title>Single paragraph</title></head><body><article><h1>Single paragraph</h1><p>The council approved the measure after a lengthy debate, citing the need for clearer rules on how public data is published. Officials said the new framework will take effect next spring and that compliance will be reviewed annually, with a public report published each year. Critics argued the timeline is too tight for smaller publishers, and that the reporting burden should scale with the size of the organisation rather than applying uniformly to every outlet that publishes public-interest data.</p></article></body></html>"#,
];

/// A windows-1252 document. `\x93` and `\x94` are curly quotes only in windows-1252, and `\xe9`
/// and `\xef` are accented letters. The `meta` charset is not what decides the decoding: the core
/// scores its single-byte tables and switches to windows-1252 because the payload holds C1 bytes.
#[cfg(test)]
const WINDOWS_1252: &[u8] = b"<html lang=\"en\"><head><meta charset=\"windows-1252\"><title>Caf\xe9</title></head><body><article><h1>Caf\xe9</h1><p>The council approved the measure after a lengthy debate, citing the need for clearer rules on how public data is published. Officials said the new framework will take effect next spring and that compliance will be reviewed annually, with a public report published each year.</p><p>\x93The r\xe9sum\xe9 is na\xefve\x94, a critic said, and those quote marks carry the whole argument.</p></article></body></html>";

/// The plain twin of [`GZIP`].
#[cfg(test)]
const GZIP_PLAIN: &str = r#"<html lang="en"><head><title>Gzipped fixture</title></head><body><article><h1>Gzipped fixture</h1><p>A compressed payload must reach the same document as its plain-text twin: the core detects the gzip magic, inflates the stream and only then looks for a charset, so the decoded text is identical either way.</p><p>The second paragraph gives the extractor enough body text to keep the article instead of discarding it as too short for the minimum output size.</p></article></body></html>"#;

/// [`GZIP_PLAIN`] through `gzip -n -9`, pasted as bytes so the fixture does not depend on an encoder.
#[cfg(test)]
const GZIP: &[u8] = &[
    0x1f, 0x8b, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x03, 0x65, 0x51, 0x41, 0x4e, 0xc4, 0x30,
    0x0c, 0xfc, 0x8a, 0xb5, 0x67, 0xa0, 0xe2, 0x8a, 0x42, 0x25, 0x4e, 0x7c, 0x80, 0x0f, 0x98, 0xc4,
    0xdb, 0x58, 0x9b, 0xc4, 0x51, 0xe2, 0xb2, 0xdb, 0x7d, 0x3d, 0x4e, 0xbb, 0xe2, 0xc2, 0x75, 0x3c,
    0xe3, 0x19, 0x8f, 0x5d, 0xd4, 0x9c, 0x20, 0x61, 0x59, 0xde, 0x4f, 0x54, 0x4e, 0xb3, 0x8b, 0x84,
    0x61, 0x76, 0xca, 0x9a, 0x68, 0xfe, 0xbc, 0x73, 0xad, 0x14, 0xe0, 0xcc, 0x37, 0x5d, 0x1b, 0xb9,
    0xe9, 0x80, 0xdd, 0x74, 0x90, 0xbe, 0x25, 0x6c, 0xb3, 0xc3, 0xa6, 0xec, 0x07, 0x1a, 0x5f, 0xff,
    0x0b, 0x0c, 0x73, 0x75, 0xfe, 0x00, 0x2f, 0xb9, 0x36, 0xea, 0xdd, 0x66, 0x15, 0xb7, 0x24, 0x18,
    0x20, 0xaf, 0x5d, 0xa1, 0x11, 0xfa, 0x08, 0x1a, 0x09, 0x3a, 0x66, 0x82, 0x20, 0x7e, 0xcd, 0x54,
    0x14, 0xb0, 0x03, 0x6b, 0x87, 0x9a, 0x90, 0xcb, 0xb3, 0xd2, 0x4d, 0x41, 0xaf, 0x5c, 0xde, 0x76,
    0xa6, 0x97, 0x66, 0x4c, 0x52, 0xf2, 0xc6, 0x18, 0xc0, 0x62, 0xae, 0x90, 0x71, 0x61, 0xff, 0x04,
    0x5c, 0xce, 0x09, 0x95, 0x8e, 0x41, 0x57, 0xdb, 0x9f, 0x01, 0x4b, 0x00, 0x29, 0x69, 0x1b, 0x58,
    0x81, 0x24, 0x72, 0xe9, 0x70, 0x96, 0x06, 0x08, 0x3e, 0x62, 0xeb, 0xa4, 0x4f, 0xd0, 0x65, 0x17,
    0x04, 0xf2, 0x12, 0x2c, 0xe3, 0xee, 0xc8, 0x96, 0x21, 0x58, 0x18, 0xf6, 0x98, 0x80, 0xd8, 0xe6,
    0x0d, 0xae, 0xb8, 0xbd, 0xb8, 0xa9, 0x8e, 0xa3, 0xbe, 0x86, 0x81, 0xf1, 0xcb, 0x38, 0xa9, 0xe1,
    0xd2, 0xb0, 0x46, 0x58, 0xf8, 0xe7, 0xe1, 0x6d, 0x1b, 0x1a, 0x7a, 0x35, 0x1b, 0x2a, 0xb2, 0x2e,
    0x11, 0x46, 0x5b, 0xc7, 0x62, 0x15, 0xb8, 0x10, 0xd5, 0x9d, 0xf6, 0x68, 0xcf, 0x72, 0x77, 0xb5,
    0x52, 0x41, 0xce, 0x10, 0xb8, 0x7b, 0x6c, 0x81, 0xcb, 0x62, 0x1d, 0x8c, 0x26, 0x54, 0x04, 0x7a,
    0x94, 0xa6, 0x7b, 0xea, 0xa1, 0xca, 0x5c, 0x38, 0xaf, 0x19, 0x64, 0xd5, 0xba, 0x2a, 0x74, 0xbe,
    0xd3, 0x11, 0x6b, 0xfa, 0xfb, 0xc6, 0x74, 0x7c, 0x67, 0x1a, 0xff, 0x9d, 0x7f, 0x01, 0xe9, 0x71,
    0x65, 0x49, 0xe6, 0x01, 0x00, 0x00,
];

#[cfg(test)]
mod tests {
    use super::*;
    use crate::contract::parse_request;
    use serde_json::json;

    const TEXT_ONLY: &[u8] = br#"{"responseFields":["contentText"]}"#;
    const EVERYTHING: &[u8] = br#"{"responseFields":["contentText","commentsText","metadata"]}"#;
    /// Index into [`FIXTURES`] of the document that carries a comments container.
    const WITH_COMMENTS: usize = 3;

    /// The debug form of the core options, which is how the option mapping is compared: the crate
    /// keeps its fields private.
    fn debug_of(options: Options) -> String {
        format!("{:?}", options.build())
    }

    /// [`build`] on raw bytes, the path most tests here cover.
    fn build_bytes(request: &Request, payload: &[u8]) -> Response {
        build(request, Payload::Bytes(payload))
    }

    #[test]
    fn bytes_path_matches_core_on_inline_fixtures() {
        for (index, html) in FIXTURES.iter().enumerate() {
            let request = parse_request(TEXT_ONLY).unwrap();
            let response = build_bytes(&request, html.as_bytes());
            let expected = Options::new()
                .with_metadata(false)
                .build()
                .bare_extraction(&Input::bytes(html.as_bytes()), &Call::default())
                .unwrap();
            assert_eq!(
                response.content_text.as_deref(),
                Some(expected.text.as_deref().unwrap_or_default()),
                "FIXTURES[{index}] differs between the byte path and the core"
            );
        }
    }

    #[test]
    fn payload_text_skips_the_binary_check() {
        // A PDF magic number with no `html` near its start is a binary file to the core, so the byte
        // path rejects it. The same bytes sent as text are a page, and the text path extracts them.
        let html = format!("%PDF-{}{}", "-".repeat(250), FIXTURES[0]);
        let request = parse_request(TEXT_ONLY).unwrap();
        let from_bytes = build_bytes(&request, html.as_bytes());
        let error = from_bytes
            .error
            .expect("the byte path must reject a binary magic");
        assert_eq!(error.kind, "MissingDocument");
        let from_text = build(&request, Payload::Text(&html));
        assert!(from_text.error.is_none(), "{:?}", from_text.error);
        let text = from_text.content_text.expect("contentText was requested");
        assert!(text.contains("The council approved the measure"), "{text}");
    }

    #[test]
    fn empty_text_payload_is_missing_document() {
        let response = build(&parse_request(br#"{}"#).unwrap(), Payload::Text(""));
        let error = response
            .error
            .expect("an empty text payload has no document");
        assert_eq!(error.kind, "MissingDocument");
        assert_eq!(error.message, "page rejected: empty tree");
    }

    #[test]
    fn gzip_input_is_decoded() {
        let request = parse_request(TEXT_ONLY).unwrap();
        let compressed = build_bytes(&request, GZIP);
        let plain = build_bytes(&request, GZIP_PLAIN.as_bytes());
        assert!(
            !plain.content_text.as_deref().unwrap_or_default().is_empty(),
            "the plain twin must extract text, or this proves nothing"
        );
        assert!(compressed.error.is_none());
        assert_eq!(compressed.content_text, plain.content_text);
    }

    #[test]
    fn meta_charset_is_detected() {
        let response = build_bytes(&parse_request(TEXT_ONLY).unwrap(), WINDOWS_1252);
        assert!(response.error.is_none(), "{:?}", response.error);
        let text = response.content_text.expect("contentText was requested");
        assert!(text.contains("r\u{e9}sum\u{e9}"), "{text}");
        assert!(text.contains('\u{201c}'), "{text}");
    }

    #[test]
    fn empty_payload_reaches_the_core() {
        let response = build_bytes(&parse_request(br#"{}"#).unwrap(), &[]);
        let error = response.error.expect("the core must report its own error");
        assert_eq!(error.kind, "MissingDocument");
        assert_eq!(error.message, "page rejected: empty tree");
        assert_eq!(error.context, None);
    }

    #[test]
    fn default_response_fields_are_text_comments_and_metadata() {
        let response = build_bytes(&parse_request(br#"{}"#).unwrap(), FIXTURES[0].as_bytes());
        assert!(!response
            .content_text
            .as_deref()
            .unwrap_or_default()
            .is_empty());
        assert_eq!(response.comments_text.as_deref(), Some(""));
        assert!(response.metadata.is_some());
    }

    #[test]
    fn response_fields_select_only_requested() {
        let html = FIXTURES[WITH_COMMENTS].as_bytes();
        let everything = build_bytes(&parse_request(EVERYTHING).unwrap(), html);
        assert!(everything.content_text.is_some());
        assert!(everything.comments_text.is_some());
        assert!(everything.metadata.is_some());

        let text_only = build_bytes(&parse_request(TEXT_ONLY).unwrap(), html);
        assert!(text_only.content_text.is_some());
        assert!(text_only.comments_text.is_none());
        assert!(text_only.metadata.is_none());

        let metadata_only = build_bytes(
            &parse_request(br#"{"responseFields":["metadata"]}"#).unwrap(),
            html,
        );
        assert!(metadata_only.metadata.is_some());
        assert!(metadata_only.content_text.is_none());
        assert!(metadata_only.comments_text.is_none());
        let value = serde_json::to_value(&metadata_only).unwrap();
        assert_eq!(value.as_object().unwrap().len(), 5, "{value}");
        assert_eq!(value["metadata"].as_object().unwrap().len(), 13, "{value}");
    }

    #[test]
    fn metadata_is_serialized_for_the_requested_field() {
        let response = build_bytes(
            &parse_request(br#"{"responseFields":["metadata"]}"#).unwrap(),
            FIXTURES[1].as_bytes(),
        );
        let metadata = response.metadata.expect("metadata was requested");
        assert_eq!(metadata.title, "Filed article");
        assert_eq!(metadata.author, "Ada Lovelace");
        assert_eq!(metadata.sitename, "Example Journal");
        assert_eq!(metadata.date.as_deref(), Some("2026-09-29"));
        let value = serde_json::to_value(&metadata).unwrap();
        assert!(value["categories"].is_array() && value["tags"].is_array());
        assert!(value.get("pageType").is_some());
    }

    #[test]
    fn metadata_date_is_null_when_absent() {
        let response = build_bytes(
            &parse_request(br#"{"responseFields":["metadata"]}"#).unwrap(),
            FIXTURES[0].as_bytes(),
        );
        let metadata = response.metadata.expect("metadata was requested");
        assert_eq!(metadata.date, None);
        assert!(serde_json::to_value(&metadata).unwrap()["date"].is_null());
    }

    #[test]
    fn metadata_keys_follow_the_document() {
        // One distinct value per field, so a swap between two wire keys shows as a mismatch. The
        // url is the canonical link, and the hostname is taken from it.
        let html = r#"<html lang="en"><head><title>f-title</title><meta name="author" content="Frances Author"><meta name="description" content="f-description"><meta property="og:site_name" content="Sitename Gazette"><meta property="og:type" content="f-pagetype"><meta property="og:image" content="https://example.org/f-image.jpg"><meta property="article:tag" content="f-tag"><meta property="article:published_time" content="2026-09-29T00:00:00Z"><link rel="canonical" href="https://example.org/f-url"></head><body><article><h1>f-title</h1><p>The council approved the measure after a lengthy debate, citing the need for clearer rules on how public data is published. Officials said the new framework will take effect next spring and that compliance will be reviewed annually, with a public report published each year.</p><p class="postmeta"><a href="https://example.org/category/f-category">f-category</a></p><p><a rel="license" href="https://example.org/f-license">f-license</a></p><p>A second paragraph adds enough body text for the extractor to keep the article instead of discarding it as too short.</p></article></body></html>"#;
        let response = build_bytes(
            &parse_request(br#"{"responseFields":["metadata"]}"#).unwrap(),
            html.as_bytes(),
        );
        assert!(response.error.is_none(), "{:?}", response.error);
        let value = serde_json::to_value(response.metadata.unwrap()).unwrap();
        assert_eq!(value["title"], "f-title", "{value}");
        assert_eq!(value["author"], "Frances Author", "{value}");
        assert_eq!(value["url"], "https://example.org/f-url", "{value}");
        assert_eq!(value["hostname"], "example.org", "{value}");
        assert_eq!(value["description"], "f-description", "{value}");
        assert_eq!(value["sitename"], "Sitename Gazette", "{value}");
        assert_eq!(value["date"], "2026-09-29", "{value}");
        assert_eq!(value["categories"], json!(["f-category"]), "{value}");
        assert_eq!(value["tags"], json!(["f-tag"]), "{value}");
        assert_eq!(value["license"], "f-license", "{value}");
        assert_eq!(value["image"], "https://example.org/f-image.jpg", "{value}");
        assert_eq!(value["pageType"], "f-pagetype", "{value}");
        assert_eq!(value["language"], "", "{value}");
    }

    #[test]
    fn url_is_the_default_metadata_url() {
        let response = build_bytes(
            &parse_request(br#"{"url":"https://example.org/post","responseFields":["metadata"]}"#)
                .unwrap(),
            FIXTURES[0].as_bytes(),
        );
        let value = serde_json::to_value(response.metadata.unwrap()).unwrap();
        assert_eq!(value["url"], "https://example.org/post", "{value}");
        assert_eq!(value["hostname"], "example.org", "{value}");
    }

    #[test]
    fn empty_url_is_absent() {
        let html = FIXTURES[0].as_bytes();
        let absent = build_bytes(&parse_request(EVERYTHING).unwrap(), html);
        let empty = build_bytes(
            &parse_request(
                br#"{"url":"","responseFields":["contentText","commentsText","metadata"]}"#,
            )
            .unwrap(),
            html,
        );
        assert_eq!(
            serde_json::to_value(&absent).unwrap(),
            serde_json::to_value(&empty).unwrap()
        );
    }

    #[test]
    fn response_keeps_its_shape_on_failure() {
        let response = build_bytes(&parse_request(TEXT_ONLY).unwrap(), &[]);
        let value = serde_json::to_value(&response).unwrap();
        assert_eq!(value["schemaVersion"], 2);
        assert_eq!(value["contentText"], "");
        assert!(value["commentsText"].is_null());
        assert!(value["metadata"].is_null());
        assert_eq!(value["error"]["kind"], "MissingDocument");
        assert!(value["error"].get("context").is_none(), "{value}");
    }

    #[test]
    fn requested_fields_are_empty_strings_on_failure() {
        let response = build_bytes(&parse_request(EVERYTHING).unwrap(), &[]);
        assert_eq!(response.content_text.as_deref(), Some(""));
        assert_eq!(response.comments_text.as_deref(), Some(""));
        assert!(
            response.metadata.is_none(),
            "metadata stays null on failure"
        );
    }

    #[test]
    fn every_response_key_is_present_on_success_and_on_failure() {
        // An absent key is not a null value. A `skip_serializing_if` only drops a key whose value is
        // `None`, so the requests below are chosen to leave every key `None` in at least one of them:
        // a response that populates all five cannot show that the key survives being absent.
        const KEYS: [&str; 5] = [
            "schemaVersion",
            "contentText",
            "commentsText",
            "metadata",
            "error",
        ];
        let cases: [(&str, &[u8], &[u8]); 5] = [
            ("default fields on a page", br#"{}"#, FIXTURES[0].as_bytes()),
            ("text only on a page", TEXT_ONLY, FIXTURES[0].as_bytes()),
            (
                "comments only on a page",
                br#"{"responseFields":["commentsText"]}"#,
                FIXTURES[0].as_bytes(),
            ),
            ("text only on a failure", TEXT_ONLY, &[]),
            ("every field on a failure", EVERYTHING, &[]),
        ];
        for (case, header, payload) in cases {
            let value = serde_json::to_value(build_bytes(&parse_request(header).unwrap(), payload))
                .unwrap();
            let object = value.as_object().expect("a response is a JSON object");
            assert_eq!(object.len(), KEYS.len(), "{case}: {value}");
            for key in KEYS {
                assert!(object.contains_key(key), "{case} must carry {key}: {value}");
            }
        }
    }

    #[test]
    fn failure_fills_only_the_content_field_that_was_requested() {
        // One field per request: with every field requested at once, a swapped arm in the failure
        // branch still fills both, so the swap would pass a test that only requests everything.
        const FIELDS: [&str; 2] = ["contentText", "commentsText"];
        for requested in FIELDS {
            let header = format!(r#"{{"responseFields":["{requested}"]}}"#);
            let value =
                serde_json::to_value(build_bytes(&parse_request(header.as_bytes()).unwrap(), &[]))
                    .unwrap();
            for field in FIELDS {
                if field == requested {
                    assert_eq!(value[field], "", "{requested} was requested: {value}");
                } else {
                    assert!(
                        value[field].is_null(),
                        "{requested} was the only field requested, so {field} must stay null: {value}"
                    );
                }
            }
            assert!(value["metadata"].is_null(), "{value}");
        }
    }

    #[test]
    fn error_context_carries_the_core_counts() {
        let html = FIXTURES[0].as_bytes();
        let expected = Options::new()
            .config(Config {
                min_output_size: 10_000,
                ..Config::default()
            })
            .build()
            .bare_extraction(&Input::bytes(html), &Call::default())
            .unwrap_err();
        let response = build_bytes(
            &parse_request(br#"{"options":{"config":{"minOutputSize":10000}}}"#).unwrap(),
            html,
        );
        let error = response.error.expect("minOutputSize 10000 must fail");
        assert_eq!(error.kind, "OutputTooShort");
        assert_eq!(error.context, contract::error_kind(&expected).1);
        let context = error.context.unwrap();
        assert!(
            context["textChars"].as_u64().unwrap_or_default() > 0,
            "the mapped minOutputSize must reach the core: {context}"
        );
        assert_eq!(context["commentsChars"], json!(0));
    }

    #[test]
    fn node_cap_rejects_a_page_past_500000_nodes() {
        let html = format!("<html><body>{}</body></html>", "<b></b>".repeat(500_001));
        let response = build_bytes(&parse_request(TEXT_ONLY).unwrap(), html.as_bytes());
        let error = response.error.expect("500,001 elements must be rejected");
        assert_eq!(error.kind, "OutputTreeTooLong");
        assert_eq!(error.context, Some(json!({"limit": "maxNodes"})));
    }

    #[test]
    fn options_absent_uses_core_defaults() {
        let html = FIXTURES[0].as_bytes();
        let response = build_bytes(&parse_request(br#"{}"#).unwrap(), html);
        let expected = Options::new()
            .with_metadata(true)
            .build()
            .bare_extraction(&Input::bytes(html), &Call::default())
            .unwrap();
        assert_eq!(
            response.content_text,
            Some(expected.text.unwrap_or_default())
        );
        assert_eq!(
            response.comments_text,
            Some(expected.comments.unwrap_or_default())
        );
        assert_eq!(
            response.metadata.unwrap().title,
            expected.title.unwrap_or_default()
        );
    }

    #[test]
    fn empty_options_object_equals_absent_options() {
        let html = FIXTURES[0].as_bytes();
        let absent = build_bytes(&parse_request(br#"{}"#).unwrap(), html);
        let empty = build_bytes(&parse_request(br#"{"options":{}}"#).unwrap(), html);
        assert_eq!(
            serde_json::to_value(&absent).unwrap(),
            serde_json::to_value(&empty).unwrap()
        );
    }

    #[test]
    fn options_map_onto_the_crate_builders() {
        let with_metadata = || Options::new().with_metadata(true);
        let cases: Vec<(&[u8], Options)> = vec![
            (br#"{}"#, with_metadata()),
            (TEXT_ONLY, Options::new()),
            (
                br#"{"options":{"targetLanguage":"en"}}"#,
                with_metadata().target_language("en"),
            ),
            (
                br#"{"options":{"focus":"balanced"}}"#,
                with_metadata().focus(Focus::Balanced),
            ),
            (
                br#"{"options":{"focus":"favorPrecision"}}"#,
                with_metadata().focus(Focus::Precision),
            ),
            (
                br#"{"options":{"focus":"favorRecall"}}"#,
                with_metadata().focus(Focus::Recall),
            ),
            (
                br#"{"options":{"excludeComments":true}}"#,
                with_metadata().comments(false),
            ),
            (
                br#"{"options":{"excludeTables":true}}"#,
                with_metadata().tables(false),
            ),
            (
                br#"{"options":{"includeImages":true}}"#,
                with_metadata().images(true),
            ),
            (
                br#"{"options":{"includeLinks":true}}"#,
                with_metadata().links(true),
            ),
            (
                br#"{"options":{"blacklistedAuthors":["a"]}}"#,
                with_metadata().author_blacklist(["a"]),
            ),
            (
                br#"{"options":{"hasEssentialMetadata":true}}"#,
                with_metadata().only_with_metadata(true),
            ),
            (
                br#"{"options":{"maxTreeSize":4000}}"#,
                with_metadata().config(Config {
                    max_tree_size: Some(4000),
                    ..Config::default()
                }),
            ),
            (
                br#"{"options":{"htmlDateMode":"default"}}"#,
                with_metadata(),
            ),
            (
                br#"{"options":{"htmlDateMode":"fast"}}"#,
                with_metadata().config(Config {
                    extensive_date_search: false,
                    ..Config::default()
                }),
            ),
            (
                br#"{"options":{"htmlDateMode":"extensive"}}"#,
                with_metadata().config(Config {
                    extensive_date_search: true,
                    ..Config::default()
                }),
            ),
            (
                br#"{"options":{"config":{"minExtractedSize":300,"minOutputSize":4,"minOutputCommentSize":5}}}"#,
                with_metadata().config(Config {
                    min_extracted_size: 300,
                    min_output_size: 4,
                    min_output_comm_size: 5,
                    ..Config::default()
                }),
            ),
            (
                br#"{"responseFields":["commentsText"]}"#,
                Options::new(),
            ),
        ];
        for (header, expected) in cases {
            let request = parse_request(header).unwrap();
            assert_eq!(
                debug_of(options_from(&request)),
                debug_of(expected),
                "{}",
                String::from_utf8_lossy(header)
            );
        }
    }

    #[test]
    fn negative_and_zero_sizes_mean_no_limit() {
        let no_limit = Config {
            max_tree_size: None,
            ..Config::default()
        };
        for header in [
            &br#"{"options":{"maxTreeSize":-1}}"#[..],
            &br#"{"options":{"maxTreeSize":0}}"#[..],
        ] {
            let request = parse_request(header).unwrap();
            assert_eq!(
                debug_of(options_from(&request)),
                debug_of(with_metadata_only(no_limit.clone())),
                "{}",
                String::from_utf8_lossy(header)
            );
        }
        let request = parse_request(
            br#"{"options":{"config":{"minExtractedSize":-5,"minOutputSize":-5,"minOutputCommentSize":-5}}}"#,
        )
        .unwrap();
        let zeroed = Config {
            min_extracted_size: 0,
            min_output_size: 0,
            min_output_comm_size: 0,
            ..Config::default()
        };
        assert_eq!(
            debug_of(options_from(&request)),
            debug_of(with_metadata_only(zeroed))
        );
    }

    /// The default-fields options with `config` set, as the mapping builds them.
    fn with_metadata_only(config: Config) -> Options {
        Options::new().with_metadata(true).config(config)
    }
}
