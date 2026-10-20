//! Retiming at the input to native sample/frame mapping, before synthesis.
//! Anchors are exact native frames, not editor score-only offsets.
use super::*;
use std::cell::RefCell;

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct Point {
    pub from: i32,
    pub to: i32,
}

pub struct Warp {
    pub(super) points: Arc<[Point]>,
}
impl Warp {
    pub fn new(points: &[Point]) -> Result<Arc<Self>, i32> {
        if points.len() < 2
            || points.len() > 8192
            || points[0].from < 0
            || points[0].from != points[0].to
            || points.last().unwrap().from != points.last().unwrap().to
            || points
                .windows(2)
                .any(|p| p[0].from >= p[1].from || p[0].to >= p[1].to)
        {
            return Err(-1);
        }
        Ok(Arc::new(Self {
            points: points.into(),
        }))
    }
    fn map(&self, frame: i32) -> i32 {
        let index = self.points.partition_point(|p| p.from <= frame);
        if index == 0 || index == self.points.len() {
            return frame;
        }
        let a = self.points[index - 1];
        let b = self.points[index];
        a.to + ((f64::from(frame - a.from) * f64::from(b.to - a.to) / f64::from(b.from - a.from))
            .round() as i32)
    }
}

pub struct Context {
    warp: Arc<Warp>,
    cursor: Mutex<Cursor>,
}
#[derive(Default)]
struct Cursor {
    displacement: i32,
    last_actual: Option<i32>,
    calls: u64,
    changed: u64,
    failures: u64,
    last_left: i32,
    last_right: i32,
    failed_actual: i32,
    reason: i32,
    trace: [i32; 24],
    checkpoints: std::collections::VecDeque<(i32, i32)>,
    schedule_checkpoints: std::collections::VecDeque<(i32, i32, i32)>,
}
impl Context {
    pub fn new(warp: Arc<Warp>) -> Arc<Self> {
        Arc::new(Self {
            warp,
            cursor: Mutex::new(Cursor::default()),
        })
    }
    pub fn failures(&self) -> u64 {
        self.cursor
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .failures
    }
    pub(super) fn snapshot(&self) -> Status {
        let c = self.cursor.lock().unwrap_or_else(|e| e.into_inner());
        Status { calls:c.calls, changed:c.changed, failures:c.failures, left:c.last_left, right:c.last_right,
            actual:c.failed_actual, displacement:c.displacement, reason:c.reason, reserved:0, trace:c.trace }
    }
    pub(super) fn reset_schedule(&self) {
        let mut cursor = self.cursor.lock().unwrap_or_else(|e| e.into_inner());
        // DSE can discard its initial frame schedule before planning the notes.
        // Reset coordinates at that actual lifecycle boundary, retaining all
        // counters and failures for the enclosing render/query scope.
        cursor.displacement = 0;
        cursor.last_actual = None;
        cursor.checkpoints.clear();
        cursor.schedule_checkpoints.clear();
    }
    unsafe fn schedule_halves(&self, schedule: usize, left: i32, right: i32) -> Option<(i32, i32)> {
        // DSE's segment count is decremented by pop-back as well as cleared by
        // reset. An empty schedule starts at the original frame zero. Testing
        // the count distinguishes this from a quantized zero-length segment.
        let count = unsafe { ptr::read_unaligned((schedule as *const u8).add(super::timing_profile::ScheduleMappedCount).cast::<i32>()) };
        let end: unsafe extern "system" fn(usize) -> i32 =
            unsafe { std::mem::transmute(GET_END.load(Ordering::Acquire)) };
        self.scheduled_halves(count, unsafe { end(schedule) }, left, right)
    }
    fn scheduled_halves(&self, count: i32, actual: i32, left: i32, right: i32) -> Option<(i32, i32)> {
        if count == 0 { self.reset_schedule(); }
        self.halves_at(actual, left, right, Some(count))
    }
    #[cfg(test)]
    pub(super) fn halves(&self, actual: i32, left: i32, right: i32) -> Option<(i32, i32)> {
        self.halves_at(actual, left, right, None)
    }
    fn halves_at(&self, actual: i32, left: i32, right: i32, count: Option<i32>) -> Option<(i32, i32)> {
        let mut cursor = self.cursor.lock().unwrap_or_else(|e| e.into_inner());
        cursor.calls += 1;
        cursor.last_left = left;
        cursor.last_right = right;
        let t = ((cursor.calls - 1) as usize).min(3) * 6;
        let record = cursor.calls <= 3 || cursor.failures == 0;
        if record {
            let d = cursor.displacement;
            cursor.trace[t..t + 4].copy_from_slice(&[actual, left, right, d]);
        }
        // Native planning removes and rebuilds the last transition when a
        // following NOTE arrives. Restore only an observed schedule boundary;
        // never guess an inverse through frame quantization or a silent gap.
        // Front consumption may also decrease the work-item count. A count
        // change alone does not prove pop-back; the native end must rewind.
        let rewound = cursor.last_actual.is_some_and(|v| v > actual);
        if rewound {
            // A compressed sample's split and end can quantize to the same
            // output frame. The native work-item count identifies the exact
            // previously observed dispatch start after pop-back, without
            // choosing an inverse through that ambiguous frame coordinate.
            let starts: Vec<i32> = cursor.schedule_checkpoints.iter()
                .filter(|p| Some(p.0) == count && p.1 == actual).map(|p| p.2).collect();
            let originals: Vec<i32> = if starts.is_empty() {
                cursor.checkpoints.iter().filter(|p| p.0 == actual).map(|p| p.1).collect()
            } else { starts };
            if let Some(&original) = originals
                .first()
                .filter(|&&v| originals.iter().all(|&p| p == v))
            {
                cursor.displacement = actual - original;
                cursor.last_actual = Some(actual);
                cursor.checkpoints.retain(|p| p.1 <= original);
                cursor.schedule_checkpoints.retain(|p| p.2 <= original);
            }
        }
        if left < 0 || right < 0 || cursor.last_actual.is_some_and(|v| v > actual) {
            cursor.failures += 1;
            cursor.failed_actual = actual;
            cursor.reason = 1;
            return None;
        }
        let Some(begin) = actual.checked_sub(cursor.displacement) else {
            cursor.failures += 1;
            return None;
        };
        let Some(split) = begin.checked_add(left) else {
            cursor.failures += 1;
            return None;
        };
        let Some(end) = split.checked_add(right) else {
            cursor.failures += 1;
            return None;
        };
        let start_to = self.warp.map(begin);
        let split_to = self.warp.map(split);
        let end_to = self.warp.map(end);
        if record {
            cursor.trace[t + 4] = start_to;
            cursor.trace[t + 5] = end_to;
        }
        if start_to != actual || split_to < start_to || end_to < split_to {
            cursor.failures += 1;
            cursor.failed_actual = actual;
            cursor.reason = 2;
            return None;
        }
        let result = (split_to - start_to, end_to - split_to);
        cursor.displacement = end_to - end;
        cursor.last_actual = Some(end_to);
        if let Some(count) = count {
            let checkpoint = (count, actual, begin);
            if !cursor.schedule_checkpoints.contains(&checkpoint) {
                cursor.schedule_checkpoints.push_back(checkpoint);
            }
            while cursor.schedule_checkpoints.len() > 128 {
                cursor.schedule_checkpoints.pop_front();
            }
        }
        for point in [(start_to, begin), (split_to, split), (end_to, end)] {
            if !cursor.checkpoints.contains(&point) {
                cursor.checkpoints.push_back(point);
            }
        }
        while cursor.checkpoints.len() > 128 {
            cursor.checkpoints.pop_front();
        }
        if result != (left, right) {
            cursor.changed += 1;
        }
        Some(result)
    }
}

