//! The JSON request header contract: parsing, validation and error kinds.
//!
//! This module is the only place that reads and validates the request header: it parses the
//! wire shape into [`Header`]/[`OptionsJson`] and maps core errors onto contract error kinds. It
//! never touches the core's `Options` or builds a response envelope; `engine` owns both.

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

/// The page as the request delivers it. The core runs its decompression, charset detection and binary
/// check on `Bytes` only, so `Text` is parsed as it is.
pub enum Payload<'a> {
    Bytes(&'a [u8]),
    Text(&'a str),
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
        ExtractError::Cancelled => ("Cancelled", None),
        // DeadlineExceeded has no wire kind yet: the wrapper sets no deadline.
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
mod tests;
