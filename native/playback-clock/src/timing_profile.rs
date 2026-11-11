// Generated structure contracts and address-table order shared with C#.
#![allow(non_upper_case_globals, non_snake_case, dead_code)]
include!(concat!(env!("OUT_DIR"), "/timing_profile.rs"));

use std::{ffi::c_void, sync::OnceLock};

#[derive(PartialEq, Eq)]
struct Layout { fingerprint: u64, dse: usize, vsm: usize, addresses: Vec<u32> }
static SELECTED: OnceLock<Layout> = OnceLock::new();
unsafe extern "system" { fn GetModuleHandleW(name: *const u16) -> *mut c_void; }

#[unsafe(no_mangle)]
pub extern "C" fn v6_timing_profile_abi_version() -> u32 { 3 }

#[unsafe(no_mangle)]
pub extern "C" fn v6_timing_profile_fingerprint() -> u64 {
    SELECTED.get().map_or(0, |layout| layout.fingerprint)
}

#[unsafe(no_mangle)]
pub extern "C" fn v6_timing_profile_supported(fingerprint: u64) -> i32 {
    i32::from(PROFILES.iter().any(|profile| profile.0 == fingerprint))
}

fn address(index: usize, default: usize) -> usize {
    // Defaults support pure unit tests only; every installer requires selected().
    SELECTED.get().map_or(if cfg!(test) { default } else { 0 }, |layout| layout.addresses[index] as usize)
}
pub(super) fn vsm_prepare() -> usize { VsmPrepare() }
pub(super) fn vsm_cache_hash() -> usize { VsmCacheHash() }

fn validate_addresses(addresses: &[u32], dse_size: u32, vsm_size: u32) -> bool {
    addresses.len() == ADDRESS_COUNT && addresses.iter().enumerate().all(|(index, &rva)|
        rva > 0 && rva < if ADDRESS_IS_VSM[index] { vsm_size } else { dse_size })
}

// Discovery and layout qualification belong to the managed loader. This ABI
// receives the resulting RVAs instead of substituting build-time addresses.
// Selected process-lifetime detours can never change layout or modules.
/// # Safety
/// addresses must point to count readable u32 values for this call only.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn v6_timing_select_layout(fingerprint: u64, dse: usize, vsm: usize,
    addresses: *const u32, count: u32) -> i32 {
    if v6_timing_profile_supported(fingerprint) != 1 || dse == 0 || vsm == 0 ||
        addresses.is_null() || count as usize != ADDRESS_COUNT { return -1; }
    let dse_name: Vec<u16> = "DSE.dll\0".encode_utf16().collect();
    let vsm_name: Vec<u16> = "VSM.dll\0".encode_utf16().collect();
    if unsafe { GetModuleHandleW(dse_name.as_ptr()) } as usize != dse ||
        unsafe { GetModuleHandleW(vsm_name.as_ptr()) } as usize != vsm { return -2; }
    unsafe fn image_size(module: usize) -> Option<u32> {
        let data = module as *const u8;
        if unsafe { data.cast::<u16>().read_unaligned() } != 0x5a4d { return None; }
        let offset = unsafe { data.add(0x3c).cast::<u32>().read_unaligned() } as usize;
        if !(0x40..=0x1000).contains(&offset) { return None; }
        if unsafe { data.add(offset).cast::<u32>().read_unaligned() } != 0x4550 ||
            unsafe { data.add(offset+4).cast::<u16>().read_unaligned() } != 0x8664 ||
            unsafe { data.add(offset+24).cast::<u16>().read_unaligned() } != 0x20b { return None; }
        Some(unsafe { data.add(offset+24+56).cast::<u32>().read_unaligned() })
    }
    let (Some(dse_size), Some(vsm_size)) = (unsafe { image_size(dse) }, unsafe { image_size(vsm) }) else { return -4; };
    let addresses = unsafe { std::slice::from_raw_parts(addresses, count as usize) };
    if !validate_addresses(addresses, dse_size, vsm_size) { return -4; }
    let layout = Layout { fingerprint, dse, vsm, addresses: addresses.to_vec() };
    match SELECTED.set(layout) {
        Ok(()) => 0,
        Err(requested) if SELECTED.get() == Some(&requested) => 0,
        Err(_) => -3,
    }
}

pub(super) fn selected() -> bool { SELECTED.get().is_some() }

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn unselected_or_mismatched_profiles_cannot_install_private_hooks() {
        assert_eq!(unsafe { v6_timing_select_layout(0, 0, 0, std::ptr::null(), 0) }, -1);
        assert_eq!(unsafe { v6_timing_select_layout(FINGERPRINT, 1, 1, DEFAULT_ADDRESSES.as_ptr(), ADDRESS_COUNT as u32) }, -2);
        for profile in PROFILES { assert_eq!(v6_timing_profile_supported(profile.0), 1); }
        assert!(!selected());
        assert_eq!(super::super::sampled_timing::v6_timing_install(), -30);
        assert_eq!(unsafe { super::super::phoneme_timing::v6_phoneme_install(std::ptr::null_mut()) }, -30);
        assert_eq!(unsafe { super::super::duration_selection::v6_duration_install(std::ptr::null_mut()) }, -30);
    }

    #[test]
    fn resolved_address_table_checks_length_and_each_module_boundary() {
        assert!(validate_addresses(DEFAULT_ADDRESSES, 0x1000000, 0x1000000));
        assert!(!validate_addresses(&DEFAULT_ADDRESSES[..ADDRESS_COUNT-1], 0x1000000, 0x1000000));
        for index in 0..ADDRESS_COUNT {
            let mut changed = DEFAULT_ADDRESSES.to_vec();
            changed[index] = 0;
            assert!(!validate_addresses(&changed, 0x1000000, 0x1000000));
            changed[index] = 0x1000000;
            assert!(!validate_addresses(&changed, 0x1000000, 0x1000000));
        }
    }
}