thread_local! { static QUERY: RefCell<Option<Arc<Context>>> = const { RefCell::new(None) }; }
thread_local! { static MAPPING: Cell<bool> = const { Cell::new(false) }; }
static ORIGINAL: AtomicPtr<c_void> = AtomicPtr::new(ptr::null_mut());
static ORIGINAL_STATIONARY: AtomicPtr<c_void> = AtomicPtr::new(ptr::null_mut());
static ORIGINAL_ALLOCATE: AtomicPtr<c_void> = AtomicPtr::new(ptr::null_mut());
static GET_END: AtomicPtr<c_void> = AtomicPtr::new(ptr::null_mut());
static INSTALL_LOCK: Mutex<()> = Mutex::new(());
pub fn ready() -> bool {
    !ORIGINAL.load(Ordering::Acquire).is_null()
        && !ORIGINAL_STATIONARY.load(Ordering::Acquire).is_null()
        && !ORIGINAL_ALLOCATE.load(Ordering::Acquire).is_null()
}
type Mapper = unsafe extern "system" fn(
    *mut c_void,
    usize,
    usize,
    usize,
    i32,
    i32,
    i32,
    i32,
    usize,
    i32,
    i32,
    u8,
) -> usize;
type Stationary =
    unsafe extern "system" fn(*mut c_void, i32, usize, i32, u8, usize, i32, i32, i32) -> i32;
