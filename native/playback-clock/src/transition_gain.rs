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
    events: Arc<[Gain]>,
    pub fingerprint: [u8; 32],
    calls: AtomicU64,
    applied: AtomicU64,
    pub failures: AtomicU64,
    primary: AtomicU64,
    auxiliary: AtomicU64,
    pub retired: AtomicBool,
}
impl Controls {
    pub fn new(events: &[Gain]) -> Result<Option<Arc<Self>>, i32> {
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
        if events.is_empty() {
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
        Ok(Some(Arc::new(Self {
            events: events.into(),
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
    fn event(&self, frame: i32) -> Option<&Gain> {
        self.events
            .get(self.events.partition_point(|g| g.end <= frame))
            .filter(|g| g.begin <= frame)
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
        let Some(event) = controls.event(time) else {
            return native_gain;
        };
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
            controls.failures.fetch_add(1, Ordering::Relaxed);
            return native_gain;
        }
        let get_row: unsafe extern "system" fn(usize, i32) -> usize = unsafe {
            std::mem::transmute(
                MODULE.load(Ordering::Acquire) + super::timing_profile::DseScheduleRow,
            )
        };
        let row = unsafe { ptr::read_unaligned(get_row(schedule, time) as *const usize) };
        let read = |offset: usize| unsafe {
            ptr::read_unaligned((row as *const u8).add(offset).cast::<i32>())
        };
        let valid = row != 0
            && (analysis == primary || analysis == auxiliary)
            && read(super::timing_profile::RowBegin) == event.begin
            && read(super::timing_profile::RowEnd) == event.end
            && read(super::timing_profile::RowFrom) == event.from
            && read(super::timing_profile::RowTo) == event.to
            && read(super::timing_profile::RowStage) == event.stage
            && i32::from(
                unsafe {
                    ptr::read_unaligned((row as *const u8).add(super::timing_profile::RowReverse))
                } != 0,
            ) == event.reverse;
        if !valid {
            controls.failures.fetch_add(1, Ordering::Relaxed);
            return native_gain;
        }
        controls.applied.fetch_add(1, Ordering::Relaxed);
        if analysis == primary {
            controls.primary.fetch_add(1, Ordering::Relaxed);
        } else {
            controls.auxiliary.fetch_add(1, Ordering::Relaxed);
        }
        native_gain * event.multiplier
    });
    unsafe { original(analysis, frame, a, b, gain) }
}
#[unsafe(no_mangle)]
pub extern "C" fn v6_transition_abi_version() -> u32 {
    1
}
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
    let controls = match Controls::new(values) {
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

#[cfg(test)]
mod tests {
    use super::*;
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
