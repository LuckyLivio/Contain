#![no_main]
use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| contain_fuzz::completion_correlation(data));