type Allocate = unsafe extern "system" fn(usize, usize, i32) -> usize;
struct MappingGuard(bool);
impl MappingGuard {
    fn enter() -> Self {
        Self(MAPPING.with(|v| v.replace(true)))
    }
}
impl Drop for MappingGuard {
    fn drop(&mut self) {
        MAPPING.with(|v| v.set(self.0));
    }
}

unsafe extern "system" fn allocate(schedule: usize, output: usize, duration: i32) -> usize {
    // Streaming keeps the schedule ahead of the synthesis clock with one-frame
    // silence segments. Those are already in output coordinates, not source
    // phoneme durations. Warping them can collapse a required segment to zero.
    let context = if MAPPING.with(|v| v.get()) {
        None
    } else {
        QUERY
            .with(|v| v.borrow().clone())
            .or_else(|| super::sampled_timing::phoneme_schedule_context(schedule))
    };
    let duration = if let Some(context) = context.filter(|_| duration > 0) {
        // The exact verified DSE image has one output-clock padding callsite.
        // Its silence wrapper can also be intercepted by duration_bridge,
        // which replaces the stack return address until the wrapper returns.
        // Use its thread-owned original caller before considering stack walk.
        // This verified callsite always appends one frame. Keep the expensive
        // standalone stack walk out of ordinary multi-frame allocations.
        let mut output_padding = false;
        if duration == 1 && QUERY.with(|v| v.borrow().is_none()) {
            let base = GET_END.load(Ordering::Acquire) as usize - super::timing_profile::DseScheduleEnd;
            if let Some(caller) = super::duration_bridge::original_caller(2) {
                output_padding = caller == base + super::timing_profile::DsePaddingCaller;
            } else {
                // Standalone timing can be installed without the duration
                // bridge. In that case the native caller is still on-stack.
                unsafe extern "system" {
                    fn RtlCaptureStackBackTrace(skip: u32, count: u32, frames: *mut *mut c_void, hash: *mut u32) -> u16;
                }
                let mut frames = [ptr::null_mut(); 8];
                let count = unsafe { RtlCaptureStackBackTrace(0, 8, frames.as_mut_ptr(), ptr::null_mut()) };
                output_padding = frames[..usize::from(count)].iter().any(|&frame| frame as usize == base + super::timing_profile::DsePaddingCaller);
            }
        }
        if output_padding {
            duration
        } else {
            unsafe { context.schedule_halves(schedule, duration, 0) }
                .map(|v| v.0)
                .unwrap_or(duration)
        }
    } else {
        duration
    };
    let original: Allocate =
        unsafe { std::mem::transmute(ORIGINAL_ALLOCATE.load(Ordering::Acquire)) };
    unsafe { original(schedule, output, duration) }
}

