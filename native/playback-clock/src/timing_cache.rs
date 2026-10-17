//! VSM's immutable render snapshot is hashed before the DSE render core runs.
//! Route that snapshot to its source Part only during cache lookup. Cache
//! publication hashes the live Part directly; both paths use the same digest.
use super::*;
use std::cell::Cell;
use std::sync::atomic::AtomicBool;

type Prepare = unsafe extern "system" fn(*mut u8, *mut c_void, *mut c_void) -> *mut c_void;
type Hash = unsafe extern "system" fn(*mut c_void, *mut u8, *mut c_void, *mut c_void) -> *mut u8;
static PREPARE: AtomicPtr<c_void> = AtomicPtr::new(ptr::null_mut());
static HASH: AtomicPtr<c_void> = AtomicPtr::new(ptr::null_mut());
static INSTALL: Mutex<()> = Mutex::new(());
static READY: AtomicBool = AtomicBool::new(false);
thread_local! { static LOOKUP: Cell<(u64, u64)> = const { Cell::new((0, 0)) }; }

#[link(name = "bcrypt")]
unsafe extern "system" {
    fn BCryptOpenAlgorithmProvider(handle: *mut *mut c_void, algorithm: *const u16, implementation: *const u16, flags: u32) -> i32;
    fn BCryptHash(handle: *mut c_void, secret: *const u8, secret_size: u32, input: *const u8, input_size: u32, output: *mut u8, output_size: u32) -> i32;
}
#[link(name = "kernel32")]
unsafe extern "system" { fn GetModuleHandleW(name: *const u16) -> *mut c_void; }

pub fn digest(input: &[u8]) -> Result<[u8; 32], i32> {
    // Windows owns this process-lifetime SHA256 provider. No mutable hash state
    // is shared between concurrent renders.
    static PROVIDER: OnceLock<usize> = OnceLock::new();
    let provider = *PROVIDER.get_or_init(|| {
        let mut handle = ptr::null_mut();
        let name: Vec<u16> = "SHA256\0".encode_utf16().collect();
        if unsafe { BCryptOpenAlgorithmProvider(&mut handle, name.as_ptr(), ptr::null(), 0) } < 0 { 0 } else { handle as usize }
    });
    if provider == 0 || input.len() > u32::MAX as usize { return Err(-6); }
    let mut output = [0u8; 32];
    if unsafe { BCryptHash(provider as *mut c_void, ptr::null(), 0, input.as_ptr(), input.len() as u32, output.as_mut_ptr(), 32) } < 0 {
        return Err(-6);
    }
    Ok(output)
}

pub fn fingerprint(notes: &[super::sampled_timing::Note], warp: Option<&super::phoneme_timing::Warp>) -> Result<[u8; 32], i32> {
    // Deliberately exclude pointers, epochs and cursors. Identical controls
    // across undo, reload and processes must address the same native cache.
    let mut input = b"VOCALOIDPatcher.sampled-timing.cache.v3\0".to_vec();
    input.extend_from_slice(&(notes.len() as u32).to_le_bytes());
    for note in notes {
        input.extend_from_slice(&note.begin.to_le_bytes());
        input.extend_from_slice(&note.end.to_le_bytes());
        input.extend_from_slice(&note.pitch.to_le_bytes());
        input.extend_from_slice(&note.coefficient.to_le_bytes());
    }
    let points = warp.map_or(&[][..], |w| w.points.as_ref());
    input.extend_from_slice(&(points.len() as u32).to_le_bytes());
    for point in points {
        input.extend_from_slice(&point.from.to_le_bytes());
        input.extend_from_slice(&point.to.to_le_bytes());
    }
    digest(&input)
}

unsafe extern "system" fn prepare(renderer: *mut u8, output: *mut c_void, argument: *mut c_void) -> *mut c_void {
    let original: Prepare = unsafe { std::mem::transmute(PREPARE.load(Ordering::Acquire)) };
    if renderer.is_null() { return unsafe { original(renderer, output, argument) }; }
    let live = unsafe { ptr::read_unaligned(renderer.add(super::timing_profile::RendererLivePart).cast::<u64>()) };
    let snapshot = unsafe { ptr::read_unaligned(renderer.add(super::timing_profile::RendererSnapshotPart).cast::<u64>()) };
    struct Restore((u64, u64));
    impl Drop for Restore { fn drop(&mut self) { LOOKUP.with(|slot| slot.set(self.0)); } }
    let _restore = Restore(LOOKUP.with(|slot| slot.replace((snapshot, live))));
    unsafe { original(renderer, output, argument) }
}

