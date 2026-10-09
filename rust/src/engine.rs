//! Core invocation and response assembly.
//!
//! [`build`] is the only path from a validated [`Request`] to a [`Response`]: it maps the wire
//! options onto the core's `Options`/`Config`, runs the extraction and fills in exactly the fields
//! the request named. A core error is not returned as `Err`; it lands in [`Response::error`], so
//! success and failure keep one envelope shape.

use crate::contract::{self, ConfigJson, Header, Request, ResponseField, SCHEMA_VERSION};
use rust::{Config, ExtractResult, ExtractionFocus, HtmlDateMode, Metadata, Options, Tree, Url};
use serde::Serialize;

/// The response `metadata` object: one key per core [`Metadata`] field.
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MetadataJson {
    pub title: String,
    pub author: String,
    pub url: String,
    pub hostname: String,
    pub description: String,
    pub sitename: String,
    /// `None` when the page carries no date; the core's zero sentinel never reaches the wire.
    pub date: Option<String>,
    pub categories: Vec<String>,
    pub tags: Vec<String>,
    pub id: String,
    pub fingerprint: String,
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

/// The response envelope, schema version 1.
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
    pub content_html: Option<String>,
    pub comments_html: Option<String>,
    pub metadata: Option<MetadataJson>,
    pub error: Option<ErrorEnvelope>,
}

/// Extracts from a raw payload and assembles the response. Infallible: core errors are mapped
/// onto [`Response::error`].
pub fn build(request: &Request, payload: &[u8]) -> Response {
    assemble(
        request,
        rust::extract(payload, &options_from(&request.header)),
    )
}

/// Extracts from HTML the caller already decoded, on the same pipeline as [`build`].
///
/// It encodes to UTF-8 and delegates, as the design requires of `ExtractFromHtml` (section 8):
/// the byte path detects the charset and normalizes the text (NFC, soft hyphens dropped), so a
/// caller that skipped it would not see the same result for the same text.
pub fn build_from_html(request: &Request, html: &str) -> Response {
    build(request, html.as_bytes())
}

