use serde::{Deserialize, Serialize};
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct Architecture {
    pub native: String,
    pub process: String,
    pub emulated: Option<bool>,
    pub probe: String,
}
fn machine(value: u16) -> &'static str {
    match value {
        0x8664 => "x86_64",
        0xaa64 => "aarch64",
        0x014c => "x86",
        _ => "unknown",
    }
}
/// IsWow64Process2 returns UNKNOWN for a native process, not an unknown native OS.
pub fn decode(result: Option<(u16, u16)>) -> Architecture {
    match result {
        Some((process, native)) if machine(native) != "unknown" => Architecture {
            native: machine(native).into(),
            process: machine(if process == 0 { native } else { process }).into(),
            emulated: if process == 0 {
                Some(false)
            } else if machine(process) != "unknown" {
                Some(process != native)
            } else {
                None
            },
            probe: "is_wow64_process2".into(),
        },
        _ => Architecture {
            native: "unknown".into(),
            process: "unknown".into(),
            emulated: None,
            probe: "is_wow64_process2_failed_or_unknown".into(),
        },
    }
}
pub fn detect() -> Architecture {
    #[cfg(windows)]
    unsafe {
        use windows_sys::Win32::System::Threading::{GetCurrentProcess, IsWow64Process2};
        let (mut process, mut native) = (0, 0);
        decode(
            (IsWow64Process2(GetCurrentProcess(), &mut process, &mut native) != 0)
                .then_some((process, native)),
        )
    }
    #[cfg(not(windows))]
    {
        Architecture {
            native: std::env::consts::ARCH.into(),
            process: std::env::consts::ARCH.into(),
            emulated: Some(false),
            probe: "non_windows_host".into(),
        }
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn native_x64() {
        let a = decode(Some((0, 0x8664)));
        assert_eq!(
            (&*a.native, &*a.process, a.emulated),
            ("x86_64", "x86_64", Some(false))
        );
    }
    #[test]
    fn arm64_x64_emulation() {
        let a = decode(Some((0x8664, 0xaa64)));
        assert_eq!(
            (&*a.native, &*a.process, a.emulated),
            ("aarch64", "x86_64", Some(true))
        );
    }
    #[test]
    fn failure_is_unknown_not_compile_target() {
        for input in [None, Some((0x8664, 0)), Some((0, 0xffff))] {
            let a = decode(input);
            assert_eq!(a.native, "unknown");
            assert_eq!(a.process, "unknown");
            assert_eq!(a.emulated, None);
        }
    }
}
