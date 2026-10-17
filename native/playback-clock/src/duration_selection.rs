//! Independent duration preference. Immutable Part snapshots; mutable state is
//! owned by a render/engine. Time contracts are before the existing timing warp.
use super::duration_bridge::Call;
use super::*;
use std::cell::RefCell;

const MAX_NOTES: usize = 100_000;
const MAX_EVENTS: usize = 32768;
const WORK_BYTES: usize = super::timing_profile::DurationWorkBytes;
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct Note {
    pub begin: i64,
    pub end: i64,
    pub pitch: f32,
    pub ratio: f32,
    pub policy: u32, // 0 native eligibility, 1 explicitly experimental 1200-cent pool
    pub identity: u32,
}
#[repr(C)]
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Event {
    pub section: u64,
    pub key: [u8; 32],
    pub kind: i32,
    pub count: i32,
    pub gap: i32,
    pub index: i32,
    pub halves: [i32; 64],
}
#[repr(C)]
#[derive(Clone, Copy, Default, Debug)]
pub struct Status {
    pub scopes: u64,
    pub scores: u64,
    pub changed: u64,
    pub failures: u64,
    pub contracts: u64,
    pub last_owner: i32,
    pub last_begin: i32,
    pub last_target: i32,
    pub last_work: i32,
    pub last_start: i32,
    pub last_end: i32,
    pub reason: i32,
    pub reserved: i32,
}
struct Snapshot {
    epoch: u64,
    notes: Arc<[Note]>,
    plan: Arc<[Event]>,
    fingerprint: [u8; 32],
    status: Mutex<Status>,
}
struct Runtime {
    snapshot: Arc<Snapshot>,
    capture: bool,
    captured: Vec<Event>,
    headers: HashMap<usize, Box<[u64; 8]>>,
    layer_history: u32,
    failure: i32,
}
type Shared = Arc<Mutex<Runtime>>;
static PARTS: OnceLock<RwLock<HashMap<u64, Arc<Snapshot>>>> = OnceLock::new();
static ENGINES: OnceLock<Mutex<HashMap<usize, ((u64, u64, usize), Shared)>>> = OnceLock::new();
static MODULE: AtomicU64 = AtomicU64::new(0);
static INSTALL: Mutex<()> = Mutex::new(());
static READY: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);
thread_local! {
    static ACTIVE: RefCell<Option<Shared>> = const { RefCell::new(None) };
    static QUERY: RefCell<Option<Shared>> = const { RefCell::new(None) };
    static PREPARE: RefCell<Option<Preparation>> = const { RefCell::new(None) };
    static WORK: Cell<(usize,usize)> = const { Cell::new((0,0)) };
}
#[derive(Clone)]
struct Preparation {
    section: u64,
    cursor: usize,
    owner: Option<Note>,
    begin: i32,
    target: i32,
}
fn parts() -> &'static RwLock<HashMap<u64, Arc<Snapshot>>> {
    PARTS.get_or_init(Default::default)
}
fn active() -> Option<Shared> {
    ACTIVE
        .with(|v| v.borrow().clone())
        .or_else(|| QUERY.with(|v| v.borrow().clone()))
}
fn status(snapshot: &Snapshot, update: impl FnOnce(&mut Status)) {
    update(&mut snapshot.status.lock().unwrap_or_else(|e| e.into_inner()));
}
fn fail(runtime: &mut Runtime, reason: i32) {
    if runtime.failure == 0 {
        runtime.failure = reason;
    }
    status(&runtime.snapshot, |s| {
        s.failures += 1;
        s.reason = reason;
    });
}
pub(super) fn failed() -> bool {
    active().is_some_and(|r| r.lock().unwrap_or_else(|e| e.into_inner()).failure != 0)
}
fn runtime(snapshot: Arc<Snapshot>, capture: bool) -> Shared {
    Arc::new(Mutex::new(Runtime {
        snapshot,
        capture,
        captured: Vec::new(),
        headers: HashMap::new(),
        layer_history: 0,
        failure: 0,
    }))
}
pub(super) struct EngineGuard(Option<Shared>);
impl Drop for EngineGuard {
    fn drop(&mut self) {
        ACTIVE.with(|v| v.replace(self.0.take()));
    }
}
pub(super) unsafe fn engine(engine: *mut c_void, reset: bool) -> EngineGuard {
    if QUERY.with(|v| v.borrow().is_some()) {
        return EngineGuard(ACTIVE.with(|v| v.borrow().clone()));
    }
    let identity = unsafe { super::sampled_timing::render_identity(engine) };
    let next = identity.and_then(|identity| {
        let snapshot = parts()
            .read()
            .unwrap_or_else(|e| e.into_inner())
            .get(&identity.0)
            .cloned()?;
        let mut engines = ENGINES
            .get_or_init(Default::default)
            .lock()
            .unwrap_or_else(|e| e.into_inner());
        if reset
            || engines
                .get(&(engine as usize))
                .is_none_or(|entry| entry.0 != identity)
        {
            let state = runtime(snapshot.clone(), false);
            if snapshot.epoch != identity.1 {
                fail(&mut state.lock().unwrap_or_else(|e| e.into_inner()), 12);
            }
            engines.insert(engine as usize, (identity, state));
            status(&snapshot, |s| s.scopes += 1);
        }
        engines.get(&(engine as usize)).map(|v| v.1.clone())
    });
    EngineGuard(ACTIVE.with(|v| v.replace(next)))
}
pub(super) struct PrepareGuard(Option<Preparation>);
impl Drop for PrepareGuard {
    fn drop(&mut self) {
        let current = PREPARE.with(|v| v.replace(self.0.take()));
        if let (Some(r), Some(p)) = (active(), current) {
            let mut r = r.lock().unwrap_or_else(|e| e.into_inner());
            if !r.capture {
                let expected = r
                    .snapshot
                    .plan
                    .iter()
                    .filter(|e| e.section == p.section)
                    .count();
                if p.cursor != expected {
                    fail(&mut r, 5);
                }
            }
        }
    }
}
unsafe fn record(notes: &[Note], input: *const i32) -> Result<(Option<Note>, i32), i32> {
    if input.is_null() {
        return Ok((None, -1));
    }
    let bytes = input.cast::<u8>();
    let begin = unsafe { input.read_unaligned() };
    let duration = unsafe { input.add(1).read_unaligned() };
    let count = unsafe { bytes.add(super::timing_profile::NotePhoneCount).read() };
    let silence = count == 0
        || (count == 1 && unsafe { slice::from_raw_parts(bytes.add(super::timing_profile::NotePhones), 4) } == b"Sil\0");
    if silence || duration <= 0 {
        return Ok((None, begin));
    }
    let pitch = unsafe { input.add(2).cast::<f32>().read_unaligned() };
    let mut matches = notes.iter().filter(|n| {
        (n.begin + 3087 - i64::from(begin)).abs() <= 2
            && (n.end + 3087 - i64::from(begin) - i64::from(duration)).abs() <= 2
            && (n.pitch - pitch).abs() <= 0.25
    });
    let found = matches.next().ok_or(1)?;
    if matches.next().is_some() {
        return Err(2);
    }
    Ok((Some(*found), begin))
}
pub(super) unsafe fn prepare(
    _parser: *mut c_void,
    current: *const i32,
    target: *const i32,
    kind: u32,
) -> PrepareGuard {
    let next = active().and_then(|r| {
        let mut r = r.lock().unwrap_or_else(|e| e.into_inner());
        let (raw_current, raw_target) = (current, target);
        let current = unsafe { record(&r.snapshot.notes, current) };
        let target = unsafe { record(&r.snapshot.notes, target) };
        match (current, target) {
            (Ok((owner, begin)), Ok((next, target))) => {
                let a = owner.map_or(0, |n| u64::from(n.identity) + 1);
                let b = next.map_or(0, |n| u64::from(n.identity) + 1);
                Some(Preparation {
                    section: a | (b << 21) | (u64::from(kind) << 42),
                    cursor: 0,
                    owner,
                    begin,
                    target,
                })
            }
            _ => {
                status(&r.snapshot, |s| unsafe {
                    s.last_begin = if raw_current.is_null() {
                        -1
                    } else {
                        raw_current.read_unaligned()
                    };
                    s.last_target = if raw_target.is_null() {
                        -1
                    } else {
                        raw_target.read_unaligned()
                    };
                    s.last_start = if raw_current.is_null() {
                        -1
                    } else {
                        raw_current.add(1).read_unaligned()
                    };
                    s.last_end = if raw_target.is_null() {
                        -1
                    } else {
                        raw_target.add(1).read_unaligned()
                    };
                    s.last_owner = r.snapshot.notes.first().map_or(-1, |n| n.begin as i32);
                });
                fail(&mut r, 1);
                None
            }
        }
    });
    PrepareGuard(PREPARE.with(|v| v.replace(next)))
}
pub(super) struct WorkGuard((usize, usize));
impl Drop for WorkGuard {
    fn drop(&mut self) {
        WORK.with(|v| v.set(self.0));
    }
}
pub(super) fn work_scope(current: usize, target: usize) -> WorkGuard {
    WorkGuard(WORK.with(|v| v.replace((current, target))))
}
pub(super) fn pitch_width(original: f32) -> f32 {
    // An explicit per-note experimental policy. Neutral/automatic renders keep
    // the native eligibility window. Work items here belong to current NOTE.
    if active().is_none_or(|r| r.lock().unwrap_or_else(|e| e.into_inner()).capture) {
        return original;
    }
    PREPARE
        .with(|v| v.borrow().as_ref().and_then(|p| p.owner))
        .filter(|n| n.policy == 1)
        .map_or(original, |_| 1200.0)
}
pub(super) unsafe fn score(
    state: usize,
    output: *mut f32,
    start: i32,
    end: i32,
    work: usize,
    first: i32,
    last: i32,
    threshold: i32,
    flag: u8,
    capped: u8,
) {
    let Some(r) = active() else {
        return;
    };
    let pair = PREPARE.with(|v| v.borrow().clone());
    let Some(pair) = pair else {
        return;
    };
    let Some(note) = pair.owner else {
        return;
    };
    let mut runtime = r.lock().unwrap_or_else(|e| e.into_inner());
    let role = WORK.with(|v| {
        let (a, b) = v.get();
        if work == a {
            1
        } else if work == b {
            2
        } else {
            0
        }
    });
    status(&runtime.snapshot, |s| {
        s.scores += 1;
        s.last_owner = note.identity as i32;
        s.last_begin = pair.begin;
        s.last_target = pair.target;
        s.last_work = role;
        s.last_start = start;
        s.last_end = end;
    });
    if runtime.capture || note.ratio == 1.0 || end <= start {
        return;
    }
    if role == 0 {
        fail(&mut runtime, 3);
        return;
    }
    let count = unsafe { (work as *const u8).add(12).cast::<i32>().read_unaligned() };
    if !(0..=32).contains(&count) || first < 0 || last > count * 2 {
        fail(&mut runtime, 4);
        return;
    }
    if first >= last
        || output.is_null()
        || unsafe { *output } == f32::MAX
        || unsafe { *output.add(1) } == f32::MAX
    {
        return;
    }
    drop(runtime);
    type Allocate =
        unsafe extern "system" fn(usize, *mut [u32; 2], i32, usize, i32, i32, i32, u8, u8) -> usize;
    let allocate: Allocate =
        unsafe { std::mem::transmute((MODULE.load(Ordering::Acquire) as usize) + super::timing_profile::DseDurationScore) };
    let original = unsafe { slice::from_raw_parts(work as *const u8, WORK_BYTES) };
    let mut scratch = original.to_vec();
    let mut actual = [0u32; 2];
    let mut virtual_cost = [0u32; 2];
    let real = end - start;
    let virtual_frames = (f64::from(real) * f64::from(note.ratio))
        .round()
        .clamp(1.0, 4096.0) as i32;
    if real == virtual_frames {
        return;
    }
    unsafe {
        allocate(
            state,
            &mut actual,
            real,
            scratch.as_mut_ptr() as usize,
            first,
            last,
            threshold,
            flag,
            capped,
        );
    }
    scratch.copy_from_slice(original);
    unsafe {
        allocate(
            state,
            &mut virtual_cost,
            virtual_frames,
            scratch.as_mut_ptr() as usize,
            first,
            last,
            threshold,
            flag,
            capped,
        );
    }
    let actual = f32::from_bits(actual[1]);
    let virtual_cost = f32::from_bits(virtual_cost[1]);
    // Difference weighting keeps 100% mathematically neutral. Policy 1 is an
    // explicit diagnostic recipe; it is never an implicit product default.
    let weight = if note.policy == 1 { 128.0 } else { 1.0 };
    let result = unsafe { *output.add(1) } + (virtual_cost - actual) * weight;
    if !result.is_finite() || !actual.is_finite() || !virtual_cost.is_finite() {
        fail(&mut r.lock().unwrap_or_else(|e| e.into_inner()), 6);
        return;
    }
    unsafe {
        *output.add(1) = result;
    }
    status(&r.lock().unwrap_or_else(|e| e.into_inner()).snapshot, |s| {
        s.changed += 1
    });
}

