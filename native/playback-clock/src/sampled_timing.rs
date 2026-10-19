//! Immutable controls captured at the VSM render-core boundary. No process-wide
//! NOTE ordinal: every parser binding owns one Part/epoch snapshot.
use super::*;
use std::cell::RefCell;

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct Note {
    pub begin: i64,
    pub end: i64,
    pub pitch: f32,
    pub coefficient: f32, // -1 means retain the native decoded VEL
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct Status {
    pub epoch: u64,
    pub scopes: u64,
    pub applied: u64,
    pub failures: u64,
    pub cache_keys: u64,
}

struct Entry {
    epoch: u64,
    notes: Arc<[Note]>,
    scopes: AtomicU64,
    applied: AtomicU64,
    failures: AtomicU64,
    cache_keys: AtomicU64,
    fingerprint: [u8; 32],
    warp: Option<Arc<super::phoneme_timing::Warp>>,
    gains: Option<Arc<super::transition_gain::Controls>>,
}
#[derive(Clone)]
struct Binding { part: u64, entry: Arc<Entry>, outer: u64, schedule: usize, scope: Arc<()>, warp: Option<Arc<super::phoneme_timing::Context>> }
static PARTS: OnceLock<RwLock<HashMap<u64, Arc<Entry>>>> = OnceLock::new();
static PARSERS: OnceLock<RwLock<HashMap<usize, Binding>>> = OnceLock::new();
static ACTIVE_SCOPES: AtomicU64 = AtomicU64::new(0);
static PEAK_SCOPES: AtomicU64 = AtomicU64::new(0);
static SCHEDULE_REUSES: AtomicU64 = AtomicU64::new(0);
thread_local! { static ACTIVE: RefCell<Option<Binding>> = const { RefCell::new(None) }; }
fn parts() -> &'static RwLock<HashMap<u64, Arc<Entry>>> { PARTS.get_or_init(Default::default) }
fn parsers() -> &'static RwLock<HashMap<usize, Binding>> { PARSERS.get_or_init(Default::default) }

pub fn set(part: u64, epoch: u64, notes: &[Note]) -> i32 {
    set_warp(part, epoch, notes, None)
}

fn set_warp(part: u64, epoch: u64, notes: &[Note], warp: Option<Arc<super::phoneme_timing::Warp>>) -> i32 {
    set_controls(part, epoch, notes, warp, None)
}
pub(super) fn set_controls(part: u64, epoch: u64, notes: &[Note], warp: Option<Arc<super::phoneme_timing::Warp>>,
    gains: Option<Arc<super::transition_gain::Controls>>) -> i32 {
    if part == 0 || epoch == 0 || notes.is_empty() || notes.len() > 100_000 || notes.iter().any(|n|
        n.begin < 0 || n.end <= n.begin || !n.pitch.is_finite() ||
        !n.coefficient.is_finite() || !(n.coefficient == -1.0 || (0.0..=1.0).contains(&n.coefficient))) { return -1; }
    let mut fingerprint = match super::timing_cache::fingerprint(notes, warp.as_deref()) { Ok(value) => value, Err(code) => return code };
    if let Some(gains) = &gains {
        let mut bytes = b"VOCALOIDPatcher.transition-gain.cache.v1\0".to_vec();
        bytes.extend_from_slice(&fingerprint); bytes.extend_from_slice(&gains.fingerprint);
        fingerprint = match super::timing_cache::digest(&bytes) { Ok(value) => value, Err(code) => return code };
    }
    let mut map = parts().write().unwrap_or_else(|e| e.into_inner());
    if map.get(&part).is_some_and(|e| e.epoch >= epoch) { return -2; }
    if let Some(g)=map.get(&part).and_then(|e|e.gains.as_ref()){g.retired.store(true,Ordering::Release);}
    map.insert(part, Arc::new(Entry { epoch, notes: notes.into(), scopes: AtomicU64::new(0),
        applied: AtomicU64::new(0), failures: AtomicU64::new(0), cache_keys: AtomicU64::new(0), fingerprint, warp, gains }));
    0
}

pub fn remove(part: u64) {
    if let Some(e)=parts().write().unwrap_or_else(|e| e.into_inner()).remove(&part) {
        if let Some(g)=&e.gains{g.retired.store(true,Ordering::Release);}
    }
    parsers().write().unwrap_or_else(|e| e.into_inner()).retain(|_, b| b.part != part);
}
pub(super) fn remove_epoch(part:u64,epoch:u64)->i32 {
    let mut entries=parts().write().unwrap_or_else(|e|e.into_inner());
    if !entries.get(&part).is_some_and(|e|e.epoch==epoch){return 0;}
    if let Some(e)=entries.remove(&part){if let Some(g)=&e.gains{g.retired.store(true,Ordering::Release);}}
    // A renderer still holding this scope must see the retired snapshot on
    // its NEXT Step too. Erasing its parser here would silently remove gain
    // and failure detection between Steps. Idle bindings may be discarded;
    // active tombstones are replaced by the next normal engine bind.
    parsers().write().unwrap_or_else(|e|e.into_inner()).retain(|_,b|
        b.part!=part || b.entry.epoch!=epoch || Arc::strong_count(&b.scope)>1);
    1
}

pub fn status(part: u64) -> Status {
    let entry = parts().read().unwrap_or_else(|e| e.into_inner()).get(&part).cloned();
    entry.map(|e| Status {
        epoch: e.epoch, scopes: e.scopes.load(Ordering::Acquire), applied: e.applied.load(Ordering::Acquire),
        cache_keys: e.cache_keys.load(Ordering::Acquire),
        failures: e.failures.load(Ordering::Acquire) + e.gains.as_ref().map_or(0,|g|g.failures.load(Ordering::Acquire)) + parsers().read().unwrap_or_else(|p|p.into_inner()).values()
            .filter(|b| b.part == part && Arc::ptr_eq(&e,&b.entry)).filter_map(|b|b.warp.as_ref()).map(|w|w.failures()).sum::<u64>() }).unwrap_or_default()
}

pub fn cache_key(part: u64, original: [u8; 16]) -> Option<[u8; 16]> {
    let entry = parts().read().unwrap_or_else(|e| e.into_inner()).get(&part).cloned()?;
    let mut input = [0u8; 48];
    input[..16].copy_from_slice(&original);
    input[16..].copy_from_slice(&entry.fingerprint);
    match super::timing_cache::digest(&input) {
        Ok(digest) => {
            entry.cache_keys.fetch_add(1, Ordering::AcqRel);
            Some(digest[..16].try_into().unwrap())
        }
        Err(_) => { entry.failures.fetch_add(1, Ordering::AcqRel); None }
    }
}

pub fn scope<T>(part: u64, run: impl FnOnce() -> T) -> T {
    let binding = parts().read().unwrap_or_else(|e| e.into_inner()).get(&part).cloned()
        .map(|entry| Binding { part, warp: None, entry, outer: 0, schedule: 0, scope: Arc::new(()) });
    let counted = binding.is_some();
    if let Some(b) = &binding {
        b.entry.scopes.fetch_add(1, Ordering::Relaxed);
        let active = ACTIVE_SCOPES.fetch_add(1, Ordering::AcqRel) + 1;
        PEAK_SCOPES.fetch_max(active, Ordering::AcqRel);
    }
    struct Restore(Option<Binding>, bool);
    impl Drop for Restore { fn drop(&mut self) {
        ACTIVE.with(|v| { v.replace(self.0.take()); });
        if self.1 { ACTIVE_SCOPES.fetch_sub(1, Ordering::AcqRel); }
    } }
    let _restore = Restore(ACTIVE.with(|v| v.replace(binding)), counted);
    run()
}

unsafe fn parser(engine: *mut c_void) -> Option<(usize, u64)> {
    if engine.is_null() { return None; }
    let slot = unsafe { ptr::read_unaligned(engine.cast::<u8>().add(16).cast::<i32>()) };
    if !(0..32).contains(&slot) { return None; }
    let name: Vec<u16> = "DSE.dll\0".encode_utf16().collect();
    let module = unsafe { GetModuleHandleW(name.as_ptr()) }.cast::<u8>();
    if module.is_null() { return None; }
    let outer = unsafe { ptr::read_unaligned(module.add(super::timing_profile::DseStates + slot as usize * 8).cast::<u64>()) };
    (outer != 0).then_some((outer as usize + super::timing_profile::StateParser, outer))
}
#[link(name = "kernel32")]
unsafe extern "system" { fn GetModuleHandleW(name: *const u16) -> *mut c_void; }

pub unsafe fn bind(engine: *mut c_void) {
    let Some((parser, outer)) = (unsafe { parser(engine) }) else { return; };
    let binding = ACTIVE.with(|v| v.borrow().clone());
    let schedule = unsafe {ptr::read_unaligned(parser as *const usize)};
    bind_parser(parser, outer, schedule, binding);
}

fn bind_parser(parser: usize, outer: u64, schedule: usize, binding: Option<Binding>) {
    let mut map = parsers().write().unwrap_or_else(|e| e.into_inner());
    // Every newly bound engine claims its schedule, including unmodified
    // renderers and private planning engines with no Part controls. Removing
    // only its parser key leaves a freed renderer's warp attached when the
    // allocator reuses that schedule at a different parser address.
    if schedule != 0 {
        let previous = map.len();
        map.retain(|&key, old| key == parser || old.schedule != schedule);
        SCHEDULE_REUSES.fetch_add((previous - map.len()) as u64, Ordering::Relaxed);
    }
    if let Some(mut b) = binding {
        b.outer = outer;
        b.schedule = schedule;
        // One Part render may create multiple engines. They share immutable
        // anchors, never mutable displacement/checkpoints. Preserve a cursor
        // only for the same initialized parser, schedule, epoch and scope.
        b.warp = map.get(&parser).filter(|old| old.outer == outer && old.schedule == b.schedule &&
            Arc::ptr_eq(&old.entry, &b.entry) && Arc::ptr_eq(&old.scope, &b.scope))
            .and_then(|old| old.warp.clone())
            .or_else(|| b.entry.warp.as_ref().map(|w| super::phoneme_timing::Context::new(w.clone())));
        map.insert(parser, b);
    }
    else { map.remove(&parser); }
}

fn current(b: &Binding) -> bool {
    parts().read().unwrap_or_else(|e| e.into_inner()).get(&b.part)
        .is_some_and(|e| Arc::ptr_eq(e, &b.entry))
}

// VSM supplies absolute frames including presend; this DSE adds 3087 frames.
// Exact fixed-origin matching avoids calibrating repeated notes by call order.
fn matching(notes: &[Note], begin: i64, duration: i64, pitch: f32) -> Result<Option<f32>, ()> {
    let mut matches = notes.iter().filter(|n| (n.begin + 3087 - begin).abs() <= 2 &&
        (n.end + 3087 - begin - duration).abs() <= 2 && (n.pitch - pitch).abs() <= 0.25);
    let first = matches.next().ok_or(())?;
    if matches.next().is_some() { return Err(()); }
    Ok((first.coefficient >= 0.0).then_some(first.coefficient))
}

pub unsafe fn apply(parser: *mut c_void, record: *const i32) {
    if parser.is_null() || record.is_null() { return; }
    let binding = ACTIVE.with(|v| v.borrow().clone()).or_else(||
        parsers().read().unwrap_or_else(|e| e.into_inner()).get(&(parser as usize)).cloned());
    let Some(b) = binding else { return; };
    let outer = unsafe { ptr::read_unaligned(parser.cast::<u8>().add(8).cast::<u64>()) };
    if b.outer != 0 && b.outer != outer { b.entry.failures.fetch_add(1, Ordering::Relaxed); return; }
    let begin = i64::from(unsafe { ptr::read_unaligned(record) });
    let duration = i64::from(unsafe { ptr::read_unaligned(record.add(1)) });
    // Synthetic silence/context records are not authored source notes.
    let count = unsafe { ptr::read_unaligned(record.cast::<u8>().add(super::timing_profile::NotePhoneCount)) };
    let silence = count == 0 || (count == 1 && unsafe {
        slice::from_raw_parts(record.cast::<u8>().add(super::timing_profile::NotePhones), 4) == b"Sil\0"
    });
    if duration <= 0 || silence { return; }
    let pitch = unsafe { ptr::read_unaligned(record.add(2).cast::<f32>()) };
    if !current(&b) { b.entry.failures.fetch_add(1, Ordering::Relaxed); return; }
    match matching(&b.entry.notes, begin, duration, pitch) {
        Ok(Some(c)) => {
            unsafe { ptr::write_unaligned(record.cast_mut().cast::<u8>().add(super::timing_profile::NoteCoefficient).cast::<f32>(), c); }
            b.entry.applied.fetch_add(1, Ordering::Relaxed);
        }
        Ok(None) => (),
        Err(()) => { b.entry.failures.fetch_add(1, Ordering::Relaxed); }
    }
}

pub unsafe fn failed(engine: *mut c_void) -> bool {
    let Some((parser, _)) = (unsafe { parser(engine) }) else { return false; };
    let binding = parsers().read().unwrap_or_else(|e| e.into_inner()).get(&parser).cloned();
    binding.is_some_and(|b| !current(&b) || b.entry.failures.load(Ordering::Acquire) != 0 || b.warp.as_ref().is_some_and(|w|w.failures()!=0) ||
        b.entry.gains.as_ref().is_some_and(|g|g.failures.load(Ordering::Acquire)!=0))
}

pub(super) unsafe fn transition_binding(engine: *mut c_void) -> Option<(usize, Arc<super::transition_gain::Controls>)> {
    let (parser, outer) = unsafe { parser(engine) }?;
    let binding = parsers().read().unwrap_or_else(|e|e.into_inner()).get(&parser).cloned()?;
    let gains = binding.entry.gains.clone()?;
    if !current(&binding) || binding.outer != outer { gains.failures.fetch_add(1, Ordering::Relaxed); }
    Some((outer as usize, gains))
}
pub(super) fn transition_status(part: u64) -> super::transition_gain::Status {
    let entry = parts().read().unwrap_or_else(|e|e.into_inner()).get(&part).cloned();
    if let Some(entry) = entry { if let Some(gains) = &entry.gains { return gains.status(entry.epoch); } }
    Default::default()
}

pub fn phoneme_context(parser: usize) -> Option<Arc<super::phoneme_timing::Context>> {
    let binding = parsers().read().unwrap_or_else(|e|e.into_inner()).get(&parser).cloned();
    bound_phoneme_context(binding)
}

pub(super) unsafe fn render_identity(engine: *mut c_void) -> Option<(u64, u64, usize)> {
    let (parser, _) = unsafe { parser(engine) }?;
    let binding = parsers().read().unwrap_or_else(|e| e.into_inner()).get(&parser).cloned()?;
    current(&binding).then_some((binding.part, binding.entry.epoch, Arc::as_ptr(&binding.scope) as usize))
}
pub fn phoneme_schedule_context(schedule: usize) -> Option<Arc<super::phoneme_timing::Context>> {
    if schedule == 0 { return None; }
    let bindings: Vec<_> = parsers().read().unwrap_or_else(|e|e.into_inner()).values()
        .filter(|b| b.schedule == schedule).cloned().collect();
    bindings.into_iter().find_map(|b| bound_phoneme_context(Some(b)))
}

fn bound_phoneme_context(binding: Option<Binding>) -> Option<Arc<super::phoneme_timing::Context>> {
    let binding = binding?;
    if !current(&binding) { return None; }
    let warp = binding.warp.as_ref()?;
    // A render scope also encloses engine initialization. Its unbound ACTIVE
    // context must not warp scratch schedule allocations during warm-up. Only
    // Start/AddEvent establish the actual parser/schedule for this render. A
    // previous binding for the reused engine must not survive into a new scope.
    let matches_scope = ACTIVE.with(|v| v.borrow().as_ref().is_none_or(|active|
        Arc::ptr_eq(&active.entry, &binding.entry) && Arc::ptr_eq(&active.scope, &binding.scope)));
    matches_scope.then(|| warp.clone())
}

#[unsafe(no_mangle)] pub extern "C" fn v6_timing_abi_version() -> u32 { 3 }
/// # Safety
/// Inputs reference the stated bounded arrays, copied before publication.
#[unsafe(no_mangle)] pub unsafe extern "C" fn v6_timing_set_part_phonemes(part: u64, epoch: u64,
    notes: *const Note, count: i32, points: *const super::phoneme_timing::Point, num_points: i32) -> i32 {
    if !super::phoneme_timing::ready() { return -3; }
    if count <= 0 || count > 100_000 || notes.is_null() || points.is_null() || !(2..=8192).contains(&num_points) { return -1; }
    let warp = match super::phoneme_timing::Warp::new(unsafe {slice::from_raw_parts(points,num_points as usize)}) { Ok(w)=>w,Err(e)=>return e };
    set_warp(part,epoch,unsafe {slice::from_raw_parts(notes,count as usize)},Some(warp))
}
#[unsafe(no_mangle)] pub extern "C" fn v6_timing_peak_scopes() -> u64 { PEAK_SCOPES.load(Ordering::Acquire) }
#[unsafe(no_mangle)] pub extern "C" fn v6_timing_schedule_reuses() -> u64 { SCHEDULE_REUSES.load(Ordering::Acquire) }
#[unsafe(no_mangle)] pub extern "C" fn v6_timing_note_size() -> u32 { std::mem::size_of::<Note>() as u32 }
#[unsafe(no_mangle)] pub extern "C" fn v6_timing_status_size() -> u32 { std::mem::size_of::<Status>() as u32 }
#[unsafe(no_mangle)] pub extern "C" fn v6_timing_install() -> i32 {
    if !super::timing_profile::selected() { return -30; }
    let result = super::register_shift_hook::install();
    if result < 0 { return result; }
    // 1A and 1B prepare hooks must both be present; partial installation fails.
    if !super::register_shift_hook::timing_ready() { return -3; }
    super::timing_cache::install()
}
/// # Safety
/// notes references count readable Note values for this call only.
#[unsafe(no_mangle)] pub unsafe extern "C" fn v6_timing_set_part(part: u64, epoch: u64, notes: *const Note, count: i32) -> i32 {
    if count <= 0 || count > 100_000 || notes.is_null() { return -1; }
    set(part, epoch, unsafe { slice::from_raw_parts(notes, count as usize) })
}
#[unsafe(no_mangle)] pub extern "C" fn v6_timing_remove_part(part: u64) { remove(part); }
/// # Safety
/// output references one writable Status.
#[unsafe(no_mangle)] pub unsafe extern "C" fn v6_timing_status(part: u64, output: *mut Status) -> i32 {
    if output.is_null() { return -1; }
    unsafe { output.write(status(part)); } 0
}
/// # Safety
/// output references one writable phoneme Status. No engine pointer is exposed.
#[unsafe(no_mangle)] pub unsafe extern "C" fn v6_timing_phoneme_status(part: u64, output: *mut super::phoneme_timing::Status) -> i32 {
    if output.is_null() { return -1; }
    let entry = parts().read().unwrap_or_else(|e| e.into_inner()).get(&part).cloned();
    let map = parsers().read().unwrap_or_else(|e| e.into_inner());
    let context = map.values().filter(|b| b.part == part && entry.as_ref().is_some_and(|e| Arc::ptr_eq(e,&b.entry))).filter_map(|b| b.warp.as_ref())
        .max_by_key(|c| c.failures());
    if let Some(context) = context { unsafe { output.write(context.snapshot()); } 0 } else { -2 }
}

