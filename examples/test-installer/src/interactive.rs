//! Opt-in native installer UI. The root is supplied by the person operating this
//! window, never by Contain's capture configuration. All existing fixture guards
//! run after this dialog and before any truth/target writes.
use anyhow::{Result, ensure};
use std::{cell::RefCell, path::PathBuf, ptr};
use windows_sys::Win32::{
    Foundation::{HWND, LPARAM, LRESULT, WPARAM},
    System::LibraryLoader::GetModuleHandleW,
    UI::WindowsAndMessaging::*,
};

#[derive(Default)]
struct State {
    edit: HWND,
    selected: Option<PathBuf>,
}
thread_local! { static STATE: RefCell<State> = RefCell::new(State::default()); }
fn wide(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(Some(0)).collect()
}

unsafe extern "system" fn procedure(window: HWND, message: u32, w: WPARAM, l: LPARAM) -> LRESULT {
    match message {
        WM_COMMAND if w & 0xffff == 102 => {
            STATE.with(|state| {
                let mut state = state.borrow_mut();
                let mut text = vec![0_u16; 1024];
                // SAFETY: edit is owned by this UI thread and text is writable.
                let count = unsafe { GetWindowTextW(state.edit, text.as_mut_ptr(), 1024) };
                if count > 0 {
                    state.selected = Some(PathBuf::from(String::from_utf16_lossy(
                        &text[..count as usize],
                    )));
                    // SAFETY: window is the live owned dialog.
                    unsafe { DestroyWindow(window) };
                }
            });
            0
        }
        WM_TIMER | WM_CLOSE => {
            // SAFETY: closing only this fixture's own window. Timeout is failure.
            unsafe { DestroyWindow(window) };
            0
        }
        WM_DESTROY => {
            // SAFETY: ends only this thread's message loop.
            unsafe { PostQuitMessage(0) };
            0
        }
        _ => {
            // SAFETY: forwards the unchanged window-message arguments.
            unsafe { DefWindowProcW(window, message, w, l) }
        }
    }
}

pub(crate) fn choose_root() -> Result<PathBuf> {
    STATE.with(|state| *state.borrow_mut() = State::default());
    let class = wide("ContainControlledInstaller");
    // SAFETY: all strings and window structures live across each synchronous
    // call; callbacks run on this thread, and child handles belong to the window.
    unsafe {
        let instance = GetModuleHandleW(ptr::null());
        let class_spec = WNDCLASSW {
            lpfnWndProc: Some(procedure),
            hInstance: instance,
            hbrBackground: 6_usize as _, // COLOR_WINDOW + 1; system-owned brush.
            lpszClassName: class.as_ptr(),
            ..Default::default()
        };
        ensure!(
            RegisterClassW(&class_spec) != 0,
            "fixture window class failed"
        );
        let window = CreateWindowExW(
            WS_EX_TOPMOST,
            class.as_ptr(),
            wide("Contain controlled installer").as_ptr(),
            WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_VISIBLE,
            100,
            100,
            680,
            230,
            ptr::null_mut(),
            ptr::null_mut(),
            instance,
            ptr::null(),
        );
        ensure!(!window.is_null(), "fixture installer window failed");
        CreateWindowExW(
            0,
            wide("STATIC").as_ptr(),
            wide("Choose a marked disposable destination, then click Install.").as_ptr(),
            WS_CHILD | WS_VISIBLE,
            20,
            20,
            620,
            25,
            window,
            ptr::null_mut(),
            instance,
            ptr::null(),
        );
        let edit = CreateWindowExW(
            WS_EX_CLIENTEDGE,
            wide("EDIT").as_ptr(),
            wide("").as_ptr(),
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL as u32,
            20,
            60,
            620,
            30,
            window,
            101_usize as _,
            instance,
            ptr::null(),
        );
        let button = CreateWindowExW(
            0,
            wide("BUTTON").as_ptr(),
            wide("Install").as_ptr(),
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_DEFPUSHBUTTON as u32,
            520,
            115,
            120,
            35,
            window,
            102_usize as _,
            instance,
            ptr::null(),
        );
        ensure!(
            !edit.is_null() && !button.is_null(),
            "fixture installer controls failed"
        );
        STATE.with(|state| state.borrow_mut().edit = edit);
        ensure!(
            SetTimer(window, 1, 120_000, None) != 0,
            "fixture deadline timer failed"
        );
        SetForegroundWindow(window);
        let mut message = MSG::default();
        loop {
            let result = GetMessageW(&mut message, ptr::null_mut(), 0, 0);
            ensure!(result >= 0, "fixture message loop failed");
            if result == 0 {
                break;
            }
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        UnregisterClassW(class.as_ptr(), instance);
    }
    STATE
        .with(|state| state.borrow_mut().selected.take())
        .ok_or_else(|| {
            anyhow::anyhow!("interactive fixture cancelled or reached its two-minute deadline")
        })
}