unsafe fn observe(id: u32, call: &mut Call) {
    let caller = call
        .caller
        .wrapping_sub(MODULE.load(Ordering::Acquire) as usize);
    let kind = match id {
        0 if matches!(caller, super::timing_profile::DurationCaller0 | super::timing_profile::DurationCaller1 | super::timing_profile::DurationCaller2 | super::timing_profile::DurationCaller3) => 1,
        1 => 0,
        2 if matches!(caller, super::timing_profile::DurationCaller4 | super::timing_profile::DurationCaller5 | super::timing_profile::DurationCaller6 | super::timing_profile::DurationCaller7 | super::timing_profile::DurationCaller8) => 2,
        _ => return,
    };
    let Some(r) = active() else {
        return;
    };
    let Some(mut preparation) = PREPARE.with(|v| v.borrow().clone()) else {
        return;
    };
    let mut runtime = r.lock().unwrap_or_else(|e| e.into_inner());
    let mut event = Event {
        section: preparation.section,
        key: [0; 32],
        kind,
        count: 0,
        gap: 0,
        index: preparation.cursor as i32,
        halves: [0; 64],
    };
    let mut work = 0usize;
    let mut output = 0usize;
    if kind == 2 {
        event.gap = call.args[1] as i32;
        event.key[..8].copy_from_slice(&(caller as u64).to_le_bytes());
    } else {
        work = if kind == 0 {
            call.args[1]
        } else {
            call.args[3]
        };
        output = if kind == 1 { call.args[1] } else { 0 };
        let bytes = work as *const u8;
        let count = unsafe { bytes.add(12).cast::<i32>().read_unaligned() };
        if !(0..=32).contains(&count) {
            fail(&mut runtime, 7);
            return;
        }
        event.count = count;
        let mut key = vec![kind as u8];
        key.extend_from_slice(unsafe { slice::from_raw_parts(bytes.add(4), 5) });
        key.extend_from_slice(&count.to_le_bytes());
        for i in 0..count as usize {
            for names in [super::timing_profile::DurationWorkLeftName, super::timing_profile::DurationWorkRightName] {
                let name = unsafe { slice::from_raw_parts(bytes.add(names + i * 16), 16) };
                let Some(end) = name.iter().position(|b| *b == 0) else {
                    fail(&mut runtime, 7);
                    return;
                };
                key.extend_from_slice(&name[..=end]);
            }
        }
        key.push(unsafe { bytes.add(super::timing_profile::DurationWorkKind).read() });
        event.key = match super::timing_cache::digest(&key) {
            Ok(v) => v,
            Err(_) => {
                fail(&mut runtime, 7);
                return;
            }
        };
        for i in 0..count as usize * 2 {
            event.halves[i] = unsafe { bytes.add(super::timing_profile::DurationWorkHalves + i * 4).cast::<i32>().read_unaligned() };
        }
        if output != 0 {
            event.gap = unsafe { (output as *const i32).read_unaligned() };
        }
    }
    if runtime.capture {
        if runtime.captured.len() >= MAX_EVENTS {
            fail(&mut runtime, 8);
            return;
        }
        let same = runtime
            .captured
            .iter()
            .find(|e| e.section == event.section && e.index == event.index);
        if same.is_some_and(|e| e != &event) {
            fail(&mut runtime, 9);
            return;
        }
        if same.is_none() {
            runtime.captured.push(event);
        }
    } else {
        let expected = runtime
            .snapshot
            .plan
            .iter()
            .find(|e| e.section == event.section && e.index == event.index)
            .cloned();
        match expected {
            Some(expected)
                if expected.key == event.key
                    && expected.kind == kind
                    && expected.count == event.count =>
            {
                if kind == 2 {
                    // Before callback edits RDX in the saved register frame.
                    call.args[1] = expected.gap as usize;
                } else {
                    unsafe {
                        ptr::copy_nonoverlapping(
                            expected.halves.as_ptr(),
                            (work as *mut u8).add(super::timing_profile::DurationWorkHalves).cast::<i32>(),
                            event.count as usize * 2,
                        );
                    }
                    if output != 0 {
                        unsafe {
                            (output as *mut i32).write_unaligned(expected.gap);
                        }
                    }
                }
                status(&runtime.snapshot, |s| s.contracts += 1);
            }
            _ => {
                fail(&mut runtime, 10);
                return;
            }
        }
    }
    preparation.cursor += 1;
    PREPARE.with(|v| v.replace(Some(preparation)));
}