unsafe extern "system" fn stationary(
    parser: *mut c_void,
    duration: i32,
    phone: usize,
    limit: i32,
    mode: u8,
    data: usize,
    language: i32,
    singer: i32,
    value: i32,
) -> i32 {
    let (duration, _) = unsafe { super::duration_selection::mapping(4, phone, 0, duration, 0, language, singer) };
    let context = QUERY
        .with(|v| v.borrow().clone())
        .or_else(|| super::sampled_timing::phoneme_context(parser as usize));
    let (duration_to, limit_to) = if let Some(context) = context.filter(|_| duration > 0) {
        let schedule = unsafe { ptr::read_unaligned(parser.cast::<usize>()) };
        let mapped = unsafe { context.schedule_halves(schedule, duration, 0) }
            .map(|v| v.0)
            .unwrap_or(duration);
        (
            mapped,
            (f64::from(limit) * f64::from(mapped) / f64::from(duration)).round() as i32,
        )
    } else {
        (duration, limit)
    };
    let original: Stationary =
        unsafe { std::mem::transmute(ORIGINAL_STATIONARY.load(Ordering::Acquire)) };
    let _guard = MappingGuard::enter();
    unsafe {
        original(
            parser,
            duration_to,
            phone,
            limit_to,
            mode,
            data,
            language,
            singer,
            value,
        )
    }
}

unsafe extern "system" fn mapper(
    parser: *mut c_void,
    unit: usize,
    from: usize,
    to: usize,
    left: i32,
    right: i32,
    language: i32,
    singer: i32,
    ratios: usize,
    a: i32,
    b: i32,
    mode: u8,
) -> usize {
    let (left, right) = unsafe { super::duration_selection::mapping(3, from, to, left, right, language, singer) };
    let context = QUERY
        .with(|v| v.borrow().clone())
        .or_else(|| super::sampled_timing::phoneme_context(parser as usize));
    let valid = unit != 0
        && unsafe { ptr::read_unaligned(unit as *const usize) } != 0
        && unsafe { ptr::read_unaligned((unit as *const u8).add(8).cast::<i32>()) } >= 0
        && left >= 0
        && right >= 0
        && left.saturating_add(right) > 0;
    let (left, right) = if let Some(context) = context.filter(|_| valid) {
        let schedule = unsafe { ptr::read_unaligned(parser.cast::<usize>()) };
        unsafe { context.schedule_halves(schedule, left, right) }
            .unwrap_or((left, right))
    } else {
        (left, right)
    };
    let original: Mapper = unsafe { std::mem::transmute(ORIGINAL.load(Ordering::Acquire)) };
    let _guard = MappingGuard::enter();
    let progress_begin = unsafe { super::transition_gain::mapping_begin(parser) };
    let result = unsafe {
        original(
            parser, unit, from, to, left, right, language, singer, ratios, a, b, mode,
        )
    };
    unsafe { super::transition_gain::mapping_end(parser, progress_begin); }
    result
}

