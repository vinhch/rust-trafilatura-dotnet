//! The JSON request header contract: parsing, validation and error kinds.
//!
//! This module is the only place that reads and validates the request header: it parses the
//! wire shape into [`Header`]/[`OptionsJson`] and maps core errors onto contract error kinds. It
//! never touches the core's `Options` or builds a response envelope; `engine` owns both.

use crate::engine::Payload;
use serde::Deserialize;
use serde_json::{json, Value};
use trafilatura_rust::{ExtractError, InputLimit, Rejection, TreeLimit};

/// The only request header version this wrapper accepts.
pub const SCHEMA_VERSION: u32 = 2;

/// A content field the caller can ask for, in `responseFields` wire order.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ResponseField {
    ContentText,
    CommentsText,
    Metadata,
}

impl ResponseField {
    /// Wire name; `None` means the name is not part of schema version 2.
    fn parse(name: &str) -> Option<Self> {
        match name {
            "contentText" => Some(Self::ContentText),
            "commentsText" => Some(Self::CommentsText),
            "metadata" => Some(Self::Metadata),
            _ => None,
        }
    }
}

/// Requested fields when `responseFields` is absent.
const DEFAULT_RESPONSE_FIELDS: [ResponseField; 3] = [
    ResponseField::ContentText,
    ResponseField::CommentsText,
    ResponseField::Metadata,
];

/// The request header, schema version 2.
///
/// `url` and `payloadText` live here rather than in `options`; every other option is a field
/// of [`OptionsJson`]. Unknown keys are rejected at every level.
#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
pub struct Header {
    pub schema_version: Option<u32>,
    pub url: Option<String>,
    pub options: Option<OptionsJson>,
    pub response_fields: Option<Vec<String>>,
    /// `true` when the payload is UTF-8 text rather than bytes.
    #[serde(rename = "payloadText")]
    pub payload_text: Option<bool>,
}

/// Extraction options as they appear on the wire.
///
/// Every field is optional: an omitted key means "core default", which `engine` applies when it
/// maps this onto the core `Options`/`Config`. The core's own enums carry no serde derives, so
/// the wire spellings are declared here.
#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
pub struct OptionsJson {
    pub target_language: Option<String>,
    pub focus: Option<FocusJson>,
    pub exclude_comments: Option<bool>,
    pub exclude_tables: Option<bool>,
    pub include_images: Option<bool>,
    pub include_links: Option<bool>,
    pub blacklisted_authors: Option<Vec<String>>,
    pub has_essential_metadata: Option<bool>,
    pub max_tree_size: Option<i64>,
    pub html_date_mode: Option<HtmlDateModeJson>,
    pub config: Option<ConfigJson>,
}

/// Wire spellings of the core `Focus`, in camelCase.
#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq)]
#[serde(rename_all = "camelCase")]
pub enum FocusJson {
    Balanced,
    FavorRecall,
    FavorPrecision,
}

/// Wire spellings of `htmlDateMode`, in camelCase. The core has no such enum: it reads two of the
/// three values from `Config::extensive_date_search`.
#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq)]
#[serde(rename_all = "camelCase")]
pub enum HtmlDateModeJson {
    Default,
    Fast,
    Extensive,
}

/// The `options.config` sub-object; each omitted key takes the core default.
#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
pub struct ConfigJson {
    pub min_extracted_size: Option<i64>,
    pub min_output_size: Option<i64>,
    pub min_output_comment_size: Option<i64>,
}

/// A validated request header plus the fields to return.
#[derive(Clone, Debug)]
pub struct Request {
    pub header: Header,
    pub fields: Vec<ResponseField>,
}

/// Why a request header was rejected before the core ran.
///
/// `InvalidRequest` carries the reason for the ABI's `{"reason": ...}` context.
#[derive(Debug, Eq, PartialEq)]
pub enum RequestError {
    InvalidRequest(String),
}

