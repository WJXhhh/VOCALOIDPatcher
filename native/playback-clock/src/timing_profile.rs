// Generated from the same embedded profile consumed by the managed adapter.
// It identifies private Yamaha layout, independently of our public clock ABI.
#![allow(non_upper_case_globals, dead_code)]
include!(concat!(env!("OUT_DIR"), "/timing_profile.rs"));

use std::{ffi::c_void, sync::atomic::{AtomicUsize, Ordering}};
static SELECTED: AtomicUsize = AtomicUsize::new(0);

unsafe extern "system" { fn GetModuleHandleW(name: *const u16) -> *mut c_void; }

#[unsafe(no_mangle)]
pub extern "C" fn v6_timing_profile_abi_version() -> u32 { 2 }

#[unsafe(no_mangle)]
pub extern "C" fn v6_timing_profile_fingerprint() -> u64 {
    active().map_or(0, |profile| profile.0)
}

#[unsafe(no_mangle)]
pub extern "C" fn v6_timing_profile_supported(fingerprint: u64) -> i32 {
    i32::from(PROFILES.iter().any(|profile| profile.0 == fingerprint))
}

fn active() -> Option<&'static (u64, usize, usize)> {
    SELECTED.load(Ordering::Acquire).checked_sub(1).and_then(|index| PROFILES.get(index))
}
pub(super) fn vsm_prepare() -> usize { active().map_or(0, |profile| profile.1) }
pub(super) fn vsm_cache_hash() -> usize { active().map_or(0, |profile| profile.2) }

// Hash qualification belongs to the managed loader. This handshake verifies
// that its selected adapter and module handles are the ones used by our hooks.
#[unsafe(no_mangle)]
pub extern "C" fn v6_timing_select_profile(fingerprint: u64, dse: usize, vsm: usize) -> i32 {
    let Some(index) = PROFILES.iter().position(|profile| profile.0 == fingerprint) else { return -1; };
    if dse == 0 || vsm == 0 { return -1; }
    let dse_name: Vec<u16> = "DSE.dll\0".encode_utf16().collect();
    let vsm_name: Vec<u16> = "VSM.dll\0".encode_utf16().collect();
    if unsafe { GetModuleHandleW(dse_name.as_ptr()) } as usize != dse ||
        unsafe { GetModuleHandleW(vsm_name.as_ptr()) } as usize != vsm { return -2; }
    // Detours are process-lifetime entries. Never switch their layout after
    // selection, including while another thread is preparing an installation.
    match SELECTED.compare_exchange(0, index + 1, Ordering::AcqRel, Ordering::Acquire) {
        Ok(_) => 0,
        Err(selected) if selected == index + 1 => 0,
        Err(_) => -3,
    }
}

pub(super) fn selected() -> bool { active().is_some() }

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn unselected_or_mismatched_profiles_cannot_install_private_hooks() {
        assert_eq!(v6_timing_select_profile(0, 0, 0), -1);
        assert_eq!(v6_timing_select_profile(FINGERPRINT, 1, 1), -2);
        assert_eq!(PROFILES.len(), 2);
        assert_ne!(PROFILES[0].0, PROFILES[1].0);
        for profile in PROFILES { assert_eq!(v6_timing_profile_supported(profile.0), 1); }
        assert_eq!(v6_timing_profile_supported(0), 0);
        assert!(!selected());
        assert_eq!(super::super::sampled_timing::v6_timing_install(), -30);
        assert_eq!(unsafe { super::super::phoneme_timing::v6_phoneme_install(std::ptr::null_mut()) }, -30);
        assert_eq!(unsafe { super::super::duration_selection::v6_duration_install(std::ptr::null_mut()) }, -30);
    }
}
