//! A cap environment value above `u32::MAX`, in a file of its own.
//!
//! Setting the variable poisons the process for every other cap test, which is why this case
//! owns a binary. No `test-hooks` symbol is used, so the binary runs in every suite.

use rust_trafilatura_ffi::{rt_set_worker_cap, rt_worker_cap};

#[test]
fn env_value_above_u32max_saturates_in_the_accessor() {
    std::env::set_var("RUSTTRAFILATURA_MAX_RUNAWAY_WORKERS", "4294967296");
    assert_eq!(rt_worker_cap(), u32::MAX);

    // The saturated read fixed nothing, so a set still applies and the accessor reports the
    // applied value.
    assert_eq!(rt_set_worker_cap(4), 0);
    assert_eq!(rt_worker_cap(), 4);
}