#[unsafe(no_mangle)]
pub extern "C" fn v6_phoneme_abi_version() -> u32 {
    1
}
#[unsafe(no_mangle)]
pub extern "C" fn v6_phoneme_point_size() -> u32 {
    std::mem::size_of::<Point>() as u32
}
#[unsafe(no_mangle)]
pub extern "C" fn v6_phoneme_status_size() -> u32 {
    std::mem::size_of::<Status>() as u32
}
/// # Safety
/// The caller verifies the exact loaded DSE image hash before installation.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn v6_phoneme_install(module: *mut u8) -> i32 {
    if !super::timing_profile::selected() { return -30; }
    let _lock = INSTALL_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    if ready() {
        return 0;
    }
    if module.is_null() {
        return -1;
    }
    let signature = [
        0x4c, 0x8b, 0xdc, 0x4d, 0x89, 0x4b, 0x20, 0x4d, 0x89, 0x43, 0x18, 0x49, 0x89, 0x53, 0x10,
    ];
    let target = unsafe { module.add(super::timing_profile::DseMapper) };
    let sustain = unsafe { module.add(super::timing_profile::DseStationary) };
    let sustain_sig = [
        0x48, 0x8b, 0xc4, 0x55, 0x53, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57,
    ];
    let allocator = unsafe { module.add(super::timing_profile::DseAllocate) };
    let allocate_sig = [
        0x48, 0x89, 0x5c, 0x24, 0x08, 0x48, 0x89, 0x6c, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18,
    ];
    if unsafe { slice::from_raw_parts(target, signature.len()) } != signature
        || unsafe { slice::from_raw_parts(sustain, sustain_sig.len()) } != sustain_sig
        || unsafe { slice::from_raw_parts(allocator, allocate_sig.len()) } != allocate_sig
    {
        return -2;
    }
    GET_END.store(unsafe { module.add(super::timing_profile::DseScheduleEnd) }.cast(), Ordering::Release);
    if let Err(error) = unsafe {
        super::register_shift_hook::install_hook(
            target,
            signature.len(),
            mapper as *const c_void,
            &ORIGINAL,
        )
    } {
        return error;
    }
    if let Err(error) = unsafe {
        super::register_shift_hook::install_hook(
            sustain,
            sustain_sig.len(),
            stationary as *const c_void,
            &ORIGINAL_STATIONARY,
        )
    } {
        return error;
    }
    match unsafe {
        super::register_shift_hook::install_hook(
            allocator,
            allocate_sig.len(),
            allocate as *const c_void,
            &ORIGINAL_ALLOCATE,
        )
    } {
        Ok(()) => 0,
        Err(error) => error,
    }
}
/// # Safety
/// points references count readable Point values; this scope is thread-owned.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn v6_phoneme_begin(points: *const Point, count: i32) -> i32 {
    if count < 2 || count > 8192 || points.is_null() {
        return -1;
    }
    let warp = match Warp::new(unsafe { slice::from_raw_parts(points, count as usize) }) {
        Ok(w) => w,
        Err(e) => return e,
    };
    QUERY.with(|v| {
        if v.borrow().is_some() {
            -2
        } else {
            v.replace(Some(Context::new(warp)));
            0
        }
    })
}
#[repr(C)]
#[derive(Default)]
pub struct Status {
    pub calls: u64,
    pub changed: u64,
    pub failures: u64,
    pub left: i32,
    pub right: i32,
    pub actual: i32,
    pub displacement: i32,
    pub reason: i32,
    pub reserved: i32,
    pub trace: [i32; 24],
}
/// # Safety
/// output references a writable Status, on the thread which began the scope.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn v6_phoneme_end(output: *mut Status) -> i32 {
    let context = QUERY.with(|v| v.replace(None));
    let Some(context) = context else {
        return -1;
    };
    let c = context.cursor.lock().unwrap_or_else(|e| e.into_inner());
    if !output.is_null() {
        unsafe {
            output.write(Status {
                calls: c.calls,
                changed: c.changed,
                failures: c.failures,
                left: c.last_left,
                right: c.last_right,
                actual: c.failed_actual,
                displacement: c.displacement,
                reason: c.reason,
                reserved: 0,
                trace: c.trace,
            });
        }
    }
    if c.failures != 0 { -2 } else { 0 }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn quantized_silence_bridge_rebuild_uses_native_work_item_start() {
        for (split, target) in [(3292, 3293), (3296, 3295)] {
            let mut points = vec![Point { from: 0, to: 0 }, Point { from: 3290, to: 3290 }, Point { from: 3292, to: 3292 },
                Point { from: 3296, to: 3296 }, Point { from: 3386, to: 3386 }];
            points.iter_mut().find(|p| p.from == split).unwrap().to = target;
            let warp = Warp::new(&points).unwrap();
            let context = Context::new(warp);
            let first = context.scheduled_halves(5, 3290, 2, 1).unwrap();
            let incoming_begin = 3290 + first.0 + first.1;
            let incoming = context.scheduled_halves(6, incoming_begin, 1, 1).unwrap();
            assert_eq!(incoming.1, 0);
            let begin = incoming_begin + incoming.0 + incoming.1;
            let original = context.scheduled_halves(7, begin, 1, 15).unwrap();
            let future = begin + original.0 + original.1;
            context.scheduled_halves(8, future, 43, 32).unwrap();
            assert!(context.cursor.lock().unwrap().checkpoints.iter()
                .filter(|p| p.0 == begin).map(|p| p.1).collect::<Vec<_>>().len() > 1);
            assert_eq!(context.scheduled_halves(7, begin, 1, 15), Some(original));
            assert_eq!(context.failures(), 0);
            // Neither an unrelated ordinal nor an unobserved frame may
            // select one of the ambiguous source positions.
            context.scheduled_halves(8, future, 43, 32).unwrap();
            assert!(context.scheduled_halves(4, begin, 1, 15).is_none());
            assert_eq!(context.failures(), 1);
        }
    }
    #[test]
    fn front_consumption_does_not_rewind_a_quantized_source_cursor() {
        let context = Context::new(Warp::new(&[
            Point { from: 0, to: 0 }, Point { from: 100, to: 100 },
            Point { from: 104, to: 101 }, Point { from: 110, to: 110 }
        ]).unwrap());
        assert_eq!(context.scheduled_halves(5, 100, 2, 0), Some((1, 0)));
        assert_eq!(context.scheduled_halves(6, 101, 1, 0), Some((0, 0)));
        assert_eq!(context.cursor.lock().unwrap().displacement, -2);
        assert_eq!(context.scheduled_halves(1, 101, 1, 0), Some((0, 0)));
        assert_eq!(context.cursor.lock().unwrap().displacement, -3);
        assert_eq!(context.failures(), 0);
    }
    #[test]
    fn work_item_history_is_bounded_private_and_cleared_with_the_schedule() {
        let warp = Warp::new(&[Point { from: 0, to: 0 }, Point { from: 2000, to: 2000 }]).unwrap();
        let context = Context::new(warp.clone());
        for frame in 0..300 {
            assert_eq!(context.scheduled_halves(frame + 1, frame, 1, 0), Some((1, 0)));
        }
        assert_eq!(context.cursor.lock().unwrap().schedule_checkpoints.len(), 128);
        let other = Context::new(warp);
        assert!(other.cursor.lock().unwrap().schedule_checkpoints.is_empty());
        assert_eq!(context.scheduled_halves(0, 0, 1, 0), Some((1, 0)));
        assert_eq!(context.cursor.lock().unwrap().schedule_checkpoints.len(), 1);
        assert_eq!(context.failures(), 0);
    }
    #[test]
    fn empty_schedule_restarts_coordinates_after_quantized_warmup() {
        let warp = Warp::new(&[
            Point { from: 86, to: 86 },
            Point { from: 3313, to: 3293 },
            Point { from: 3345, to: 3345 },
        ]).unwrap();
        let context = Context::new(warp);
        let mut actual = 0;
        for _ in 0..167 {
            actual += context.halves(actual, 1, 0).unwrap().0;
        }
        assert_eq!(actual, 166);
        assert_eq!(context.cursor.lock().unwrap().displacement, -1);
        context.reset_schedule();
        assert_eq!(context.halves(0, 3313, 32), Some((3293, 52)));
        assert_eq!(context.failures(), 0);
        // A true invalid rewind must still fail, and resetting must not erase it.
        assert!(context.halves(117, 1, 1).is_none());
        context.reset_schedule();
        assert_eq!(context.failures(), 1);
        assert_eq!(context.cursor.lock().unwrap().calls, 169);
    }
    #[test]
    fn mapping_preserves_anchors_and_rejects_crossing() {
        let warp = Warp::new(&[
            Point { from: 100, to: 100 },
            Point { from: 120, to: 130 },
            Point { from: 160, to: 160 },
        ])
        .unwrap();
        assert_eq!(warp.map(90), 90);
        assert_eq!(warp.map(120), 130);
        assert_eq!(warp.map(160), 160);
        let c = Context::new(warp);
        assert_eq!(c.halves(100, 20, 20), Some((30, 15)));
        assert_eq!(c.halves(145, 10, 10), Some((8, 7)));
        assert!(
            Warp::new(&[
                Point { from: 1, to: 1 },
                Point { from: 2, to: 4 },
                Point { from: 3, to: 3 }
            ])
            .is_err()
        );
    }
    #[test]
    fn native_transition_rebuild_restores_only_observed_checkpoints() {
        let warp = Warp::new(&[
            Point { from: 100, to: 100 },
            Point { from: 120, to: 123 },
            Point { from: 160, to: 160 },
        ])
        .unwrap();
        let context = Context::new(warp);
        assert_eq!(context.halves(100, 20, 20), Some((23, 19)));
        assert_eq!(context.halves(123, 10, 10), Some((9, 10)));
        assert_eq!(context.failures(), 0);
        assert!(context.halves(117, 1, 1).is_none());
        assert_eq!(context.failures(), 1);
    }
}
