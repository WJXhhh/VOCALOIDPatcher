//! Opt-in backend only. Scale the selected ART-driven frame contribution before
//! overlap-add; never rewrite DYN, source material or post-mix PCM windows.
use super::*;
use std::cell::RefCell;
use std::sync::atomic::{AtomicBool, AtomicUsize};

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct Gain {
    pub begin: i32,
    pub end: i32,
    pub from: i32,
    pub to: i32,
    pub stage: i32,
    pub reverse: i32,
    pub multiplier: f32,
    pub reserved: u32,
}
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct Progress {
    pub begin: i32, pub end: i32, pub from: i32, pub to: i32,
    pub stage: i32, pub reverse: i32, pub frame: i32, pub source: f32,
}
#[repr(C)]
#[derive(Default)]
pub struct Status {
    pub epoch: u64,
    pub calls: u64,
    pub applied: u64,
    pub failures: u64,
    pub primary: u64,
    pub auxiliary: u64,
}
pub struct Controls {
    first_failure: Mutex<Option<Failure>>,
    events: Arc<[Gain]>,
    progress: Arc<[Progress]>,
    pub fingerprint: [u8; 32],
    calls: AtomicU64,
    applied: AtomicU64,
    pub failures: AtomicU64,
    primary: AtomicU64,
    auxiliary: AtomicU64,
    pub retired: AtomicBool,
}
// Optional diagnostic ABI: retain only the first rejected frame, without pointers.
#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct Failure {
    pub reason: u32,
    pub frame: i32,
    pub analysis: i32,
    pub has_progress: u32,
    pub expected: [i32; 6],
    pub actual: [i32; 6],
    pub expected_source: u32,
    pub actual_source: u32,
}
impl Controls {
    #[cfg(test)]
    pub fn new(events: &[Gain]) -> Result<Option<Arc<Self>>, i32> {
        Self::with_progress(events, &[])
    }
    pub fn with_progress(events: &[Gain], progress: &[Progress]) -> Result<Option<Arc<Self>>, i32> {
        if events.len() > 4096
            || events.iter().any(|g| {
                g.begin < 0
                    || g.end <= g.begin
                    || !(0..256).contains(&g.from)
                    || !(0..256).contains(&g.to)
                    || !(0..128).contains(&g.stage)
                    || !(0..=1).contains(&g.reverse)
                    || !g.multiplier.is_finite()
                    || !(0.0..=4.0).contains(&g.multiplier)
                    || g.reserved != 0
            })
        {
            return Err(-1);
        }
        let mut events: Vec<_> = events
            .iter()
            .copied()
            .filter(|g| g.multiplier != 1.0)
            .collect();
        events.sort_by_key(|g| (g.begin, g.end, g.stage));
        if events.windows(2).any(|p| p[0].end > p[1].begin) {
            return Err(-1);
        }
        if progress.len() > 65536 || progress.iter().any(|p|
            p.begin < 0 || p.end <= p.begin || p.frame < p.begin || p.frame >= p.end ||
            !(0..256).contains(&p.from) || !(0..256).contains(&p.to) || !(0..128).contains(&p.stage) ||
            !(0..=1).contains(&p.reverse) || !p.source.is_finite() || !(0.0..=65535.0).contains(&p.source)) {
            return Err(-1);
        }
        let mut progress = progress.to_vec(); progress.sort_by_key(|p| p.frame);
        if progress.windows(2).any(|p| p[0].frame >= p[1].frame) { return Err(-1); }
        if events.is_empty() && progress.is_empty() {
            return Ok(None);
        }
        let mut bytes = Vec::new();
        for g in &mut events {
            if g.multiplier == 0.0 {
                g.multiplier = 0.0;
            }
            for i in [g.begin, g.end, g.from, g.to, g.stage, g.reverse] {
                bytes.extend_from_slice(&i.to_le_bytes());
            }
            bytes.extend_from_slice(&g.multiplier.to_le_bytes());
        }
        if !progress.is_empty() {
            bytes.extend_from_slice(b"onset.source-progress.v1\0");
            for p in &progress {
                for i in [p.begin,p.end,p.from,p.to,p.stage,p.reverse,p.frame] { bytes.extend_from_slice(&i.to_le_bytes()); }
                bytes.extend_from_slice(&p.source.to_bits().to_le_bytes());
            }
        }
        Ok(Some(Arc::new(Self {
            first_failure: Mutex::new(None),
            events: events.into(),
            progress: progress.into(),
            fingerprint: super::timing_cache::digest(&bytes)?,
            calls: AtomicU64::new(0),
            applied: AtomicU64::new(0),
            failures: AtomicU64::new(0),
            primary: AtomicU64::new(0),
            auxiliary: AtomicU64::new(0),
            retired: AtomicBool::new(false),
        })))
    }
    pub fn status(&self, epoch: u64) -> Status {
        Status {
            epoch,
            calls: self.calls.load(Ordering::Acquire),
            applied: self.applied.load(Ordering::Acquire),
            failures: self.failures.load(Ordering::Acquire),
            primary: self.primary.load(Ordering::Acquire),
            auxiliary: self.auxiliary.load(Ordering::Acquire),
        }
    }
    pub fn failure(&self) -> Failure {
        self.first_failure.lock().unwrap_or_else(|e| e.into_inner()).unwrap_or_default()
    }
    fn reject(&self, failure: Failure) {
        let mut first = self.first_failure.lock().unwrap_or_else(|e| e.into_inner());
        if first.is_none() { *first = Some(failure); }
        self.failures.fetch_add(1, Ordering::Relaxed);
    }
    fn event(&self, frame: i32) -> Option<&Gain> {
        self.events
            .get(self.events.partition_point(|g| g.end <= frame))
            .filter(|g| g.begin <= frame)
    }
    fn unconsumed_progress(&self, current: i32, end: i32) -> &[Progress] {
        if end <= current { return &[]; }
        let begin = self.progress.partition_point(|p| p.frame < current);
        let end = self.progress.partition_point(|p| p.frame < end);
        &self.progress[begin..end]
    }
}
thread_local! { static ACTIVE: RefCell<Option<(usize,Arc<Controls>)>> = const { RefCell::new(None) }; }
pub struct EngineGuard(Option<(usize, Arc<Controls>)>);
impl Drop for EngineGuard {
    fn drop(&mut self) {
        ACTIVE.with(|v| v.replace(self.0.take()));
    }
}
pub unsafe fn engine(engine: *mut c_void) -> EngineGuard {
    let binding = if ORIGINAL.load(Ordering::Acquire).is_null() {
        None
    } else {
        unsafe { super::sampled_timing::transition_binding(engine) }
    };
    EngineGuard(ACTIVE.with(|v| v.replace(binding)))
}
pub fn failed() -> bool {
    ACTIVE.with(|v| {
        v.borrow().as_ref().is_some_and(|(_, c)| {
            c.retired.load(Ordering::Acquire) || c.failures.load(Ordering::Acquire) != 0
        })
    })
}
// Mapper completion precedes source-frame fetch, including rows first created
// inside Step. A subsequent NOTE can replace an earlier temporary silence row
// without appending those frames again. Revisit unconsumed frames, deferring a
// row until its final ART identity exists. Synthesis still requires the exact
// identity and source bits before accepting the result. TLS remains per engine.
pub unsafe fn mapping_begin(parser: *mut c_void) -> Option<i32> {
    ACTIVE.with(|v| {
        let b = v.borrow(); let (state, c) = b.as_ref()?;
        if c.progress.is_empty() || *state + super::timing_profile::StateParser != parser as usize { return None; }
        Some(unsafe { ptr::read_unaligned((*state as *const u8).add(super::timing_profile::StateCurrentFrame).cast::<i32>()) })
    })
}
pub unsafe fn mapping_end(parser: *mut c_void, begin: Option<i32>) {
    let Some(begin) = begin else { return; };
    ACTIVE.with(|v| {
        let b = v.borrow(); let Some((state,c)) = b.as_ref() else { return; };
        if *state + super::timing_profile::StateParser != parser as usize || c.retired.load(Ordering::Acquire) { c.failures.fetch_add(1,Ordering::Relaxed); return; }
        let schedule = unsafe { ptr::read_unaligned(parser.cast::<usize>()) };
        let base = MODULE.load(Ordering::Acquire);
        let get_end: unsafe extern "system" fn(usize)->i32 = unsafe { std::mem::transmute(base+super::timing_profile::DseScheduleEnd) };
        let get_row: unsafe extern "system" fn(usize,i32)->usize = unsafe { std::mem::transmute(base+super::timing_profile::DseScheduleRow) };
        let end = unsafe { get_end(schedule) };
        for p in c.unconsumed_progress(begin, end) {
            let entry = unsafe { get_row(schedule,p.frame) };
            let row = if entry == 0 { 0 } else { unsafe { ptr::read_unaligned(entry as *const usize) } };
            let read = |offset| unsafe { ptr::read_unaligned((row as *const u8).add(offset).cast::<i32>()) };
            if row == 0 || read(super::timing_profile::RowBegin)!=p.begin || read(super::timing_profile::RowEnd)!=p.end ||
                read(super::timing_profile::RowFrom)!=p.from || read(super::timing_profile::RowTo)!=p.to || read(super::timing_profile::RowStage)!=p.stage ||
                i32::from(unsafe { ptr::read_unaligned((row as *const u8).add(super::timing_profile::RowReverse)) }!=0)!=p.reverse {
                // Future schedule space may still contain a temporary Sil
                // row. A later NOTE/mapper will finalize it. Do not touch that
                // row or report a failure before it can be consumed; the
                // synthesis check below rejects any unresolved target.
                continue;
            }
            unsafe { ptr::write_unaligned((entry as *mut u8).add(8).cast::<f32>(),p.source); }
            c.applied.fetch_add(1,Ordering::Relaxed);
        }
    });
}
type Synthesize = unsafe extern "system" fn(usize, usize, usize, usize, f32) -> usize;
static ORIGINAL: AtomicPtr<c_void> = AtomicPtr::new(ptr::null_mut());
static MODULE: AtomicUsize = AtomicUsize::new(0);
static INSTALL: Mutex<()> = Mutex::new(());
unsafe extern "system" fn synthesize(
    analysis: usize,
    frame: usize,
    a: usize,
    b: usize,
    native_gain: f32,
) -> usize {
    let original: Synthesize = unsafe { std::mem::transmute(ORIGINAL.load(Ordering::Acquire)) };
    let gain = ACTIVE.with(|v| {
        let borrowed = v.borrow();
        let Some((state, controls)) = borrowed.as_ref() else {
            return native_gain;
        };
        controls.calls.fetch_add(1, Ordering::Relaxed);
        let time = unsafe {
            ptr::read_unaligned(
                (*state as *const u8)
                    .add(super::timing_profile::StateCurrentFrame)
                    .cast::<i32>(),
            )
        };
        let event = controls.event(time);
        let progress = controls.progress.binary_search_by_key(&time, |p| p.frame).ok().map(|i| &controls.progress[i]);
        if event.is_none() && progress.is_none() {
            return native_gain;
        }
        let primary = unsafe {
            ptr::read_unaligned(
                (*state as *const u8)
                    .add(super::timing_profile::StateAnalysisPrimary)
                    .cast::<usize>(),
            )
        };
        let auxiliary = unsafe {
            ptr::read_unaligned(
                (*state as *const u8)
                    .add(super::timing_profile::StateAnalysisAuxiliary)
                    .cast::<usize>(),
            )
        };
        let schedule = unsafe {
            ptr::read_unaligned(
                (*state as *const u8)
                    .add(super::timing_profile::StateSchedule)
                    .cast::<usize>(),
            )
        };
        if schedule == 0 {
            controls.reject(Failure { reason: 1, frame: time, ..Default::default() });
            return native_gain;
        }
        let get_row: unsafe extern "system" fn(usize, i32) -> usize = unsafe {
            std::mem::transmute(
                MODULE.load(Ordering::Acquire) + super::timing_profile::DseScheduleRow,
            )
        };
        let entry = unsafe { get_row(schedule,time) };
        let row = if entry == 0 { 0 } else { unsafe { ptr::read_unaligned(entry as *const usize) } };
        let read = |offset: usize| unsafe {
            ptr::read_unaligned((row as *const u8).add(offset).cast::<i32>())
        };
        let identity = event.map(|p| (p.begin,p.end,p.from,p.to,p.stage,p.reverse))
            .or_else(||progress.map(|p|(p.begin,p.end,p.from,p.to,p.stage,p.reverse))).unwrap();
        let valid = row != 0
            && (analysis == primary || analysis == auxiliary)
            && read(super::timing_profile::RowBegin) == identity.0
            && read(super::timing_profile::RowEnd) == identity.1
            && read(super::timing_profile::RowFrom) == identity.2
            && read(super::timing_profile::RowTo) == identity.3
            && read(super::timing_profile::RowStage) == identity.4
            && i32::from(
                unsafe {
                    ptr::read_unaligned((row as *const u8).add(super::timing_profile::RowReverse))
                } != 0,
            ) == identity.5
            && progress.is_none_or(|p| unsafe { ptr::read_unaligned((entry as *const u8).add(8).cast::<f32>()) }.to_bits()==p.source.to_bits());
        if !valid {
            let actual = if row == 0 { [0; 6] } else {
                [read(super::timing_profile::RowBegin), read(super::timing_profile::RowEnd),
                 read(super::timing_profile::RowFrom), read(super::timing_profile::RowTo),
                 read(super::timing_profile::RowStage), i32::from(unsafe {
                     ptr::read_unaligned((row as *const u8).add(super::timing_profile::RowReverse))
                 } != 0)]
            };
            let expected = [identity.0,identity.1,identity.2,identity.3,identity.4,identity.5];
            controls.reject(Failure {
                reason: if row == 0 { 2 } else if analysis != primary && analysis != auxiliary { 3 }
                    else if actual != expected { 4 } else { 5 },
                frame: time, analysis: if analysis == primary { 1 } else if analysis == auxiliary { 2 } else { 0 },
                has_progress: u32::from(progress.is_some()), expected, actual,
                expected_source: progress.map_or(0, |p| p.source.to_bits()),
                actual_source: if entry == 0 { 0 } else { unsafe {
                    ptr::read_unaligned((entry as *const u8).add(8).cast::<u32>())
                } },
            });
            return native_gain;
        }
        controls.applied.fetch_add(1, Ordering::Relaxed);
        if analysis == primary {
            controls.primary.fetch_add(1, Ordering::Relaxed);
        } else {
            controls.auxiliary.fetch_add(1, Ordering::Relaxed);
        }
        native_gain * event.map_or(1.0, |e|e.multiplier)
    });
    unsafe { original(analysis, frame, a, b, gain) }
}
#[unsafe(no_mangle)]
pub extern "C" fn v6_transition_abi_version() -> u32 {
    2
}
#[unsafe(no_mangle)]
pub extern "C" fn v6_transition_progress_size() -> u32 { std::mem::size_of::<Progress>() as u32 }
#[unsafe(no_mangle)]
pub extern "C" fn v6_transition_gain_size() -> u32 {
    std::mem::size_of::<Gain>() as u32
}
#[unsafe(no_mangle)]
pub extern "C" fn v6_transition_status_size() -> u32 {
    std::mem::size_of::<Status>() as u32
}
/// # Safety
/// Managed caller verifies the full DSE/VSM tuple and module handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn v6_transition_install(module: *mut u8) -> i32 {
    if !super::timing_profile::selected() {
        return -30;
    }
    let _guard = INSTALL.lock().unwrap_or_else(|e| e.into_inner());
    if !ORIGINAL.load(Ordering::Acquire).is_null() {
        return 0;
    }
    if module.is_null() {
        return -1;
    }
    let prefix = [
        0x48, 0x89, 0x6c, 0x24, 0x18, 0x57, 0x48, 0x83, 0xec, 0x30, 0x48, 0x63, 0x41, 0x60, 0x48,
        0x8b, 0xf9,
    ];
    let entry = unsafe { module.add(super::timing_profile::DseTransitionSynthesis) };
    if unsafe { slice::from_raw_parts(entry, prefix.len()) } != prefix {
        return -2;
    }
    MODULE.store(module as usize, Ordering::Release);
    match unsafe {
        super::register_shift_hook::install_hook(
            entry,
            prefix.len(),
            synthesize as *const c_void,
            &ORIGINAL,
        )
    } {
        Ok(()) => 0,
        Err(e) => e,
    }
}
/// # Safety
/// Arrays are bounded and readable for this call, copied before publication.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn v6_transition_set_part(
    part: u64,
    epoch: u64,
    notes: *const super::sampled_timing::Note,
    count: i32,
    points: *const super::phoneme_timing::Point,
    num_points: i32,
    gains: *const Gain,
    num_gains: i32,
) -> i32 {
    unsafe { v6_transition_set_part_v2(part,epoch,notes,count,points,num_points,gains,num_gains,ptr::null(),0) }
}
/// # Safety
/// All bounded arrays are readable for the call and copied before publication.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn v6_transition_set_part_v2(
    part:u64, epoch:u64, notes:*const super::sampled_timing::Note, count:i32,
    points:*const super::phoneme_timing::Point, num_points:i32, gains:*const Gain, num_gains:i32,
    progress:*const Progress, num_progress:i32,
) -> i32 {
    if ORIGINAL.load(Ordering::Acquire).is_null() || !super::phoneme_timing::ready() {
        return -3;
    }
    if count <= 0
        || count > 100_000
        || notes.is_null()
        || !(2..=8192).contains(&num_points)
        || points.is_null()
        || !(0..=4096).contains(&num_gains)
        || (num_gains > 0 && gains.is_null())
        || !(0..=65536).contains(&num_progress) || (num_progress>0 && progress.is_null())
    {
        return -1;
    }
    let warp = match super::phoneme_timing::Warp::new(unsafe {
        slice::from_raw_parts(points, num_points as usize)
    }) {
        Ok(w) => w,
        Err(e) => return e,
    };
    let values = if num_gains == 0 {
        &[][..]
    } else {
        unsafe { slice::from_raw_parts(gains, num_gains as usize) }
    };
    let progress = if num_progress==0 { &[][..] } else { unsafe { slice::from_raw_parts(progress,num_progress as usize) } };
    let controls = match Controls::with_progress(values, progress) {
        Ok(c) => c,
        Err(e) => return e,
    };
    super::sampled_timing::set_controls(
        part,
        epoch,
        unsafe { slice::from_raw_parts(notes, count as usize) },
        Some(warp),
        controls,
    )
}
/// # Safety
/// Output is one writable Status value.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn v6_transition_status(part: u64, output: *mut Status) -> i32 {
    if output.is_null() {
        return -1;
    }
    unsafe {
        output.write(super::sampled_timing::transition_status(part));
    }
    0
}
#[unsafe(no_mangle)]
pub extern "C" fn v6_transition_remove_part(part: u64, epoch: u64) -> i32 {
    super::sampled_timing::remove_epoch(part, epoch)
}
#[unsafe(no_mangle)]
pub extern "C" fn v6_transition_failure_size() -> u32 { std::mem::size_of::<Failure>() as u32 }
/// # Safety
/// Output is one writable Failure value; absent snapshots return a zero value.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn v6_transition_failure(part: u64, output: *mut Failure) -> i32 {
    if output.is_null() { return -1; }
    unsafe { output.write(super::sampled_timing::transition_failure(part)); }
    0
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn first_failure_survives_auxiliary_rejection_without_changing_control_identity() {
        let controls = Controls::new(&[gain()]).unwrap().unwrap();
        let fingerprint = controls.fingerprint;
        controls.reject(Failure { reason: 4, frame: 100, analysis: 1,
            expected: [100,120,1,2,0,0], actual: [101,121,1,2,0,0], ..Default::default() });
        controls.reject(Failure { reason: 5, frame: 101, analysis: 2, ..Default::default() });
        assert_eq!(controls.failure().reason, 4);
        assert_eq!(controls.failure().actual, [101,121,1,2,0,0]);
        assert_eq!(controls.status(7).failures, 2);
        assert_eq!(controls.fingerprint, fingerprint);
        assert_eq!(std::mem::size_of::<Failure>(), 72);
    }
    #[test]
    fn future_art_is_revisited_after_placeholder_replacement() {
        let progress: Vec<_> = (3337..3376).map(|frame| Progress {
            begin:3337,end:3376,from:41,to:0,stage:0,reverse:0,frame,source:0.5,
        }).collect();
        let controls = Controls::with_progress(&[], &progress).unwrap().unwrap();
        // First NOTE reserves a temporary a -> Sil row through 3347.
        assert_eq!(controls.unconsumed_progress(3259,3347).len(),10);
        // Next NOTE replaces that row with l -> a. The earlier ten frames
        // are still unconsumed and must be revisited, not just the append.
        let ready = controls.unconsumed_progress(3259,3376);
        assert_eq!(ready.len(),39); assert_eq!(ready[0].frame,3337);
        // During Step, already consumed source frames are never rewritten.
        let later = controls.unconsumed_progress(3350,3376);
        assert_eq!(later.len(),26); assert_eq!(later[0].frame,3350);
        assert!(controls.unconsumed_progress(3376,3376).is_empty());
        assert!(controls.unconsumed_progress(3376,3300).is_empty());
    }
    #[test]
    fn source_progress_is_bounded_unique_and_in_the_immutable_cache_key() {
        let p = Progress {begin:100,end:120,from:1,to:2,stage:0,reverse:0,frame:100,source:0.5};
        let a = Controls::with_progress(&[], &[p]).unwrap().unwrap();
        assert!(a.events.is_empty()); assert_eq!(a.progress.len(),1);
        for changed in [Progress{source:0.6,..p},Progress{frame:101,..p},Progress{stage:1,..p}] {
            assert_ne!(a.fingerprint,Controls::with_progress(&[],&[changed]).unwrap().unwrap().fingerprint);
        }
        for bad in [Progress{source:f32::NAN,..p},Progress{source:-1.0,..p},Progress{frame:120,..p}] {
            assert!(Controls::with_progress(&[],&[bad]).is_err());
        }
        assert!(Controls::with_progress(&[],&[p,p]).is_err());
        assert!(Controls::with_progress(&[gain()],&[p]).unwrap().is_some());
    }
    fn gain() -> Gain {
        Gain {
            begin: 100,
            end: 120,
            from: 1,
            to: 2,
            stage: 0,
            reverse: 0,
            multiplier: 0.5,
            reserved: 0,
        }
    }
    #[test]
    fn stage_and_both_sides_are_in_the_cache_identity() {
        let base = gain();
        let a = Controls::new(&[base]).unwrap().unwrap();
        for other in [
            Gain { begin: 101, ..base },
            Gain { end: 121, ..base },
            Gain { from: 3, ..base },
            Gain { to: 4, ..base },
            Gain { stage: 1, ..base },
            Gain { reverse: 1, ..base },
            Gain {
                multiplier: 0.25,
                ..base
            },
        ] {
            assert_ne!(
                a.fingerprint,
                Controls::new(&[other]).unwrap().unwrap().fingerprint
            );
        }
        assert!(
            Controls::new(&[Gain {
                multiplier: 1.0,
                ..base
            }])
            .unwrap()
            .is_none()
        );
        assert_eq!(
            Controls::new(&[Gain {
                multiplier: 0.0,
                ..base
            }])
            .unwrap()
            .unwrap()
            .fingerprint,
            Controls::new(&[Gain {
                multiplier: -0.0,
                ..base
            }])
            .unwrap()
            .unwrap()
            .fingerprint
        );
        assert!(a.event(99).is_none());
        assert!(a.event(100).is_some());
        assert!(a.event(119).is_some());
        assert!(a.event(120).is_none());
    }
    #[test]
    fn malformed_and_overlapping_gain_plans_are_rejected() {
        let a = gain();
        for b in [
            Gain { end: 100, ..a },
            Gain { stage: -1, ..a },
            Gain {
                multiplier: f32::NAN,
                ..a
            },
            Gain { reserved: 1, ..a },
        ] {
            assert!(Controls::new(&[b]).is_err());
        }
        assert!(Controls::new(&[a, a]).is_err());
        assert!(
            Controls::new(&[
                a,
                Gain {
                    begin: 120,
                    end: 140,
                    ..a
                }
            ])
            .is_ok()
        );
    }
    #[test]
    fn retired_generation_fails_only_its_own_thread_scope() {
        let controls = Controls::new(&[gain()]).unwrap().unwrap();
        let guard = EngineGuard(ACTIVE.with(|v| v.replace(Some((1, controls.clone())))));
        assert!(!failed());
        assert!(!std::thread::spawn(failed).join().unwrap());
        controls.retired.store(true, Ordering::Release);
        assert!(failed());
        assert!(!std::thread::spawn(failed).join().unwrap());
        drop(guard);
        assert!(!failed());
    }
}