#[cfg(test)] mod tests {
    use super::*;
    #[test] fn transition_removal_cannot_detach_a_newer_publication() {
        use super::super::transition_gain::{Controls,Gain};
        let part=0xff30;
        let notes=[Note{begin:100,end:200,pitch:0.0,coefficient:0.5}];
        let make=||Controls::new(&[Gain{begin:100,end:120,from:1,to:2,stage:0,reverse:0,multiplier:0.5,reserved:0}]).unwrap().unwrap();
        let first=make();let second=make();
        assert_eq!(set_controls(part,1,&notes,None,Some(first.clone())),0);
        let key=cache_key(part,[0;16]).unwrap();
        assert_eq!(set_controls(part,2,&notes,None,Some(second.clone())),0);
        assert!(first.retired.load(Ordering::Acquire));
        assert_eq!(key,cache_key(part,[0;16]).unwrap());
        assert_eq!(remove_epoch(part,1),0);
        assert_eq!(status(part).epoch,2);
        assert!(!second.retired.load(Ordering::Acquire));
        scope(part,|| {
            bind_parser(0xff31,0xff32,0xff33,ACTIVE.with(|v|v.borrow().clone()));
            assert_eq!(remove_epoch(part,2),1);
            let bound=parsers().read().unwrap().get(&0xff31).cloned().unwrap();
            assert!(!current(&bound));
            assert!(bound.entry.gains.as_ref().unwrap().retired.load(Ordering::Acquire));
        });
        assert!(second.retired.load(Ordering::Acquire));
        assert_eq!(status(part).epoch,0);
        assert_eq!(set(part,3,&notes),0);
        assert_ne!(key,cache_key(part,[0;16]).unwrap());
        remove(part);
    }
    #[test] fn reused_schedule_address_does_not_select_a_stale_parser() {
        use super::super::phoneme_timing::{Point,Warp};
        let part = 0xff20;
        let notes = [Note{begin:100,end:200,pitch:0.0,coefficient:-1.0}];
        let warp = Warp::new(&[Point{from:0,to:0},Point{from:100,to:101},Point{from:200,to:200}]).unwrap();
        assert_eq!(set_warp(part,1,&notes,Some(warp.clone())),0);
        scope(part,|| bind_parser(0xff21,0xff22,0xff23,ACTIVE.with(|v|v.borrow().clone())));
        assert_eq!(set_warp(part,2,&notes,Some(warp)),0);
        scope(part,|| {
            bind_parser(0xff24,0xff25,0xff23,ACTIVE.with(|v|v.borrow().clone()));
            assert!(phoneme_context(0xff21).is_none());
            let expected = phoneme_context(0xff24).unwrap();
            assert!(Arc::ptr_eq(&expected,&phoneme_schedule_context(0xff23).unwrap()));
        });
        remove(part);
    }
    #[test] fn engines_in_one_part_do_not_reset_each_others_coordinates() {
        use super::super::phoneme_timing::{Point, Warp};
        let part = 0xff10;
        let notes = [Note { begin: 86, end: 172, pitch: 0.0, coefficient: -1.0 }];
        let warp = Warp::new(&[Point { from: 100, to: 100 }, Point { from: 120, to: 130 }, Point { from: 160, to: 160 }]).unwrap();
        assert_eq!(set_warp(part, 1, &notes, Some(warp)), 0);
        scope(part, || {
            let active = || ACTIVE.with(|v| v.borrow().clone());
            bind_parser(0xff11, 0xff12, 0xff13, active());
            let a = phoneme_context(0xff11).unwrap();
            assert_eq!(a.halves(100, 20, 20), Some((30, 15)));
            bind_parser(0xff14, 0xff15, 0xff16, active());
            let b = phoneme_context(0xff14).unwrap();
            assert_eq!(b.halves(100, 20, 20), Some((30, 15)));
            b.reset_schedule();
            bind_parser(0xff11, 0xff12, 0xff13, active());
            assert_eq!(phoneme_context(0xff11).unwrap().halves(145, 10, 10), Some((8, 7)));
            assert_eq!(a.failures(), 0);
            // Replacing an engine's schedule must start a fresh coordinate space.
            bind_parser(0xff11, 0xff12, 0xff17, active());
            assert_eq!(phoneme_context(0xff11).unwrap().halves(100, 20, 20), Some((30, 15)));
        });
        remove(part);
    }
    #[test] fn phoneme_context_requires_the_bound_schedule_and_current_scope() {
        let part = 0xff02;
        let notes = [Note { begin: 86, end: 172, pitch: 0.0, coefficient: -1.0 }];
        let warp = super::super::phoneme_timing::Warp::new(&[
            super::super::phoneme_timing::Point { from: 0, to: 0 },
            super::super::phoneme_timing::Point { from: 100, to: 100 },
        ]).unwrap();
        assert_eq!(set_warp(part, 1, &notes, Some(warp)), 0);
        scope(part, || {
            assert!(phoneme_schedule_context(0xff03).is_none());
            bind_parser(0xff04, 0xff06, 0xff03, ACTIVE.with(|v| v.borrow().clone()));
            assert!(phoneme_context(0xff04).is_some());
            assert!(phoneme_schedule_context(0xff03).is_some());
            assert!(phoneme_schedule_context(0xff05).is_none());
            scope(part, || assert!(phoneme_schedule_context(0xff03).is_none()));
            assert!(phoneme_schedule_context(0xff03).is_some());
        });
        assert!(phoneme_schedule_context(0xff03).is_some());
        assert_eq!(set(part, 2, &notes), 0);
        assert!(phoneme_schedule_context(0xff03).is_none());
        remove(part);
    }
    #[test] fn uncontrolled_engine_claim_retires_a_reused_schedules_old_warp() {
        let part = 0xff72;
        let notes = [Note { begin: 86, end: 172, pitch: 0.0, coefficient: -1.0 }];
        let warp = super::super::phoneme_timing::Warp::new(&[
            super::super::phoneme_timing::Point { from: 0, to: 0 },
            super::super::phoneme_timing::Point { from: 100, to: 120 },
            super::super::phoneme_timing::Point { from: 200, to: 200 },
        ]).unwrap();
        assert_eq!(set_warp(part, 1, &notes, Some(warp)), 0);
        scope(part, || bind_parser(0xff74, 0xff76, 0xff73, ACTIVE.with(|v| v.borrow().clone())));
        assert!(phoneme_schedule_context(0xff73).is_some());
        // The old Part is still current. A different engine now owns exactly
        // that freed schedule, without entering a controlled render scope.
        bind_parser(0xff78, 0xff79, 0xff73, None);
        assert!(phoneme_schedule_context(0xff73).is_none());
        assert!(phoneme_context(0xff74).is_none());
        assert!(phoneme_context(0xff78).is_none());
        assert!(parts().read().unwrap().contains_key(&part));
        remove(part);
    }
    #[test] fn exact_frames_disambiguate_repeated_notes_and_reject_ambiguity() {
        let notes = [Note { begin: 86, end: 172, pitch: -900.0, coefficient: 0.125 },
            Note { begin: 172, end: 258, pitch: -900.0, coefficient: 0.875 }];
        assert_eq!(matching(&notes, 3173, 86, -900.0), Ok(Some(0.125)));
        assert_eq!(matching(&notes, 3259, 86, -900.0), Ok(Some(0.875)));
        assert!(matching(&[notes[0], notes[0]], 3173, 86, -900.0).is_err());
        assert!(matching(&notes, 0, 86, -900.0).is_err());
    }
    #[test] fn snapshots_survive_updates_but_cannot_become_current_again() {
        let part = 0xff01;
        let notes = [Note { begin: 86, end: 172, pitch: 0.0, coefficient: 0.25 }];
        assert_eq!(set(part, 1, &notes), 0);
        scope(part, || {
            let b = ACTIVE.with(|v| v.borrow().clone().unwrap());
            assert!(current(&b)); assert_eq!(set(part, 2, &notes), 0); assert!(!current(&b));
            assert_eq!(b.entry.notes[0].coefficient, 0.25);
            assert_eq!(set(part, 1, &notes), -2);
        });
        remove(part); assert_eq!(status(part).epoch, 0);
        assert_eq!(std::mem::size_of::<Note>(), 24); assert_eq!(std::mem::size_of::<Status>(), 40);
    }
}