/// Parses and validates a request header.
///
/// A missing (empty slice), non-UTF-8, malformed or non-object header is an `InvalidRequest`,
/// as are unknown keys, a `schemaVersion` other than [`SCHEMA_VERSION`], and an empty or unknown
/// `responseFields` entry.
pub fn parse_request(header: &[u8]) -> Result<Request, RequestError> {
    let text = std::str::from_utf8(header)
        .map_err(|error| RequestError::InvalidRequest(format!("header is not UTF-8: {error}")))?;
    let header: Header = serde_json::from_str(text)
        .map_err(|error| RequestError::InvalidRequest(format!("invalid header: {error}")))?;

    if let Some(version) = header.schema_version {
        if version != SCHEMA_VERSION {
            return Err(RequestError::InvalidRequest(format!(
                "unsupported schemaVersion {version}, expected {SCHEMA_VERSION}"
            )));
        }
    }

    let fields = match &header.response_fields {
        None => DEFAULT_RESPONSE_FIELDS.to_vec(),
        Some(names) if names.is_empty() => {
            return Err(RequestError::InvalidRequest(
                "responseFields must not be empty".to_owned(),
            ))
        }
        Some(names) => names
            .iter()
            .map(|name| {
                ResponseField::parse(name).ok_or_else(|| {
                    RequestError::InvalidRequest(format!("unknown response field {name:?}"))
                })
            })
            .collect::<Result<Vec<_>, _>>()?,
    };

    Ok(Request { header, fields })
}

/// The payload as the header names it: text when `payloadText` is `true`, otherwise the raw bytes.
///
/// The payload is never echoed in the error, which names only the rule that failed.
pub fn payload<'a>(header: &Header, bytes: &'a [u8]) -> Result<Payload<'a>, RequestError> {
    if header.payload_text != Some(true) {
        return Ok(Payload::Bytes(bytes));
    }
    let text = std::str::from_utf8(bytes).map_err(|_| {
        RequestError::InvalidRequest(
            "payloadText is set but the payload is not valid UTF-8".to_owned(),
        )
    })?;
    Ok(Payload::Text(text))
}

/// Maps a core error onto its `kind` and optional `context`.
///
/// The `message` of an error object is not built here: the caller fills it from the error's
/// `Display`, which is where the core's payloads are folded in.
pub fn error_kind(error: &ExtractError) -> (&'static str, Option<Value>) {
    match error {
        ExtractError::Rejected(rejection) => rejection_kind(*rejection),
        // No wire kind for these yet.
        ExtractError::Cancelled | ExtractError::DeadlineExceeded => ("Unknown", None),
        _ => ("Unknown", None),
    }
}

fn rejection_kind(rejection: Rejection) -> (&'static str, Option<Value>) {
    match rejection {
        Rejection::EmptyTree => ("MissingDocument", None),
        Rejection::WrongHtmlLang => ("HtmlLanguage", None),
        // The core's language classifier reports no language in this version, so it never raises this.
        Rejection::WrongLanguage => ("WrongLanguage", None),
        // The wrapper never sets `url_blacklist`, so the core does not raise this.
        Rejection::BlacklistedUrl => ("BlacklistedUrl", None),
        // `Call::default()` carries no dedup cache, and without one no text is flagged.
        Rejection::Duplicate => ("DuplicateBody", None),
        Rejection::InvalidValue => ("InvalidValue", None),
        Rejection::MissingMetadata(missing) => (
            "MissingMetadata",
            Some(json!({ "missing": missing.names().collect::<Vec<_>>() })),
        ),
        Rejection::TooShort {
            text_chars,
            comments_chars,
        } => (
            "OutputTooShort",
            Some(json!({ "textChars": text_chars, "commentsChars": comments_chars })),
        ),
        Rejection::TreeTooLarge(limit) => ("OutputTreeTooLong", Some(tree_limit_context(limit))),
        Rejection::InputTooLarge(limit) => ("InputTooLarge", Some(input_limit_context(limit))),
        _ => ("Unknown", None),
    }
}

fn tree_limit_context(limit: TreeLimit) -> Value {
    match limit {
        TreeLimit::MaxNodes => json!({ "limit": "maxNodes" }),
        TreeLimit::TagAttributes => json!({ "limit": "tagAttributes" }),
        TreeLimit::MaxTreeSize { children } => {
            json!({ "limit": "maxTreeSize", "children": children })
        }
        TreeLimit::TableCells => json!({ "limit": "tableCells" }),
        _ => json!({ "limit": "unknown" }),
    }
}