// Material-dependent integer rounding also occurs at the final frame-mapper
// entry (notably reversed consonant tails). Capture that native allocation
// before the existing phoneme warp; restoring only upper work halves leaves
// those transitions one frame longer with some selected recordings.
pub(super) unsafe fn mapping(
    kind: i32,
    from: usize,
    to: usize,
    left: i32,
    right: i32,
    language: i32,
    singer: i32,
) -> (i32, i32) {
    let Some(shared) = active() else {
        return (left, right);
    };
    let Some(mut preparation) = PREPARE.with(|v| v.borrow().clone()) else {
        return (left, right);
    };
    let mut runtime = shared.lock().unwrap_or_else(|e| e.into_inner());
    let mut key = vec![kind as u8];
    for address in [from, to] {
        if address == 0 {
            key.push(0);
            continue;
        }
        let name = unsafe { slice::from_raw_parts(address as *const u8, 16) };
        let Some(end) = name.iter().position(|b| *b == 0) else {
            fail(&mut runtime, 13);
            return (left, right);
        };
        key.extend_from_slice(&name[..=end]);
    }
    key.extend_from_slice(&language.to_le_bytes());
    key.extend_from_slice(&singer.to_le_bytes());
    let Ok(key) = super::timing_cache::digest(&key) else {
        fail(&mut runtime, 13);
        return (left, right);
    };
    let mut event = Event {
        section: preparation.section,
        key,
        kind,
        count: 1,
        gap: 0,
        index: preparation.cursor as i32,
        halves: [0; 64],
    };
    event.halves[0] = left;
    event.halves[1] = right;
    let result = if runtime.capture {
        if runtime.captured.len() >= MAX_EVENTS {
            fail(&mut runtime, 8);
            return (left, right);
        }
        match runtime
            .captured
            .iter()
            .find(|e| e.section == event.section && e.index == event.index)
        {
            Some(old) if old != &event => {
                fail(&mut runtime, 9);
                return (left, right);
            }
            Some(_) => {}
            None => runtime.captured.push(event),
        }
        (left, right)
    } else {
        match runtime
            .snapshot
            .plan
            .iter()
            .find(|e| e.section == event.section && e.index == event.index)
            .cloned()
        {
            Some(expected)
                if expected.kind == kind && expected.key == key && expected.count == 1 =>
            {
                status(&runtime.snapshot, |s| s.contracts += 1);
                (expected.halves[0], expected.halves[1])
            }
            _ => {
                fail(&mut runtime, 14);
                return (left, right);
            }
        }
    };
    preparation.cursor += 1;
    PREPARE.with(|v| v.replace(Some(preparation)));
    result
}
#[link(name = "kernel32")]
unsafe extern "system" {
    fn CreateMutexW(attributes: *const c_void, owner: i32, name: *const u16) -> usize;
    fn WaitForSingleObject(handle: usize, milliseconds: u32) -> u32;
    fn ReleaseMutex(handle: usize) -> i32;
}
static LAYER_LOCK: OnceLock<usize> = OnceLock::new();
pub(super) unsafe fn before(id: u32, call: &mut Call) {
    if id == 2 {
        unsafe { observe(id, call) };
    }
    if id != 4 {
        return;
    }
    // Same lock order for every caller, including unmodified renderers:
    // native engine mutex, then recursive process-wide layer-history mutex.
    let mutex = unsafe { ((call.args[0] + super::timing_profile::StateMutex) as *const usize).read_unaligned() };
    let global = *LAYER_LOCK.get_or_init(|| unsafe { CreateMutexW(ptr::null(), 0, ptr::null()) });
    if global == 0 || unsafe { WaitForSingleObject(mutex, 1000) } != 0 {
        if let Some(r) = active() {
            fail(&mut r.lock().unwrap_or_else(|e| e.into_inner()), 11);
        }
        return;
    }
    if unsafe { WaitForSingleObject(global, 1000) } != 0 {
        unsafe {
            ReleaseMutex(mutex);
        };
        if let Some(r) = active() {
            fail(&mut r.lock().unwrap_or_else(|e| e.into_inner()), 11);
        }
        return;
    }
    let address = (MODULE.load(Ordering::Acquire) as usize + super::timing_profile::DseDurationRandom) as *mut u32;
    let saved = unsafe { address.read_unaligned() };
    call.token = mutex;
    call.args[13] = (u64::from(saved) | (1u64 << 32)) as usize;
    if let Some(r) = active() {
        unsafe {
            address.write_unaligned(r.lock().unwrap_or_else(|e| e.into_inner()).layer_history);
        }
    }
}
pub(super) unsafe fn after(id: u32, call: &mut Call) {
    if matches!(id, 0 | 1) {
        unsafe { observe(id, call) };
    }
    if id == 3 && call.result != 0 {
        if let Some(r) = active() {
            let mut r = r.lock().unwrap_or_else(|e| e.into_inner());
            if !r.capture {
                let header = r.headers.entry(call.result).or_insert_with(|| {
                    let mut header = Box::new([0u64; 8]);
                    // Do not even read another render's mutable 20-byte history.
                    unsafe {
                        ptr::copy_nonoverlapping(
                            (call.result as *const u8).add(20),
                            header.as_mut_ptr().cast::<u8>().add(20),
                            44,
                        );
                    }
                    header[0] = 0;
                    header[1] = 0x461c4000ff7fffff;
                    header[2] &= 0xffffffff00000000;
                    header
                });
                call.result = header.as_ptr() as usize;
            }
        }
    }
    if id == 4 && call.token != 0 {
        let address = (MODULE.load(Ordering::Acquire) as usize + super::timing_profile::DseDurationRandom) as *mut u32;
        if let Some(r) = active() {
            r.lock().unwrap_or_else(|e| e.into_inner()).layer_history =
                unsafe { address.read_unaligned() };
            unsafe {
                address.write_unaligned(call.args[13] as u32);
            }
        }
        unsafe {
            ReleaseMutex(*LAYER_LOCK.get().unwrap());
            ReleaseMutex(call.token);
        }
    }
}
fn validate(notes: &[Note], plan: &[Event]) -> bool {
    let mut identities = std::collections::HashSet::new();
    !notes.is_empty()
        && notes.len() <= MAX_NOTES
        && plan.len() <= MAX_EVENTS
        && notes.iter().all(|n| {
            n.begin >= 0
                && n.end > n.begin
                && n.end < i64::from(i32::MAX) - 3087
                && n.pitch.is_finite()
                && n.ratio.is_finite()
                && (0.001..=32.0).contains(&n.ratio)
                && ((n.end - n.begin) as f64) * f64::from(n.ratio) <= 4096.0
                && n.policy <= 1
                && n.identity < 1_000_000
                && identities.insert(n.identity)
        })
        && plan
            .iter()
            .all(|e| (0..=32).contains(&e.count) && (0..=4).contains(&e.kind) && e.index >= 0)
        && {
            let mut locations = std::collections::HashSet::new();
            plan.iter().all(|e| locations.insert((e.section, e.index)))
        }
}
fn fingerprint(notes: &[Note], plan: &[Event]) -> Result<[u8; 32], i32> {
    let mut bytes = b"VOCALOIDPatcher.duration-selection.v1.contract2\0".to_vec();
    bytes.extend_from_slice(&(notes.len() as u32).to_le_bytes());
    bytes.extend_from_slice(&(plan.len() as u32).to_le_bytes());
    let mut notes = notes.iter().collect::<Vec<_>>();
    notes.sort_unstable_by_key(|n| n.identity);
    let mut plan = plan.iter().collect::<Vec<_>>();
    plan.sort_unstable_by_key(|e| (e.section, e.index));
    for n in notes {
        bytes.extend_from_slice(&n.begin.to_le_bytes());
        bytes.extend_from_slice(&n.end.to_le_bytes());
        bytes.extend_from_slice(&n.pitch.to_bits().to_le_bytes());
        bytes.extend_from_slice(&n.ratio.to_bits().to_le_bytes());
        bytes.extend_from_slice(&n.policy.to_le_bytes());
        bytes.extend_from_slice(&n.identity.to_le_bytes());
    }
    for e in plan {
        bytes.extend_from_slice(&e.section.to_le_bytes());
        bytes.extend_from_slice(&e.key);
        for v in [e.kind, e.count, e.gap, e.index] {
            bytes.extend_from_slice(&v.to_le_bytes());
        }
        for v in &e.halves[..e.count as usize * 2] {
            bytes.extend_from_slice(&v.to_le_bytes());
        }
    }
    super::timing_cache::digest(&bytes)
}
pub(super) fn cache_key(part: u64, native: [u8; 16]) -> Option<[u8; 16]> {
    let snapshot = parts()
        .read()
        .unwrap_or_else(|e| e.into_inner())
        .get(&part)
        .cloned()?;
    let mut bytes = native.to_vec();
    bytes.extend_from_slice(&snapshot.fingerprint);
    super::timing_cache::digest(&bytes)
        .ok()
        .map(|v| v[..16].try_into().unwrap())
}
#[unsafe(no_mangle)]
pub extern "C" fn v6_duration_abi_version() -> u32 {
    1
}
#[unsafe(no_mangle)]
pub extern "C" fn v6_duration_note_size() -> u32 {
    std::mem::size_of::<Note>() as u32
}
#[unsafe(no_mangle)]
pub extern "C" fn v6_duration_event_size() -> u32 {
    std::mem::size_of::<Event>() as u32
}
#[unsafe(no_mangle)]
pub extern "C" fn v6_duration_status_size() -> u32 {
    std::mem::size_of::<Status>() as u32
}
#[unsafe(no_mangle)]
pub unsafe extern "C" fn v6_duration_install(module: *mut u8) -> i32 {
    if !super::timing_profile::selected() { return -30; }
    let _guard = INSTALL.lock().unwrap_or_else(|e| e.into_inner());
    if READY.load(Ordering::Acquire) {
        return 0;
    }
    if module.is_null() {
        return -1;
    }
    let route = super::sampled_timing::v6_timing_install();
    if route < 0 {
        return route;
    }
    MODULE.store(module as u64, Ordering::Release);
    let mapping = unsafe { super::phoneme_timing::v6_phoneme_install(module) };
    if mapping < 0 {
        return mapping;
    }
    match unsafe { super::duration_bridge::install(module) } {
        Ok(()) => {
            READY.store(true, Ordering::Release);
            0
        }
        Err(e) => e,
    }
}
#[unsafe(no_mangle)]
pub unsafe extern "C" fn v6_duration_query_begin(notes: *const Note, count: i32) -> i32 {
    if !READY.load(Ordering::Acquire) || count <= 0 || count as usize > MAX_NOTES || notes.is_null()
    {
        return -1;
    }
    let notes = unsafe { slice::from_raw_parts(notes, count as usize) };
    if !validate(notes, &[]) {
        return -1;
    }
    QUERY.with(|v| {
        let mut v = v.borrow_mut();
        if v.is_some() {
            return -2;
        }
        let s = Arc::new(Snapshot {
            epoch: 0,
            notes: notes.into(),
            plan: Arc::from([]),
            fingerprint: [0; 32],
            status: Mutex::new(Status::default()),
        });
        *v = Some(runtime(s, true));
        0
    })
}
#[unsafe(no_mangle)]
pub unsafe extern "C" fn v6_duration_query_end(
    events: *mut Event,
    capacity: i32,
    count: *mut i32,
    output: *mut Status,
) -> i32 {
    let Some(r) = QUERY.with(|v| v.borrow_mut().take()) else {
        return -2;
    };
    let r = r.lock().unwrap_or_else(|e| e.into_inner());
    if count.is_null() || output.is_null() {
        return -1;
    }
    unsafe {
        *count = r.captured.len() as i32;
        *output = *r.snapshot.status.lock().unwrap_or_else(|e| e.into_inner());
    }
    if r.failure != 0 {
        return -10 - r.failure;
    }
    if capacity < r.captured.len() as i32 || (!r.captured.is_empty() && events.is_null()) {
        return -3;
    }
    if !r.captured.is_empty() {
        unsafe {
            ptr::copy_nonoverlapping(r.captured.as_ptr(), events, r.captured.len());
        }
    }
    0
}
#[unsafe(no_mangle)]
pub extern "C" fn v6_duration_query_count() -> i32 {
    QUERY.with(|v| {
        v.borrow().as_ref().map_or(-1, |r| {
            r.lock().unwrap_or_else(|e| e.into_inner()).captured.len() as i32
        })
    })
}
#[unsafe(no_mangle)]
pub extern "C" fn v6_duration_query_cancel() {
    QUERY.with(|v| v.borrow_mut().take());
}
#[unsafe(no_mangle)]
pub unsafe extern "C" fn v6_duration_set_part(
    part: u64,
    epoch: u64,
    notes: *const Note,
    count: i32,
    events: *const Event,
    num_events: i32,
) -> i32 {
    if part == 0
        || epoch == 0
        || notes.is_null()
        || count <= 0
        || count as usize > MAX_NOTES
        || events.is_null()
        || num_events <= 0
        || num_events as usize > MAX_EVENTS
    {
        return -1;
    }
    let notes = unsafe { slice::from_raw_parts(notes, count as usize) };
    let plan = unsafe { slice::from_raw_parts(events, num_events as usize) };
    if !validate(notes, plan) {
        return -1;
    }
    let fingerprint = match fingerprint(notes, plan) {
        Ok(v) => v,
        Err(e) => return e,
    };
    let mut parts = parts().write().unwrap_or_else(|e| e.into_inner());
    if parts.get(&part).is_some_and(|s| s.epoch >= epoch) {
        return -2;
    }
    parts.insert(
        part,
        Arc::new(Snapshot {
            epoch,
            notes: notes.into(),
            plan: plan.into(),
            fingerprint,
            status: Mutex::new(Status::default()),
        }),
    );
    0
}
#[unsafe(no_mangle)]
pub extern "C" fn v6_duration_remove_part(part: u64) {
    parts()
        .write()
        .unwrap_or_else(|e| e.into_inner())
        .remove(&part);
    ENGINES
        .get_or_init(Default::default)
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .retain(|_, v| v.0.0 != part);
}
#[unsafe(no_mangle)]
pub unsafe extern "C" fn v6_duration_status(part: u64, output: *mut Status) -> i32 {
    if output.is_null() {
        return -1;
    }
    let Some(s) = parts()
        .read()
        .unwrap_or_else(|e| e.into_inner())
        .get(&part)
        .cloned()
    else {
        return -2;
    };
    unsafe {
        *output = *s.status.lock().unwrap_or_else(|e| e.into_inner());
    }
    0
}