/// Fills in the requested fields of a response from the core's result, or the error envelope.
fn assemble(request: &Request, extracted: Result<ExtractResult, rust::Error>) -> Response {
    let fields = &request.fields;
    let mut response = Response {
        schema_version: SCHEMA_VERSION,
        content_text: None,
        comments_text: None,
        content_html: None,
        comments_html: None,
        metadata: None,
        error: None,
    };
    match extracted {
        Ok(result) => {
            let ExtractResult {
                content_node,
                comments_node,
                content_text,
                comments_text,
                metadata,
            } = result;
            if fields.contains(&ResponseField::ContentText) {
                response.content_text = Some(content_text);
            }
            if fields.contains(&ResponseField::CommentsText) {
                response.comments_text = Some(comments_text);
            }
            if fields.contains(&ResponseField::ContentHtml) {
                response.content_html = Some(outer_html(&content_node));
            }
            if fields.contains(&ResponseField::CommentsHtml) {
                response.comments_html =
                    Some(comments_node.as_ref().map_or_else(String::new, outer_html));
            }
            if fields.contains(&ResponseField::Metadata) {
                response.metadata = Some(metadata_json(metadata));
            }
        }
        Err(error) => {
            for field in fields {
                match field {
                    ResponseField::ContentText => response.content_text = Some(String::new()),
                    ResponseField::CommentsText => response.comments_text = Some(String::new()),
                    ResponseField::ContentHtml => response.content_html = Some(String::new()),
                    ResponseField::CommentsHtml => response.comments_html = Some(String::new()),
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

/// Serializes one tree root, never the whole document.
fn outer_html(tree: &Tree) -> String {
    tree.document.outer_html(tree.root)
}

/// Maps the request header onto the core options. An absent key keeps the core's own default, so
/// an empty `options` object is exactly `Options::default()`.
fn options_from(header: &Header) -> Options {
    let mut options = Options {
        config: header
            .options
            .as_ref()
            .and_then(|options| options.config.as_ref())
            .map(config_from),
        input_encoding: header.input_encoding.clone().unwrap_or_default(),
        original_url: header
            .url
            .as_deref()
            .filter(|url| !url.is_empty())
            .and_then(Url::request),
        ..Options::default()
    };
    let Some(json) = &header.options else {
        return options;
    };
    if let Some(value) = &json.target_language {
        options.target_language.clone_from(value);
    }
    if let Some(value) = json.enable_fallback {
        options.enable_fallback = value;
    }
    if let Some(value) = json.focus {
        options.focus = focus_from(value);
    }
    if let Some(value) = json.exclude_comments {
        options.exclude_comments = value;
    }
    if let Some(value) = json.exclude_tables {
        options.exclude_tables = value;
    }
    if let Some(value) = json.include_images {
        options.include_images = value;
    }
    if let Some(value) = json.include_links {
        options.include_links = value;
    }
    if let Some(value) = &json.blacklisted_authors {
        options.blacklisted_authors.clone_from(value);
    }
    if let Some(value) = json.deduplicate {
        options.deduplicate = value;
    }
    if let Some(value) = json.has_essential_metadata {
        options.has_essential_metadata = value;
    }
    if let Some(value) = json.max_tree_size {
        options.max_tree_size = value;
    }
    if let Some(value) = json.html_date_mode {
        options.html_date_mode = html_date_mode_from(value);
    }
    if let Some(value) = &json.prune_selector {
        options.prune_selector.clone_from(value);
    }
    options
}

/// Maps the `options.config` object onto a core [`Config`], each omitted key from
/// [`Config::default`].
fn config_from(json: &ConfigJson) -> Config {
    let default = Config::default();
    Config {
        cache_size: json.cache_size.unwrap_or(default.cache_size),
        max_duplicate_count: json
            .max_duplicate_count
            .unwrap_or(default.max_duplicate_count),
        min_duplicate_check_size: json
            .min_duplicate_check_size
            .unwrap_or(default.min_duplicate_check_size),
        min_extracted_size: json
            .min_extracted_size
            .unwrap_or(default.min_extracted_size),
        min_extracted_comment_size: json
            .min_extracted_comment_size
            .unwrap_or(default.min_extracted_comment_size),
        min_output_size: json.min_output_size.unwrap_or(default.min_output_size),
        min_output_comment_size: json
            .min_output_comment_size
            .unwrap_or(default.min_output_comment_size),
    }
}

/// The wire `focus` enum is not the core enum, so no `From` shortcut applies.
fn focus_from(focus: contract::FocusJson) -> ExtractionFocus {
    match focus {
        contract::FocusJson::Balanced => ExtractionFocus::Balanced,
        contract::FocusJson::FavorRecall => ExtractionFocus::FavorRecall,
        contract::FocusJson::FavorPrecision => ExtractionFocus::FavorPrecision,
    }
}

/// The wire `htmlDateMode` enum is not the core enum, so no `From` shortcut applies.
fn html_date_mode_from(mode: contract::HtmlDateModeJson) -> HtmlDateMode {
    match mode {
        contract::HtmlDateModeJson::Default => HtmlDateMode::Default,
        contract::HtmlDateModeJson::Fast => HtmlDateMode::Fast,
        contract::HtmlDateModeJson::Extensive => HtmlDateMode::Extensive,
        contract::HtmlDateModeJson::Disabled => HtmlDateMode::Disabled,
    }
}

/// Moves the core metadata into its wire shape, formatting the date on the way.
fn metadata_json(metadata: Metadata) -> MetadataJson {
    MetadataJson {
        title: metadata.title,
        author: metadata.author,
        url: metadata.url,
        hostname: metadata.hostname,
        description: metadata.description,
        sitename: metadata.sitename,
        date: contract::format_date(metadata.date),
        categories: metadata.categories,
        tags: metadata.tags,
        id: metadata.id,
        fingerprint: metadata.fingerprint,
        license: metadata.license,
        language: metadata.language,
        image: metadata.image,
        page_type: metadata.page_type,
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

/// [`GZIP_PLAIN`] through `gzip -n -9`, pasted as bytes because `flate2` is not a dependency of
/// this crate.
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
    const EVERYTHING: &[u8] = br#"{"responseFields":["contentText","commentsText","contentHtml","commentsHtml","metadata"]}"#;
    /// Index into [`FIXTURES`] of the document that carries a comments container.
    const WITH_COMMENTS: usize = 3;

    #[test]
    fn bytes_path_matches_core_on_inline_fixtures() {
        for html in FIXTURES {
            let request = parse_request(TEXT_ONLY).unwrap();
            let response = build(&request, html.as_bytes());
            let expected = rust::extract(html.as_bytes(), &Options::default()).unwrap();
            assert_eq!(
                response.content_text.as_deref(),
                Some(expected.content_text.as_str())
            );
        }
    }

    #[test]
    fn html_string_and_bytes_agree_for_the_same_text() {
        // Enough multi-byte characters that the byte path detects UTF-8 instead of guessing a
        // single-byte table, plus a soft hyphen and an NFD sequence for it to normalize.
        let html = "<html lang=\"en\"><head><title>Soft hyphen</title></head><body><article><h1>Soft hyphen</h1><p>The council approved the measure after a lengthy debate, citing the need for clearer rules on how public data is published. Officials said the new framework will take effect next spring and that compliance will be reviewed annually, with a public report published each year. A so\u{ad}ft hyphen sits beside a cafe\u{301}, a caf\u{e9}, a na\u{ef}ve r\u{e9}sum\u{e9} and a souffl\u{e9}.</p><p>A second paragraph adds enough body text for the extractor to keep the article instead of discarding it as too short.</p></article></body></html>";
        let request = parse_request(TEXT_ONLY).unwrap();
        let from_html = build_from_html(&request, html);
        let from_bytes = build(&request, html.as_bytes());
        assert_eq!(from_html.content_text, from_bytes.content_text);
        let text = from_html.content_text.expect("contentText was requested");
        assert!(!text.contains('\u{ad}'), "soft hyphen survived: {text}");
        assert!(text.contains("soft hyphen"), "{text}");
        assert!(text.contains("caf\u{e9}"), "{text}");
    }

    #[test]
    fn gzip_input_is_decoded() {
        let request = parse_request(TEXT_ONLY).unwrap();
        let compressed = build(&request, GZIP);
        let plain = build(&request, GZIP_PLAIN.as_bytes());
        assert!(
            !plain.content_text.as_deref().unwrap_or_default().is_empty(),
            "the plain twin must extract text, or this proves nothing"
        );
        assert!(compressed.error.is_none());
        assert_eq!(compressed.content_text, plain.content_text);
    }

    #[test]
    fn truncated_gzip_returns_io_error() {
        let request = parse_request(br#"{}"#).unwrap();
        let response = build(&request, &[0x1f, 0x8b, 0x08, 0x00, 0x00]);
        assert_eq!(response.error.unwrap().kind, "Io");
    }

    #[test]
    fn meta_charset_is_detected() {
        let response = build(&parse_request(TEXT_ONLY).unwrap(), WINDOWS_1252);
        assert!(response.error.is_none(), "{:?}", response.error);
        let text = response.content_text.expect("contentText was requested");
        assert!(text.contains("r\u{e9}sum\u{e9}"), "{text}");
        assert!(text.contains('\u{201c}'), "{text}");
    }

    #[test]
    fn input_encoding_selects_the_decoder() {
        let detected = build(&parse_request(TEXT_ONLY).unwrap(), FIXTURES[9].as_bytes());
        let forced = build(
            &parse_request(br#"{"responseFields":["contentText"],"inputEncoding":"windows-1252"}"#)
                .unwrap(),
            FIXTURES[9].as_bytes(),
        );
        let detected = detected.content_text.unwrap();
        let forced = forced.content_text.unwrap();
        assert!(detected.contains("approuv\u{e9}"), "{detected}");
        assert!(
            forced.contains("approuv\u{c3}\u{a9}"),
            "the explicit encoding must win: {forced}"
        );
    }

    #[test]
    fn empty_payload_reaches_the_core() {
        let response = build(&parse_request(br#"{}"#).unwrap(), &[]);
        let error = response.error.expect("the core must report its own error");
        assert_eq!(error.kind, "OutputTooShort");
        assert_eq!(error.message, "text and comments are not long enough: 0 0");
        assert_eq!(error.context, Some(json!({"content": 0, "comments": 0})));
    }

    #[test]
    fn default_response_fields_omit_html() {
        let response = build(&parse_request(br#"{}"#).unwrap(), FIXTURES[0].as_bytes());
        assert!(!response
            .content_text
            .as_deref()
            .unwrap_or_default()
            .is_empty());
        assert_eq!(response.comments_text.as_deref(), Some(""));
        assert!(response.metadata.is_some());
        assert!(response.content_html.is_none() && response.comments_html.is_none());
    }

    #[test]
    fn response_fields_select_only_requested() {
        let html = FIXTURES[WITH_COMMENTS].as_bytes();
        let everything = build(&parse_request(EVERYTHING).unwrap(), html);
        assert!(everything.content_text.is_some());
        assert!(everything.comments_text.is_some());
        assert!(everything.content_html.is_some());
        assert!(everything.comments_html.is_some());
        assert!(everything.metadata.is_some());

        let text_only = build(&parse_request(TEXT_ONLY).unwrap(), html);
        assert!(text_only.content_text.is_some());
        assert!(text_only.comments_text.is_none());
        assert!(text_only.content_html.is_none());
        assert!(text_only.comments_html.is_none());
        assert!(text_only.metadata.is_none());

        let html_only = build(
            &parse_request(br#"{"responseFields":["contentHtml"]}"#).unwrap(),
            html,
        );
        assert!(html_only.content_html.is_some());
        assert!(html_only.comments_html.is_none());
        assert!(html_only.content_text.is_none());
    }

    #[test]
    fn omitting_html_skips_serialization() {
        let request = parse_request(TEXT_ONLY).unwrap();
        let response = build(&request, FIXTURES[0].as_bytes());
        assert!(response.content_html.is_none() && response.comments_html.is_none());
    }

    #[test]
    fn content_html_serializes_only_the_content_root() {
        let html = FIXTURES[0].as_bytes();
        let response = build(
            &parse_request(br#"{"responseFields":["contentHtml"]}"#).unwrap(),
            html,
        );
        let expected = rust::extract(html, &Options::default()).unwrap();
        let expected_html = expected
            .content_node
            .document
            .outer_html(expected.content_node.root);
        assert_eq!(
            response.content_html.as_deref(),
            Some(expected_html.as_str())
        );
        assert!(
            !response.content_html.as_deref().unwrap().contains("<html"),
            "the whole document leaked into contentHtml"
        );
    }

    #[test]
    fn comments_html_is_empty_when_requested_without_comments_node() {
        let comments_only =
            parse_request(br#"{"responseFields":["commentsText","commentsHtml"]}"#).unwrap();
        let without = build(&comments_only, FIXTURES[0].as_bytes());
        assert_eq!(without.comments_text.as_deref(), Some(""));
        assert_eq!(without.comments_html.as_deref(), Some(""));
        let with = build(&comments_only, FIXTURES[WITH_COMMENTS].as_bytes());
        assert!(!with.comments_text.as_deref().unwrap_or_default().is_empty());
        assert!(!with.comments_html.as_deref().unwrap_or_default().is_empty());
        let unrequested = build(
            &parse_request(TEXT_ONLY).unwrap(),
            FIXTURES[WITH_COMMENTS].as_bytes(),
        );
        assert!(unrequested.comments_html.is_none());
    }

    #[test]
    fn metadata_is_serialized_for_the_requested_field() {
        let response = build(
            &parse_request(br#"{"responseFields":["metadata"]}"#).unwrap(),
            FIXTURES[1].as_bytes(),
        );
        let metadata = response.metadata.expect("metadata was requested");
        assert_eq!(metadata.title, "Filed article");
        assert_eq!(metadata.author, "Ada Lovelace");
        assert_eq!(metadata.sitename, "Example Journal");
        assert_eq!(metadata.date.as_deref(), Some("2026-09-29T00:00:00Z"));
        let value = serde_json::to_value(&metadata).unwrap();
        assert!(value["categories"].is_array() && value["tags"].is_array());
        assert!(value.get("pageType").is_some());
    }

    #[test]
    fn metadata_date_is_null_when_absent() {
        let response = build(
            &parse_request(br#"{"responseFields":["metadata"]}"#).unwrap(),
            FIXTURES[0].as_bytes(),
        );
        let metadata = response.metadata.expect("metadata was requested");
        assert_eq!(metadata.date, None);
        assert!(serde_json::to_value(&metadata).unwrap()["date"].is_null());
    }

    #[test]
    fn response_keeps_its_shape_on_failure() {
        let response = build(&parse_request(TEXT_ONLY).unwrap(), &[]);
        let value = serde_json::to_value(&response).unwrap();
        assert_eq!(value["schemaVersion"], 1);
        assert_eq!(value["contentText"], "");
        assert!(value["commentsText"].is_null());
        assert!(value["contentHtml"].is_null());
        assert!(value["commentsHtml"].is_null());
        assert!(value["metadata"].is_null());
        assert_eq!(value["error"]["kind"], "OutputTooShort");
        assert!(value["error"]["context"]["content"].is_number());
    }

    #[test]
    fn requested_fields_are_empty_strings_on_failure() {
        let response = build(&parse_request(EVERYTHING).unwrap(), &[]);
        assert_eq!(response.content_text.as_deref(), Some(""));
        assert_eq!(response.comments_text.as_deref(), Some(""));
        assert_eq!(response.content_html.as_deref(), Some(""));
        assert_eq!(response.comments_html.as_deref(), Some(""));
        assert!(
            response.metadata.is_none(),
            "metadata stays null on failure"
        );
    }

    #[test]
    fn error_context_carries_the_core_counts() {
        let options = Options {
            config: Some(Config {
                min_output_size: 10_000,
                ..Config::default()
            }),
            ..Options::default()
        };
        let expected = rust::extract(FIXTURES[0].as_bytes(), &options).unwrap_err();
        assert!(matches!(expected, rust::Error::OutputTooShort(..)));
        let response = build(
            &parse_request(br#"{"options":{"config":{"minOutputSize":10000}}}"#).unwrap(),
            FIXTURES[0].as_bytes(),
        );
        let error = response.error.expect("minOutputSize 10000 must fail");
        assert_eq!(error.kind, "OutputTooShort");
        assert_eq!(error.context, contract::error_kind(&expected).1);
        let context = error.context.unwrap();
        assert!(
            context["content"].as_u64().unwrap_or_default() > 0,
            "the mapped minOutputSize must reach the core: {context}"
        );
        assert_eq!(context["comments"], json!(0));
    }

    #[test]
    fn options_absent_uses_core_defaults() {
        let html = FIXTURES[0].as_bytes();
        let response = build(&parse_request(br#"{}"#).unwrap(), html);
        let expected = rust::extract(html, &Options::default()).unwrap();
        assert_eq!(
            response.content_text.as_deref(),
            Some(expected.content_text.as_str())
        );
        assert_eq!(
            response.comments_text.as_deref(),
            Some(expected.comments_text.as_str())
        );
        assert_eq!(response.metadata.unwrap().title, expected.metadata.title);
    }

    #[test]
    fn empty_options_object_equals_absent_options() {
        let html = FIXTURES[0].as_bytes();
        let absent = build(&parse_request(br#"{}"#).unwrap(), html);
        let empty = build(&parse_request(br#"{"options":{}}"#).unwrap(), html);
        assert_eq!(
            serde_json::to_value(&absent).unwrap(),
            serde_json::to_value(&empty).unwrap()
        );
    }

    #[test]
    fn wire_options_map_onto_every_core_field() {
        let request = parse_request(
            br#"{
                "url": "https://example.org/article",
                "inputEncoding": "windows-1252",
                "options": {
                    "targetLanguage": "en",
                    "enableFallback": true,
                    "focus": "favorRecall",
                    "excludeComments": true,
                    "excludeTables": true,
                    "includeImages": true,
                    "includeLinks": true,
                    "blacklistedAuthors": ["a", "b"],
                    "deduplicate": true,
                    "hasEssentialMetadata": true,
                    "maxTreeSize": 1050,
                    "htmlDateMode": "extensive",
                    "pruneSelector": "aside",
                    "config": {
                        "cacheSize": 10,
                        "maxDuplicateCount": 3,
                        "minDuplicateCheckSize": 7,
                        "minExtractedSize": 300,
                        "minExtractedCommentSize": 2,
                        "minOutputSize": 4,
                        "minOutputCommentSize": 5
                    }
                }
            }"#,
        )
        .unwrap();
        let options = options_from(&request.header);
        assert_eq!(options.input_encoding, "windows-1252");
        assert_eq!(
            options
                .original_url
                .as_ref()
                .map(ToString::to_string)
                .as_deref(),
            Some("https://example.org/article")
        );
        assert_eq!(options.target_language, "en");
        assert!(options.enable_fallback);
        assert_eq!(options.focus, ExtractionFocus::FavorRecall);
        assert!(options.exclude_comments);
        assert!(options.exclude_tables);
        assert!(options.include_images);
        assert!(options.include_links);
        assert_eq!(
            options.blacklisted_authors,
            vec!["a".to_owned(), "b".to_owned()]
        );
        assert!(options.deduplicate);
        assert!(options.has_essential_metadata);
        assert_eq!(options.max_tree_size, 1050);
        assert_eq!(options.html_date_mode, HtmlDateMode::Extensive);
        assert_eq!(options.prune_selector, "aside");
        assert!(!options.enable_log);
        assert_eq!(
            options.config,
            Some(Config {
                cache_size: 10,
                max_duplicate_count: 3,
                min_duplicate_check_size: 7,
                min_extracted_size: 300,
                min_extracted_comment_size: 2,
                min_output_size: 4,
                min_output_comment_size: 5,
            })
        );
    }

    #[test]
    fn focus_and_html_date_mode_map_by_variant() {
        for (wire, expected) in [
            ("balanced", ExtractionFocus::Balanced),
            ("favorRecall", ExtractionFocus::FavorRecall),
            ("favorPrecision", ExtractionFocus::FavorPrecision),
        ] {
            let header = format!(r#"{{"options":{{"focus":"{wire}"}}}}"#);
            let request = parse_request(header.as_bytes()).unwrap();
            assert_eq!(options_from(&request.header).focus, expected, "{wire}");
        }
        for (wire, expected) in [
            ("default", HtmlDateMode::Default),
            ("fast", HtmlDateMode::Fast),
            ("extensive", HtmlDateMode::Extensive),
            ("disabled", HtmlDateMode::Disabled),
        ] {
            let header = format!(r#"{{"options":{{"htmlDateMode":"{wire}"}}}}"#);
            let request = parse_request(header.as_bytes()).unwrap();
            assert_eq!(
                options_from(&request.header).html_date_mode,
                expected,
                "{wire}"
            );
        }
    }

    #[test]
    fn config_keys_default_to_core_defaults() {
        let absent = parse_request(br#"{"options":{}}"#).unwrap();
        assert_eq!(options_from(&absent.header).config, None);
        let empty = parse_request(br#"{"options":{"config":{}}}"#).unwrap();
        assert_eq!(options_from(&empty.header).config, Some(Config::default()));
        let partial = parse_request(br#"{"options":{"config":{"minOutputSize":4}}}"#).unwrap();
        assert_eq!(
            options_from(&partial.header).config,
            Some(Config {
                min_output_size: 4,
                ..Config::default()
            })
        );
    }

    #[test]
    fn url_and_absent_url_map_onto_original_url() {
        let absent = parse_request(br#"{}"#).unwrap();
        assert!(options_from(&absent.header).original_url.is_none());
        let empty = parse_request(br#"{"url":""}"#).unwrap();
        assert!(options_from(&empty.header).original_url.is_none());
        let present = parse_request(br#"{"url":"https://example.org/a?b=c"}"#).unwrap();
        assert_eq!(
            options_from(&present.header)
                .original_url
                .as_ref()
                .map(ToString::to_string)
                .as_deref(),
            Some("https://example.org/a?b=c")
        );
    }
}