unsafe extern "system" fn hash(sequence: *mut c_void, output: *mut u8, part: *mut c_void, tempo: *mut c_void) -> *mut u8 {
    let original: Hash = unsafe { std::mem::transmute(HASH.load(Ordering::Acquire)) };
    let result = unsafe { original(sequence, output, part, tempo) };
    if output.is_null() || part.is_null() { return result; }
    let pointer = part as usize as u64;
    let source = LOOKUP.with(|slot| { let (snapshot, live) = slot.get(); if snapshot == pointer { live } else { pointer } });
    let native = unsafe { ptr::read_unaligned(output.cast::<[u8; 16]>()) };
    let native = super::sampled_timing::cache_key(source, native).unwrap_or(native);
    if let Some(key) = super::duration_selection::cache_key(source, native) {
        unsafe { ptr::write_unaligned(output.cast::<[u8; 16]>(), key); }
    } else { unsafe { ptr::write_unaligned(output.cast::<[u8;16]>(),native); } }
    result
}

pub fn install() -> i32 {
    if !super::timing_profile::selected() { return -30; }
    let _guard = INSTALL.lock().unwrap_or_else(|e| e.into_inner());
    if READY.load(Ordering::Acquire) { return 0; }
    let name: Vec<u16> = "VSM.dll\0".encode_utf16().collect();
    let module = unsafe { GetModuleHandleW(name.as_ptr()) }.cast::<u8>();
    if module.is_null() { return -3; }
    // Verified VSM SHA256 is also required by the managed initialization gate.
    // Both complete instruction prefixes contain no PC-relative instructions.
    let prefixes: [(usize, &[u8], *const c_void, &AtomicPtr<c_void>); 2] = [
        (super::timing_profile::vsm_prepare(), &[0x48,0x89,0x5c,0x24,0x18,0x55,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57], prepare as *const c_void, &PREPARE),
        (super::timing_profile::vsm_cache_hash(), &[0x40,0x55,0x53,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57,0x48,0x8d,0x6c,0x24,0xe1], hash as *const c_void, &HASH),
    ];
    for (offset, prefix, _, original) in &prefixes {
        if original.load(Ordering::Acquire).is_null() && unsafe { slice::from_raw_parts(module.add(*offset), prefix.len()) } != *prefix { return -3; }
    }
    for (offset, prefix, replacement, original) in prefixes {
        if original.load(Ordering::Acquire).is_null() {
            if let Err(code) = unsafe { super::register_shift_hook::install_hook(module.add(offset), prefix.len(), replacement, original) } { return code; }
        }
    }
    READY.store(true, Ordering::Release);
    0
}

#[cfg(test)] mod tests {
    use super::*;
    use super::super::{phoneme_timing::{Point, Warp}, sampled_timing::Note};
    #[test] fn cache_keys_are_stable_and_include_every_numeric_control() {
        assert_eq!(digest(b"abc").unwrap(), [0xba,0x78,0x16,0xbf,0x8f,0x01,0xcf,0xea,0x41,0x41,0x40,0xde,0x5d,0xae,0x22,0x23,0xb0,0x03,0x61,0xa3,0x96,0x17,0x7a,0x9c,0xb4,0x10,0xff,0x61,0xf2,0x00,0x15,0xad]);
        let note = Note { begin: 100, end: 200, pitch: -900.0, coefficient: 0.25 };
        let a = fingerprint(&[note], None).unwrap();
        assert_eq!(a, fingerprint(&[note], None).unwrap());
        for changed in [Note{begin:101,..note}, Note{end:201,..note}, Note{pitch:-800.0,..note}, Note{coefficient:0.251,..note}] {
            assert_ne!(a, fingerprint(&[changed], None).unwrap());
        }
        let warp = Warp::new(&[Point{from:0,to:0}, Point{from:100,to:101}, Point{from:200,to:200}]).unwrap();
        assert_ne!(a, fingerprint(&[note], Some(&warp)).unwrap());
        assert_eq!(a, fingerprint(&[note], None).unwrap());
    }
}
