//! C ABI glue for the published `rust-trafilatura` crate.
//!
//! Modules, added by later tasks:
//!
//! - `contract`: request header and response JSON types, plus validation.
//! - `engine`: core invocation and response assembly.
//!
//! The three exported symbols (`rt_extract`, `rt_free`, `rt_version`) and the crate's
//! only `unsafe` code belong in this file.