fn input_limit_context(limit: InputLimit) -> Value {
    match limit {
        InputLimit::MaxInputBytes => json!({ "limit": "maxInputBytes" }),
        InputLimit::MaxFileSize => json!({ "limit": "maxFileSize" }),
        InputLimit::JsonSize => json!({ "limit": "jsonSize" }),
        _ => json!({ "limit": "unknown" }),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn default_response_fields_are_text_comments_and_metadata() {
        let request = parse_request(br#"{}"#).unwrap();
        assert_eq!(
            request.fields,
            vec![
                ResponseField::ContentText,
                ResponseField::CommentsText,
                ResponseField::Metadata
            ]
        );
    }

    #[test]
    fn non_object_header_is_invalid_request() {
        for header in [&b"[]"[..], b"\"x\"", b"null", b"123"] {
            assert!(matches!(
                parse_request(header),
                Err(RequestError::InvalidRequest(_))
            ));
        }
    }

    #[test]
    fn request_errors_name_their_cause() {
        assert!(matches!(
            parse_request(b"{"),
            Err(RequestError::InvalidRequest(reason)) if !reason.is_empty()
        ));
        assert!(matches!(
            parse_request(&[0xff, 0xfe]),
            Err(RequestError::InvalidRequest(reason)) if reason.contains("UTF-8")
        ));
    }

    #[test]
    fn missing_empty_and_malformed_headers_are_invalid_request() {
        for header in [
            &b""[..],
            &b"{} trailing"[..],
            &b"{\"schemaVersion\":}"[..],
            &b"{\"url\":5}"[..],
            &b"{\"options\":7}"[..],
            &b"{\"options\":{\"focus\":\"unknown\"}}"[..],
        ] {
            assert!(
                matches!(parse_request(header), Err(RequestError::InvalidRequest(_))),
                "{header:?} must be rejected"
            );
        }
    }

    #[test]
    fn unknown_key_at_each_level_is_rejected() {
        for (key, header) in [
            ("htmlDate", &br#"{"htmlDate":"2026-01-01"}"#[..]),
            (
                "unknownOption",
                &br#"{"options":{"unknownOption":true}}"#[..],
            ),
            (
                "unknownConfigKey",
                &br#"{"options":{"config":{"unknownConfigKey":1}}}"#[..],
            ),
        ] {
            match parse_request(header) {
                Err(RequestError::InvalidRequest(reason)) => {
                    assert!(reason.contains(key), "{reason:?} must name {key:?}")
                }
                other => panic!("{header:?} must be an InvalidRequest, got {other:?}"),
            }
        }
    }

    #[test]
    fn payload_is_never_base64() {
        for key in ["htmlBase64", "html"] {
            let header = format!(r#"{{"{key}":"PGh0bWw+"}}"#);
            match parse_request(header.as_bytes()) {
                Err(RequestError::InvalidRequest(reason)) => {
                    assert!(reason.contains(key), "{reason:?} must name {key:?}")
                }
                other => panic!("{header} must be an InvalidRequest, got {other:?}"),
            }
        }
    }

    #[test]
    fn schema_version_other_than_two_is_rejected() {
        assert!(parse_request(br#"{"schemaVersion":2}"#).is_ok());
        for header in [
            &br#"{"schemaVersion":0}"#[..],
            &br#"{"schemaVersion":3}"#[..],
            &br#"{"schemaVersion":"2"}"#[..],
            &br#"{"schemaVersion":3.0}"#[..],
            &br#"{"schemaVersion":-1}"#[..],
        ] {
            assert!(
                matches!(parse_request(header), Err(RequestError::InvalidRequest(_))),
                "{header:?} must be rejected"
            );
        }
    }

    #[test]
    fn unknown_or_empty_response_fields_is_rejected() {
        for header in [
            &br#"{"responseFields":[]}"#[..],
            &br#"{"responseFields":["contentText","markdown"]}"#[..],
            &br#"{"responseFields":["contentText",3]}"#[..],
        ] {
            assert!(
                matches!(parse_request(header), Err(RequestError::InvalidRequest(_))),
                "{header:?} must be rejected"
            );
        }
        // The two causes do not read the same way: the reason names the entry it refused, so a
        // caller that misspelled a field is told which one, and an empty list is told what it is.
        assert!(matches!(
            parse_request(br#"{"responseFields":["markdown"]}"#),
            Err(RequestError::InvalidRequest(reason)) if reason.contains("markdown")
        ));
        assert!(matches!(
            parse_request(br#"{"responseFields":[]}"#),
            Err(RequestError::InvalidRequest(reason)) if reason.contains("must not be empty")
        ));
        let request =
            parse_request(br#"{"responseFields":["metadata","commentsText","contentText"]}"#)
                .unwrap();
        assert_eq!(
            request.fields,
            vec![
                ResponseField::Metadata,
                ResponseField::CommentsText,
                ResponseField::ContentText
            ]
        );
    }

    #[test]
    fn html_response_fields_are_unknown() {
        for header in [
            &br#"{"responseFields":["contentHtml"]}"#[..],
            &br#"{"responseFields":["commentsHtml"]}"#[..],
        ] {
            assert!(
                matches!(parse_request(header), Err(RequestError::InvalidRequest(_))),
                "{} must be an InvalidRequest",
                String::from_utf8_lossy(header)
            );
        }
    }

    #[test]
    fn payload_text_selects_the_path_only_when_true() {
        let invalid = [0xff, 0xfe];
        for header in [
            &br#"{}"#[..],
            br#"{"payloadText":false}"#,
            br#"{"payloadText":null}"#,
        ] {
            let request = parse_request(header).unwrap();
            assert!(
                matches!(payload(&request.header, &invalid), Ok(Payload::Bytes(bytes)) if bytes == invalid),
                "{header:?} must keep the byte path, whatever the bytes are"
            );
        }
        let request = parse_request(br#"{"payloadText":true}"#).unwrap();
        assert!(matches!(
            payload(&request.header, b"<p>x</p>"),
            Ok(Payload::Text("<p>x</p>"))
        ));
    }

    #[test]
    fn payload_text_rejects_invalid_utf8_without_echoing_it() {
        let request = parse_request(br#"{"payloadText":true}"#).unwrap();
        assert!(matches!(
            payload(&request.header, &[0xff, 0xfe]),
            Err(RequestError::InvalidRequest(reason))
                if reason == "payloadText is set but the payload is not valid UTF-8"
        ));
    }

    #[test]
    fn non_boolean_payload_text_is_invalid_request() {
        for header in [
            &br#"{"payloadText":"true"}"#[..],
            &br#"{"payloadText":1}"#[..],
        ] {
            assert!(
                matches!(parse_request(header), Err(RequestError::InvalidRequest(_))),
                "{header:?} must be rejected"
            );
        }
    }

    #[test]
    fn empty_url_means_no_url() {
        let request = parse_request(br#"{"url":""}"#).unwrap();
        assert_eq!(request.header.url.as_deref(), Some(""));
    }

    #[test]
    fn options_round_trip_every_wire_key() {
        let header = br#"{
            "schemaVersion": 2,
            "url": "https://example.org/article",
            "options": {
                "targetLanguage": "en",
                "focus": "favorRecall",
                "excludeComments": true,
                "excludeTables": true,
                "includeImages": true,
                "includeLinks": true,
                "blacklistedAuthors": ["a", "b"],
                "hasEssentialMetadata": true,
                "maxTreeSize": 1050,
                "htmlDateMode": "extensive",
                "config": {
                    "minExtractedSize": 300,
                    "minOutputSize": 4,
                    "minOutputCommentSize": 5
                }
            }
        }"#;
        let request = parse_request(header).unwrap();
        assert_eq!(request.header.schema_version, Some(SCHEMA_VERSION));
        let options = request.header.options.unwrap();
        assert_eq!(options.target_language.as_deref(), Some("en"));
        assert_eq!(options.focus, Some(FocusJson::FavorRecall));
        assert_eq!(options.exclude_comments, Some(true));
        assert_eq!(options.exclude_tables, Some(true));
        assert_eq!(options.include_images, Some(true));
        assert_eq!(options.include_links, Some(true));
        assert_eq!(
            options.blacklisted_authors,
            Some(vec!["a".to_owned(), "b".to_owned()])
        );
        assert_eq!(options.has_essential_metadata, Some(true));
        assert_eq!(options.max_tree_size, Some(1050));
        assert_eq!(options.html_date_mode, Some(HtmlDateModeJson::Extensive));
        let config = options.config.unwrap();
        assert_eq!(config.min_extracted_size, Some(300));
        assert_eq!(config.min_output_size, Some(4));
        assert_eq!(config.min_output_comment_size, Some(5));
    }

    #[test]
    fn removed_option_keys_are_unknown() {
        for header in [
            &br#"{"inputEncoding":"utf-8"}"#[..],
            &br#"{"options":{"inputEncoding":"windows-1252"}}"#[..],
            &br#"{"options":{"enableFallback":true}}"#[..],
            &br#"{"options":{"deduplicate":true}}"#[..],
            &br#"{"options":{"pruneSelector":"div"}}"#[..],
            &br#"{"options":{"htmlDateMode":"disabled"}}"#[..],
            &br#"{"options":{"config":{"cacheSize":1}}}"#[..],
            &br#"{"options":{"config":{"maxDuplicateCount":1}}}"#[..],
            &br#"{"options":{"config":{"minDuplicateCheckSize":1}}}"#[..],
            &br#"{"options":{"config":{"minExtractedCommentSize":1}}}"#[..],
        ] {
            assert!(
                matches!(parse_request(header), Err(RequestError::InvalidRequest(_))),
                "{} must be an InvalidRequest",
                String::from_utf8_lossy(header)
            );
        }
    }

    #[test]
    fn url_is_rejected_inside_options() {
        assert!(matches!(
            parse_request(br#"{"options":{"url":"https://example.org/a"}}"#),
            Err(RequestError::InvalidRequest(reason)) if reason.contains("url")
        ));
    }

    #[test]
    fn focus_and_html_date_mode_reject_unknown_spellings() {
        for header in [
            &br#"{"options":{"focus":"favor_recall"}}"#[..],
            &br#"{"options":{"focus":"recall"}}"#[..],
            &br#"{"options":{"htmlDateMode":"defaultMode"}}"#[..],
            &br#"{"options":{"htmlDateMode":"none"}}"#[..],
        ] {
            assert!(
                matches!(parse_request(header), Err(RequestError::InvalidRequest(_))),
                "{header:?} must be rejected"
            );
        }
        assert_eq!(
            parse_request(br#"{"options":{"focus":"balanced","htmlDateMode":"fast"}}"#)
                .unwrap()
                .header
                .options
                .unwrap()
                .focus,
            Some(FocusJson::Balanced)
        );
    }

    #[test]
    fn time_budget_key_is_an_unknown_header_key() {
        assert!(matches!(
            parse_request(br#"{"timeBudgetMs":100}"#),
            Err(RequestError::InvalidRequest(_))
        ));
    }

    #[test]
    fn error_kind_covers_every_core_variant() {
        let cases = [
            (
                ExtractError::Rejected(Rejection::EmptyTree),
                "MissingDocument",
                None,
            ),
            (
                ExtractError::Rejected(Rejection::WrongHtmlLang),
                "HtmlLanguage",
                None,
            ),
            (
                ExtractError::Rejected(Rejection::WrongLanguage),
                "WrongLanguage",
                None,
            ),
            (
                ExtractError::Rejected(Rejection::Duplicate),
                "DuplicateBody",
                None,
            ),
            (
                ExtractError::Rejected(Rejection::BlacklistedUrl),
                "BlacklistedUrl",
                None,
            ),
            (
                ExtractError::Rejected(Rejection::InvalidValue),
                "InvalidValue",
                None,
            ),
            (
                ExtractError::Rejected(Rejection::TooShort {
                    text_chars: 3,
                    comments_chars: 4,
                }),
                "OutputTooShort",
                Some(json!({"textChars": 3, "commentsChars": 4})),
            ),
            (
                ExtractError::Rejected(Rejection::TreeTooLarge(TreeLimit::MaxTreeSize {
                    children: 9,
                })),
                "OutputTreeTooLong",
                Some(json!({"limit": "maxTreeSize", "children": 9})),
            ),
            (
                ExtractError::Rejected(Rejection::TreeTooLarge(TreeLimit::MaxNodes)),
                "OutputTreeTooLong",
                Some(json!({"limit": "maxNodes"})),
            ),
            (
                ExtractError::Rejected(Rejection::TreeTooLarge(TreeLimit::TagAttributes)),
                "OutputTreeTooLong",
                Some(json!({"limit": "tagAttributes"})),
            ),
            (
                ExtractError::Rejected(Rejection::TreeTooLarge(TreeLimit::TableCells)),
                "OutputTreeTooLong",
                Some(json!({"limit": "tableCells"})),
            ),
            (
                ExtractError::Rejected(Rejection::InputTooLarge(InputLimit::MaxInputBytes)),
                "InputTooLarge",
                Some(json!({"limit": "maxInputBytes"})),
            ),
            (
                ExtractError::Rejected(Rejection::InputTooLarge(InputLimit::MaxFileSize)),
                "InputTooLarge",
                Some(json!({"limit": "maxFileSize"})),
            ),
            (
                ExtractError::Rejected(Rejection::InputTooLarge(InputLimit::JsonSize)),
                "InputTooLarge",
                Some(json!({"limit": "jsonSize"})),
            ),
            (ExtractError::Cancelled, "Unknown", None),
            (ExtractError::DeadlineExceeded, "Unknown", None),
        ];
        for (error, kind, context) in cases {
            let (actual_kind, actual_context) = error_kind(&error);
            assert_eq!(actual_kind, kind, "{error:?}");
            assert_eq!(actual_context, context, "{error:?}");
        }
    }
}