#[cfg(test)]
mod tests {
    use super::*;
    fn note(id: u32) -> Note {
        Note {
            begin: 100 + i64::from(id) * 200,
            end: 200 + i64::from(id) * 200,
            pitch: 0.0,
            ratio: 1.0,
            policy: 0,
            identity: id,
        }
    }
    fn event(index: i32) -> Event {
        Event {
            section: 1,
            key: [3; 32],
            kind: 0,
            count: 1,
            gap: 2,
            index,
            halves: [5; 64],
        }
    }
    fn snapshot(notes: &[Note]) -> Arc<Snapshot> {
        Arc::new(Snapshot {
            epoch: 1,
            notes: notes.into(),
            plan: Arc::from([]),
            fingerprint: [0; 32],
            status: Mutex::new(Status::default()),
        })
    }
    #[test]
    fn content_identity_covers_every_control_and_time_contract() {
        let a = note(0);
        let e = event(0);
        let base = fingerprint(&[a], &[e.clone()]).unwrap();
        for n in [
            Note { ratio: 0.5, ..a },
            Note { policy: 1, ..a },
            Note { begin: 101, ..a },
            Note { end: 201, ..a },
            Note { pitch: 100.0, ..a },
            Note { identity: 2, ..a },
        ] {
            assert_ne!(base, fingerprint(&[n], &[e.clone()]).unwrap());
        }
        let mut changed = e.clone();
        changed.halves[0] += 1;
        assert_ne!(base, fingerprint(&[a], &[changed]).unwrap());
        let mut changed = e.clone();
        changed.gap += 1;
        assert_ne!(base, fingerprint(&[a], &[changed]).unwrap());
        assert_eq!(
            fingerprint(&[a, note(1)], &[event(0), event(1)]),
            fingerprint(&[note(1), a], &[event(1), event(0)])
        );
    }
    #[test]
    fn malformed_or_ambiguous_snapshots_are_rejected() {
        let a = note(0);
        assert!(validate(&[a], &[event(0)]));
        assert!(!validate(&[a, a], &[]));
        assert!(!validate(&[a], &[event(0), event(0)]));
        for n in [
            Note { ratio: 0.0, ..a },
            Note {
                ratio: f32::NAN,
                ..a
            },
            Note {
                ratio: f32::INFINITY,
                ..a
            },
            Note { policy: 2, ..a },
            Note { end: i64::MAX, ..a },
        ] {
            assert!(!validate(&[n], &[]));
        }
    }
    #[test]
    fn exact_note_matching_rejects_ambiguous_coordinates() {
        let n = note(0);
        let mut data = [0u8; super::timing_profile::NoteStride];
        data[..4].copy_from_slice(&((n.begin + 3087) as i32).to_le_bytes());
        data[4..8].copy_from_slice(&100i32.to_le_bytes());
        data[8..12].copy_from_slice(&0f32.to_bits().to_le_bytes());
        data[super::timing_profile::NotePhoneCount] = 1;
        data[super::timing_profile::NotePhones..0x257].copy_from_slice(b"a\0");
        assert_eq!(
            unsafe { record(&[n], data.as_ptr().cast()) }
                .unwrap()
                .0
                .unwrap()
                .identity,
            0
        );
        assert!(unsafe { record(&[n, Note { identity: 1, ..n }], data.as_ptr().cast()) }.is_err());
    }
    #[test]
    fn timbre_history_is_private_across_threads_and_nested_render_scopes() {
        let source = Box::new([0x1234567890abcdefu64; 8]);
        let address = source.as_ptr() as usize;
        let threads = (0..8)
            .map(|index| {
                std::thread::spawn(move || {
                    let outer = runtime(snapshot(&[note(index)]), false);
                    ACTIVE.with(|v| v.replace(Some(outer.clone())));
                    let mut call = Call {
                        args: [0; 14],
                        caller: 0,
                        result: address,
                        token: 0,
                    };
                    unsafe {
                        after(3, &mut call);
                    }
                    assert_ne!(call.result, address);
                    unsafe {
                        (call.result as *mut u64).write(index as u64);
                    }
                    let inner = runtime(snapshot(&[note(index + 100)]), false);
                    let guard = EngineGuard(ACTIVE.with(|v| v.replace(Some(inner))));
                    let mut nested = Call {
                        args: [0; 14],
                        caller: 0,
                        result: address,
                        token: 0,
                    };
                    unsafe {
                        after(3, &mut nested);
                    }
                    assert_ne!(nested.result, call.result);
                    drop(guard);
                    assert_eq!(unsafe { (call.result as *const u64).read() }, index as u64);
                    assert_eq!(
                        ACTIVE.with(|v| v
                            .borrow()
                            .as_ref()
                            .unwrap()
                            .lock()
                            .unwrap()
                            .snapshot
                            .notes[0]
                            .identity),
                        index
                    );
                    ACTIVE.with(|v| v.replace(None));
                })
            })
            .collect::<Vec<_>>();
        for t in threads {
            t.join().unwrap();
        }
        assert_eq!(*source, [0x1234567890abcdefu64; 8]);
    }
}
