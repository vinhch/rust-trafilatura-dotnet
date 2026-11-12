//! Core invocation and response assembly.
//!
//! [`build`] is the only path from a validated [`Request`] to a [`Response`]: it maps the wire
//! options onto the core's `Options`/`Config`, runs the extraction and fills in exactly the fields
//! the request named. A core error is not returned as `Err`; it lands in [`Response::error`], so
//! success and failure keep one envelope shape.

use crate::contract::{self, ConfigJson, Payload, Request, ResponseField, SCHEMA_VERSION};
use serde::Serialize;
use trafilatura_rust::{
    Call, CancelToken, Config, Document, ExtractError, Focus, Input, InputLimit, Options, Rejection,
};

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

/// Extracts from a payload and assembles the response. Infallible: core errors are mapped onto
/// [`Response::error`]. `max_input_bytes` is the core's input cap, and `None` lifts it. `cancel`, when
/// present, stops the extraction once it is cancelled, which fails with `Cancelled`.
pub fn build(
    request: &Request,
    payload: Payload<'_>,
    max_input_bytes: Option<usize>,
    cancel: Option<&CancelToken>,
) -> Response {
    let extractor = options_from(request, max_input_bytes).build();
    let mut input = match payload {
        Payload::Bytes(bytes) => Input::bytes(bytes),
        Payload::Text(text) => Input::text(text),
    };
    if let Some(url) = request.header.url.as_deref().filter(|url| !url.is_empty()) {
        input = input.url(url);
    }
    let call = cancel.map_or_else(Call::default, |token| Call::default().cancel(token));
    assemble(request, extractor.bare_extraction(&input, &call))
}

/// The response for a payload over the input cap. It is assembled from the core's own
/// `InputTooLarge` error, so the envelope is the one the core writes; the payload is never read.
pub fn too_large(request: &Request) -> Response {
    assemble(
        request,
        Err(ExtractError::Rejected(Rejection::InputTooLarge(
            InputLimit::MaxInputBytes,
        ))),
    )
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
/// extracts metadata, and the response then leaves it out. `max_input_bytes` is the input cap the
/// core applies, and `None` lifts it.
fn options_from(request: &Request, max_input_bytes: Option<usize>) -> Options {
    let mut options = Options::new()
        .with_metadata(request.fields.contains(&ResponseField::Metadata))
        .max_input_bytes(max_input_bytes);
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

#[cfg(test)]
mod tests;
