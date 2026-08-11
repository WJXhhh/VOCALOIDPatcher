use std::ffi::c_void;
use std::ptr;
use std::slice;
#[cfg(windows)]
use std::sync::atomic::AtomicPtr;
use std::sync::atomic::{AtomicI32, AtomicI64, AtomicU64, Ordering};
use std::sync::{Mutex, OnceLock};

const ABI_VERSION: u32 = 8;
const HARD_RESYNC_SECONDS: f64 = 0.080;
const BACKWARD_TOLERANCE_SECONDS: f64 = 0.020;
const MAX_PHASE_STEP_SECONDS: f64 = 0.006;
const PHASE_GAIN: f64 = 0.20;
const LATENCY_TIME_CONSTANT_SECONDS: f64 = 0.35;
const STALE_OBSERVATION_SECONDS: f64 = 0.25;
const RATE_ESTIMATION_WINDOW_SECONDS: f64 = 2.0;
const RATE_SMOOTHING_GAIN: f64 = 0.10;
const MIN_PLAYBACK_RATE: f64 = 0.999;
const MAX_PLAYBACK_RATE: f64 = 1.001;

const SNAPSHOT_INITIALIZED: u32 = 1;
const SNAPSHOT_STALE: u32 = 1 << 1;

const BREATH_EVENT_CAPACITY: usize = 256;

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct BreathEvent {
    pub sequence: u64,
    pub part_handle: u64,
    pub begin_frame: i64,
    pub end_frame: i64,
}

#[derive(Debug)]
struct BreathEventQueue {
    events: [BreathEvent; BREATH_EVENT_CAPACITY],
    begin: usize,
    length: usize,
    next_sequence: u64,
}

impl Default for BreathEventQueue {
    fn default() -> Self {
        Self {
            events: [BreathEvent::default(); BREATH_EVENT_CAPACITY],
            begin: 0,
            length: 0,
            next_sequence: 1,
        }
    }
}

impl BreathEventQueue {
    fn push(&mut self, part_handle: u64, begin_frame: i64, end_frame: i64) -> bool {
        for offset in 0..self.length {
            let index = (self.begin + offset) % BREATH_EVENT_CAPACITY;
            let event = &mut self.events[index];
            if event.part_handle == part_handle
                && begin_frame <= event.end_frame
                && end_frame >= event.begin_frame
            {
                let new_begin = event.begin_frame.min(begin_frame);
                let new_end = event.end_frame.max(end_frame);
                let changed = new_begin != event.begin_frame || new_end != event.end_frame;
                event.begin_frame = new_begin;
                event.end_frame = new_end;
                return changed;
            }
        }

        let event = BreathEvent {
            sequence: self.next_sequence,
            part_handle,
            begin_frame,
            end_frame,
        };
        self.next_sequence = self.next_sequence.wrapping_add(1).max(1);

        if self.length == BREATH_EVENT_CAPACITY {
            self.events[self.begin] = event;
            self.begin = (self.begin + 1) % BREATH_EVENT_CAPACITY;
            return true;
        }

        let end = (self.begin + self.length) % BREATH_EVENT_CAPACITY;
        self.events[end] = event;
        self.length += 1;
        true
    }

    fn pop(&mut self) -> Option<BreathEvent> {
        if self.length == 0 {
            return None;
        }
        let event = self.events[self.begin];
        self.begin = (self.begin + 1) % BREATH_EVENT_CAPACITY;
        self.length -= 1;
        Some(event)
    }

    fn clear(&mut self) {
        self.begin = 0;
        self.length = 0;
    }
}

static BREATH_EVENTS: OnceLock<Mutex<BreathEventQueue>> = OnceLock::new();

static BREATH_INSTALL_RESULT: AtomicI32 = AtomicI32::new(i32::MIN);
static BREATH_TARGET_RVA: AtomicU64 = AtomicU64::new(0);
static BREATH_CORE_TARGET_RVA: AtomicU64 = AtomicU64::new(0);
static BREATH_CORE_CALLS: AtomicU64 = AtomicU64::new(0);
static BREATH_MAPPED_CONTEXTS: AtomicU64 = AtomicU64::new(0);
static BREATH_CONTEXT_MISSES: AtomicU64 = AtomicU64::new(0);
static BREATH_HOOK_CALLS: AtomicU64 = AtomicU64::new(0);
static BREATH_SUCCESSFUL_BLOCKS: AtomicU64 = AtomicU64::new(0);
static BREATH_OUTPUT_SAMPLES: AtomicU64 = AtomicU64::new(0);
static BREATH_OUTPUT_PEAK: AtomicU64 = AtomicU64::new(0);
static BREATH_QUEUED_EVENTS: AtomicU64 = AtomicU64::new(0);
static BREATH_DROPPED_EVENTS: AtomicU64 = AtomicU64::new(0);
static BREATH_INVALID_CALLS: AtomicU64 = AtomicU64::new(0);
static BREATH_LAST_PART_HANDLE: AtomicU64 = AtomicU64::new(0);
static BREATH_LAST_BEGIN_FRAME: AtomicI64 = AtomicI64::new(-1);
static BREATH_LAST_END_FRAME: AtomicI64 = AtomicI64::new(-1);
static BREATH_LAST_RESULT: AtomicI32 = AtomicI32::new(i32::MIN);

static DSE_INSTALL_RESULT: AtomicI32 = AtomicI32::new(i32::MIN);
static DSE_VTABLE_RVA: AtomicU64 = AtomicU64::new(0);
static DSE_CREATE_BUFFER_CALLS: AtomicU64 = AtomicU64::new(0);
static DSE_ADD_EVENT_CALLS: AtomicU64 = AtomicU64::new(0);
static DSE_SET_PREROLL_CALLS: AtomicU64 = AtomicU64::new(0);
static DSE_START_CALLS: AtomicU64 = AtomicU64::new(0);
static DSE_STOP_CALLS: AtomicU64 = AtomicU64::new(0);
static DSE_STEP_CALLS: AtomicU64 = AtomicU64::new(0);
static DSE_STEP_SUCCESSES: AtomicU64 = AtomicU64::new(0);
static DSE_LAST_EVENT_COUNT: AtomicI64 = AtomicI64::new(-1);
static DSE_LAST_EVENT_CODE: AtomicI32 = AtomicI32::new(0);
static DSE_LAST_START_RESULT: AtomicI32 = AtomicI32::new(i32::MIN);
static DSE_LAST_STEP_RESULT: AtomicI32 = AtomicI32::new(i32::MIN);
static DSE_LAST_EVENT_SEQUENCE: AtomicU64 = AtomicU64::new(0);
static DSE_LAST_EVENT_FIELD01: AtomicU64 = AtomicU64::new(0);
static DSE_LAST_EVENT_FIELD23: AtomicU64 = AtomicU64::new(0);
static DSE_LAST_EVENT_VALUE_COUNT: AtomicI32 = AtomicI32::new(0);
static DSE_LAST_EVENT_VALUE_HASH: AtomicU64 = AtomicU64::new(0);
static DSE_LAST_EVENT_SECONDARY_VALUE_HASH: AtomicU64 = AtomicU64::new(0);
static DSE_LAST_EVENT_SECONDARY_VALUE_COUNT: AtomicU64 = AtomicU64::new(0);
static DSE_LAST_INPUT_FRAME: AtomicI64 = AtomicI64::new(-1);
static DSE_RENDER_OUTPUT_SAMPLES: AtomicU64 = AtomicU64::new(0);
static DSE_RENDER_OUTPUT_HASH: AtomicU64 = AtomicU64::new(0);
static DSE_RENDER_OUTPUT_PEAK: AtomicU64 = AtomicU64::new(0);
static DSE_RENDER_OUTPUT_ENERGY: AtomicU64 = AtomicU64::new(0);
static DSE_METADATA_STEPS: AtomicU64 = AtomicU64::new(0);
static DSE_POINTERLESS_STEPS: AtomicU64 = AtomicU64::new(0);
static DSE_POINTERLESS_ACTIVE_STEPS: AtomicU64 = AtomicU64::new(0);
static DSE_POINTERLESS_LOUD_STEPS: AtomicU64 = AtomicU64::new(0);
static DSE_POINTERLESS_FIRST_FRAME: AtomicI64 = AtomicI64::new(-1);
static DSE_POINTERLESS_LAST_FRAME: AtomicI64 = AtomicI64::new(-1);
static DSE_LAST_METADATA_FIELD01: AtomicU64 = AtomicU64::new(0);
static DSE_LAST_METADATA_FIELD23: AtomicU64 = AtomicU64::new(0);
static DSE_LAST_METADATA_FLAGS: AtomicU64 = AtomicU64::new(0);
static DSE_LAST_METADATA_POINTER_MASK: AtomicU64 = AtomicU64::new(0);

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct BreathCaptureStatus {
    pub install_result: i32,
    pub reserved: u32,
    pub target_rva: u64,
    pub core_target_rva: u64,
    pub core_calls: u64,
    pub mapped_contexts: u64,
    pub context_misses: u64,
    pub hook_calls: u64,
    pub successful_blocks: u64,
    pub output_samples: u64,
    pub output_peak: u64,
    pub queued_events: u64,
    pub dropped_events: u64,
    pub invalid_calls: u64,
    pub last_part_handle: u64,
    pub last_begin_frame: i64,
    pub last_end_frame: i64,
    pub last_result: i32,
    pub reserved2: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct DseCaptureStatus {
    pub install_result: i32,
    pub reserved: u32,
    pub vtable_rva: u64,
    pub create_buffer_calls: u64,
    pub add_event_calls: u64,
    pub set_preroll_calls: u64,
    pub start_calls: u64,
    pub stop_calls: u64,
    pub step_calls: u64,
    pub step_successes: u64,
    pub last_event_count: i64,
    pub last_event_code: i32,
    pub last_start_result: i32,
    pub last_step_result: i32,
    pub last_event_value_count: i32,
    pub last_event_sequence: u64,
    pub last_event_field01: u64,
    pub last_event_field23: u64,
    pub last_event_value_hash: u64,
    pub last_event_secondary_value_hash: u64,
    pub last_event_secondary_value_count: u64,
    pub last_input_frame: i64,
    pub render_output_samples: u64,
    pub render_output_hash: u64,
    pub render_output_peak: u64,
    pub render_output_energy: u64,
    pub metadata_steps: u64,
    pub pointerless_steps: u64,
    pub pointerless_active_steps: u64,
    pub pointerless_loud_steps: u64,
    pub pointerless_first_frame: i64,
    pub pointerless_last_frame: i64,
    pub last_metadata_field01: u64,
    pub last_metadata_field23: u64,
    pub last_metadata_flags: u64,
    pub last_metadata_pointer_mask: u64,
}

fn breath_events() -> &'static Mutex<BreathEventQueue> {
    BREATH_EVENTS.get_or_init(|| Mutex::new(BreathEventQueue::default()))
}

#[repr(C)]
pub struct ClockSnapshot {
    pub current_time: f64,
    pub projected_time: f64,
    pub playback_rate: f64,
    pub latency_seconds: f64,
    pub phase_error_seconds: f64,
    pub generation: u64,
    pub flags: u32,
    pub reserved: u32,
}

#[repr(C)]
pub struct CorrelationResult {
    pub lag: i32,
    pub correlation: f64,
    pub prominence: f64,
}

#[derive(Debug)]
struct ClockState {
    initialized: bool,
    frequency: f64,
    anchor_ticks: i64,
    anchor_time: f64,
    playback_rate: f64,
    last_observation_ticks: i64,
    last_engine_time: f64,
    rate_window_ticks: i64,
    rate_window_engine_time: f64,
    latency_ticks: i64,
    latency_seconds: f64,
    latency_target: f64,
    phase_error: f64,
    generation: u64,
}

impl Default for ClockState {
    fn default() -> Self {
        Self {
            initialized: false,
            frequency: 0.0,
            anchor_ticks: 0,
            anchor_time: 0.0,
            playback_rate: 1.0,
            last_observation_ticks: 0,
            last_engine_time: 0.0,
            rate_window_ticks: 0,
            rate_window_engine_time: 0.0,
            latency_ticks: 0,
            latency_seconds: 0.0,
            latency_target: 0.0,
            phase_error: 0.0,
            generation: 0,
        }
    }
}

impl ClockState {
    fn reset(&mut self, engine_time: f64, ticks: i64, frequency: i64, latency: f64) {
        self.initialized = true;
        self.frequency = frequency as f64;
        self.anchor_ticks = ticks;
        self.anchor_time = engine_time.max(0.0);
        self.playback_rate = 1.0;
        self.last_observation_ticks = ticks;
        self.last_engine_time = engine_time;
        self.rate_window_ticks = ticks;
        self.rate_window_engine_time = engine_time;
        self.latency_ticks = ticks;
        self.latency_seconds = latency.max(0.0);
        self.latency_target = latency.max(0.0);
        self.phase_error = 0.0;
        self.generation = self.generation.wrapping_add(1);
    }

    fn predicted_engine_time(&self, ticks: i64) -> f64 {
        self.anchor_time + (ticks - self.anchor_ticks) as f64 / self.frequency * self.playback_rate
    }

    fn smooth_latency(&mut self, ticks: i64) {
        let elapsed = ((ticks - self.latency_ticks) as f64 / self.frequency).clamp(0.0, 5.0);
        if elapsed > 0.0 {
            let gain = 1.0 - (-elapsed / LATENCY_TIME_CONSTANT_SECONDS).exp();
            self.latency_seconds += (self.latency_target - self.latency_seconds) * gain;
            self.latency_ticks = ticks;
        }
    }

    fn observe(&mut self, engine_time: f64, ticks: i64, latency_target: f64) -> i32 {
        if !self.initialized {
            return -2;
        }

        self.smooth_latency(ticks);
        self.latency_target = latency_target.max(0.0);

        // VAE may expose the same sample for several render callbacks. Treating
        // those duplicates as fresh observations would pull the clock backwards.
        if (engine_time - self.last_engine_time).abs() <= f64::EPSILON {
            return 0;
        }

        let predicted = self.predicted_engine_time(ticks);
        let error = engine_time - predicted;
        let moved_backwards = engine_time + BACKWARD_TOLERANCE_SECONDS < self.last_engine_time;
        if moved_backwards
            || error.abs() >= HARD_RESYNC_SECONDS
            || ticks < self.last_observation_ticks
        {
            let frequency = self.frequency as i64;
            let latency = self.latency_seconds;
            self.reset(engine_time, ticks, frequency, latency);
            self.latency_target = latency_target.max(0.0);
            self.phase_error = error;
            return 1;
        }

        let correction =
            (error * PHASE_GAIN).clamp(-MAX_PHASE_STEP_SECONDS, MAX_PHASE_STEP_SECONDS);
        self.anchor_time += correction;

        let rate_window_seconds = (ticks - self.rate_window_ticks) as f64 / self.frequency;
        if rate_window_seconds >= RATE_ESTIMATION_WINDOW_SECONDS {
            let engine_seconds = engine_time - self.rate_window_engine_time;
            let measured_rate = engine_seconds / rate_window_seconds;
            if measured_rate.is_finite() && (0.99..=1.01).contains(&measured_rate) {
                let new_rate = (self.playback_rate
                    + (measured_rate - self.playback_rate) * RATE_SMOOTHING_GAIN)
                    .clamp(MIN_PLAYBACK_RATE, MAX_PLAYBACK_RATE);

                // Changing the rate against an old anchor would itself create
                // a position jump. Re-anchor at the corrected current phase.
                self.anchor_time = self.predicted_engine_time(ticks);
                self.anchor_ticks = ticks;
                self.playback_rate = new_rate;
            }
            self.rate_window_ticks = ticks;
            self.rate_window_engine_time = engine_time;
        }

        self.phase_error = error;
        self.last_engine_time = engine_time;
        self.last_observation_ticks = ticks;
        0
    }

    fn snapshot(&mut self, ticks: i64, display_lead: f64, horizon: f64) -> ClockSnapshot {
        self.smooth_latency(ticks);
        let engine_time = self.predicted_engine_time(ticks);
        let current_time = (engine_time - self.latency_seconds + display_lead).max(0.0);
        let observation_age = (ticks - self.last_observation_ticks) as f64 / self.frequency;
        let mut flags = SNAPSHOT_INITIALIZED;
        if observation_age > STALE_OBSERVATION_SECONDS {
            flags |= SNAPSHOT_STALE;
        }

        ClockSnapshot {
            current_time,
            projected_time: current_time + horizon.max(0.0) * self.playback_rate,
            playback_rate: self.playback_rate,
            latency_seconds: self.latency_seconds,
            phase_error_seconds: self.phase_error,
            generation: self.generation,
            flags,
            reserved: 0,
        }
    }
}

static CLOCK: OnceLock<Mutex<ClockState>> = OnceLock::new();

fn clock() -> &'static Mutex<ClockState> {
    CLOCK.get_or_init(|| Mutex::new(ClockState::default()))
}

fn valid_time(value: f64) -> bool {
    value.is_finite() && value >= 0.0
}

const MAX_BREATH_FRAME: i64 = 1_000_000_000;

#[cfg(windows)]
mod breath_hook {
    use super::*;

    const PAGE_EXECUTE_READWRITE: u32 = 0x40;
    const MEM_COMMIT: u32 = 0x1000;
    const MEM_RESERVE: u32 = 0x2000;
    const MEM_RELEASE: u32 = 0x8000;
    const ABSOLUTE_JUMP_LENGTH: usize = 14;
    const CORE_PATCH_LENGTH: usize = 14;
    const MIXER_PATCH_LENGTH: usize = 16;
    const CORE_TARGET_RVA: usize = 0x50eb0;
    const MIXER_TARGET_RVA: usize = 0x4cda0;
    const EXPECTED_TIMESTAMP: u32 = 0x6a1e_712a;
    const EXPECTED_SIZE_OF_IMAGE: usize = 0x393000;
    const CONTEXT_MAP_CAPACITY: usize = 256;
    const CONTEXT_MAP_PROBES: usize = 8;

    // VSM 6.13 traditional render core (FUN_180050eb0) and automatic-breath
    // PCM mixer (FUN_18004cda0). Both must match the exact sample identity and
    // complete prologues before either observation is used.
    const CORE_SIGNATURE: &[u8] = &[
        0x48, 0x8b, 0xc4, 0x53, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57, 0x48,
        0x81, 0xec, 0x10, 0x03, 0x00, 0x00, 0x0f, 0x29, 0x70, 0xb8, 0x0f, 0x29, 0x78, 0xa8, 0x44,
        0x0f, 0x29, 0x40, 0x98, 0x44, 0x0f, 0x29, 0x48, 0x88, 0x44, 0x0f, 0x29, 0x90, 0x78, 0xff,
        0xff, 0xff,
    ];
    const MIXER_SIGNATURE: &[u8] = &[
        0x48, 0x8b, 0xc4, 0x48, 0x89, 0x58, 0x08, 0x48, 0x89, 0x70, 0x10, 0x48, 0x89, 0x78, 0x18,
        0x55, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57, 0x48, 0x8d, 0x68, 0xa8, 0x48, 0x81,
        0xec, 0x30, 0x01, 0x00, 0x00, 0x0f, 0x29, 0x70, 0xc8, 0x0f, 0x29, 0x78, 0xb8, 0x44, 0x0f,
        0x29, 0x40, 0xa8,
    ];

    static ORIGINAL_CORE: AtomicPtr<c_void> = AtomicPtr::new(ptr::null_mut());
    static ORIGINAL_MIXER: AtomicPtr<c_void> = AtomicPtr::new(ptr::null_mut());
    static INSTALL_LOCK: Mutex<()> = Mutex::new(());
    static CONTEXT_KEYS: [AtomicU64; CONTEXT_MAP_CAPACITY] =
        [const { AtomicU64::new(0) }; CONTEXT_MAP_CAPACITY];
    static CONTEXT_PARTS: [AtomicU64; CONTEXT_MAP_CAPACITY] =
        [const { AtomicU64::new(0) }; CONTEXT_MAP_CAPACITY];
    static CONTEXT_EPOCHS: [AtomicU64; CONTEXT_MAP_CAPACITY] =
        [const { AtomicU64::new(0) }; CONTEXT_MAP_CAPACITY];
    static NEXT_CONTEXT_EPOCH: AtomicU64 = AtomicU64::new(1);

    type TraditionalRenderCore =
        unsafe extern "system" fn(*mut c_void, *const c_void, *mut c_void) -> i32;
    type TraditionalBreathMixer =
        unsafe extern "system" fn(*mut c_void, *const c_void, i32, i32, i32, *mut i16) -> i32;

    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn GetModuleHandleW(module_name: *const u16) -> *mut c_void;
        fn GetCurrentProcess() -> *mut c_void;
        fn VirtualAlloc(
            address: *mut c_void,
            size: usize,
            allocation_type: u32,
            protect: u32,
        ) -> *mut c_void;
        fn VirtualFree(address: *mut c_void, size: usize, free_type: u32) -> i32;
        fn VirtualProtect(
            address: *mut c_void,
            size: usize,
            new_protect: u32,
            old_protect: *mut u32,
        ) -> i32;
        fn FlushInstructionCache(
            process: *mut c_void,
            base_address: *const c_void,
            size: usize,
        ) -> i32;
    }

    fn context_slot(context: u64) -> usize {
        ((context >> 4) as usize) & (CONTEXT_MAP_CAPACITY - 1)
    }

    fn register_context(context: u64, part: u64) {
        if context == 0 || part == 0 {
            return;
        }

        let base = context_slot(context);
        let epoch = NEXT_CONTEXT_EPOCH.fetch_add(1, Ordering::Relaxed).max(1);
        let mut oldest_index = base;
        let mut oldest_epoch = u64::MAX;
        for probe in 0..CONTEXT_MAP_PROBES {
            let index = (base + probe) & (CONTEXT_MAP_CAPACITY - 1);
            let key = CONTEXT_KEYS[index].load(Ordering::Acquire);
            if key == context {
                CONTEXT_PARTS[index].store(part, Ordering::Release);
                CONTEXT_EPOCHS[index].store(epoch, Ordering::Relaxed);
                BREATH_MAPPED_CONTEXTS.fetch_add(1, Ordering::Relaxed);
                return;
            }
            if key == 0 {
                CONTEXT_PARTS[index].store(part, Ordering::Relaxed);
                CONTEXT_EPOCHS[index].store(epoch, Ordering::Relaxed);
                CONTEXT_KEYS[index].store(context, Ordering::Release);
                BREATH_MAPPED_CONTEXTS.fetch_add(1, Ordering::Relaxed);
                return;
            }

            let candidate_epoch = CONTEXT_EPOCHS[index].load(Ordering::Relaxed);
            if candidate_epoch < oldest_epoch {
                oldest_epoch = candidate_epoch;
                oldest_index = index;
            }
        }

        // The oldest of a small probe window is overwhelmingly a completed
        // render. Clear the key before publishing a replacement pair.
        CONTEXT_KEYS[oldest_index].store(0, Ordering::Release);
        CONTEXT_PARTS[oldest_index].store(part, Ordering::Relaxed);
        CONTEXT_EPOCHS[oldest_index].store(epoch, Ordering::Relaxed);
        CONTEXT_KEYS[oldest_index].store(context, Ordering::Release);
        BREATH_MAPPED_CONTEXTS.fetch_add(1, Ordering::Relaxed);
    }

    fn find_part_for_context(context: u64) -> u64 {
        let base = context_slot(context);
        for probe in 0..CONTEXT_MAP_PROBES {
            let index = (base + probe) & (CONTEXT_MAP_CAPACITY - 1);
            if CONTEXT_KEYS[index].load(Ordering::Acquire) == context {
                let part = CONTEXT_PARTS[index].load(Ordering::Acquire);
                // A replacement clears the key before publishing the new pair.
                // Recheck it so a reader that raced the clear cannot combine an
                // old key with the replacement Part value.
                if CONTEXT_KEYS[index].load(Ordering::Acquire) == context {
                    return part;
                }
            }
        }
        0
    }

    unsafe extern "system" fn traditional_render_core_hook(
        renderer_holder: *mut c_void,
        arguments: *const c_void,
        frame_shape: *mut c_void,
    ) -> i32 {
        BREATH_CORE_CALLS.fetch_add(1, Ordering::Relaxed);
        if !renderer_holder.is_null() && !arguments.is_null() {
            let renderer = unsafe { ptr::read_unaligned(renderer_holder.cast::<*mut u8>()) };
            let context = unsafe { ptr::read_unaligned(arguments.cast::<*const c_void>()) };
            if !renderer.is_null() && !context.is_null() {
                let part =
                    unsafe { ptr::read_unaligned(renderer.add(0x10).cast::<*const c_void>()) };
                register_context(context as usize as u64, part as usize as u64);
            }
        }

        let original = ORIGINAL_CORE.load(Ordering::Acquire);
        if original.is_null() {
            BREATH_INVALID_CALLS.fetch_add(1, Ordering::Relaxed);
            return 0x14;
        }
        let original: TraditionalRenderCore = unsafe { std::mem::transmute(original) };
        unsafe { original(renderer_holder, arguments, frame_shape) }
    }

    unsafe extern "system" fn traditional_breath_mixer_hook(
        cache: *mut c_void,
        render_context: *const c_void,
        frame: i32,
        context_a: i32,
        context_b: i32,
        output: *mut i16,
    ) -> i32 {
        BREATH_HOOK_CALLS.fetch_add(1, Ordering::Relaxed);

        let original = ORIGINAL_MIXER.load(Ordering::Acquire);
        if original.is_null() {
            BREATH_INVALID_CALLS.fetch_add(1, Ordering::Relaxed);
            return -128;
        }
        let original: TraditionalBreathMixer = unsafe { std::mem::transmute(original) };
        let result =
            unsafe { original(cache, render_context, frame, context_a, context_b, output) };
        BREATH_LAST_RESULT.store(result, Ordering::Relaxed);
        if result < 0 {
            return result;
        }

        if render_context.is_null()
            || output.is_null()
            || frame < 0
            || i64::from(frame) >= MAX_BREATH_FRAME
        {
            BREATH_INVALID_CALLS.fetch_add(1, Ordering::Relaxed);
            return result;
        }

        let samples = unsafe { slice::from_raw_parts(output, 0x100) };
        let peak = samples
            .iter()
            .map(|sample| i64::from(*sample).unsigned_abs())
            .max()
            .unwrap_or(0);
        let part_handle = find_part_for_context(render_context as usize as u64);
        if part_handle == 0 {
            BREATH_CONTEXT_MISSES.fetch_add(1, Ordering::Relaxed);
            return result;
        }
        let begin_frame = i64::from(frame);
        let end_frame = begin_frame + 1;
        BREATH_SUCCESSFUL_BLOCKS.fetch_add(1, Ordering::Relaxed);
        BREATH_OUTPUT_SAMPLES.fetch_add(samples.len() as u64, Ordering::Relaxed);
        BREATH_OUTPUT_PEAK.fetch_max(peak, Ordering::Relaxed);
        BREATH_LAST_PART_HANDLE.store(part_handle, Ordering::Relaxed);
        BREATH_LAST_BEGIN_FRAME.store(begin_frame, Ordering::Relaxed);
        BREATH_LAST_END_FRAME.store(end_frame, Ordering::Relaxed);
        if let Ok(mut queue) = breath_events().try_lock() {
            if queue.push(part_handle, begin_frame, end_frame) {
                BREATH_QUEUED_EVENTS.fetch_add(1, Ordering::Relaxed);
            }
        } else {
            // Never block VSM's render thread on a diagnostic consumer.
            BREATH_DROPPED_EVENTS.fetch_add(1, Ordering::Relaxed);
        }
        result
    }

    fn signature_matches(bytes: &[u8], signature: &[u8]) -> bool {
        bytes.len() >= signature.len()
            && signature
                .iter()
                .enumerate()
                .all(|(index, expected)| bytes[index] == *expected)
    }

    unsafe fn validated_image_size(module: *mut u8) -> Result<usize, i32> {
        if module.is_null() || unsafe { ptr::read_unaligned(module.cast::<u16>()) } != 0x5a4d {
            return Err(-2);
        }

        let nt_offset = unsafe { ptr::read_unaligned(module.add(0x3c).cast::<u32>()) } as usize;
        let nt = unsafe { module.add(nt_offset) };
        if unsafe { ptr::read_unaligned(nt.cast::<u32>()) } != 0x0000_4550 {
            return Err(-2);
        }
        let timestamp = unsafe { ptr::read_unaligned(nt.add(8).cast::<u32>()) };

        let optional_size = unsafe { ptr::read_unaligned(nt.add(20).cast::<u16>()) } as usize;
        if optional_size < 0x70 {
            return Err(-2);
        }
        let size_of_image =
            unsafe { ptr::read_unaligned(nt.add(24 + 0x38).cast::<u32>()) } as usize;
        if timestamp != EXPECTED_TIMESTAMP || size_of_image != EXPECTED_SIZE_OF_IMAGE {
            return Err(-7);
        }
        Ok(size_of_image)
    }

    unsafe fn find_target(
        module: *mut u8,
        target_rva: usize,
        signature: &[u8],
    ) -> Result<*mut u8, i32> {
        let size_of_image = unsafe { validated_image_size(module)? };
        let Some(signature_end) = target_rva.checked_add(signature.len()) else {
            return Err(-2);
        };
        if signature_end > size_of_image {
            return Err(-2);
        }

        let target = unsafe { module.add(target_rva) };
        let bytes = unsafe { slice::from_raw_parts(target, signature.len()) };
        if signature_matches(bytes, signature) {
            Ok(target)
        } else {
            Err(-3)
        }
    }

    unsafe fn write_absolute_jump(destination: *mut u8, target: *const c_void) {
        let mut jump = [0u8; ABSOLUTE_JUMP_LENGTH];
        jump[..6].copy_from_slice(&[0xff, 0x25, 0x00, 0x00, 0x00, 0x00]);
        jump[6..].copy_from_slice(&(target as usize as u64).to_le_bytes());
        unsafe { ptr::copy_nonoverlapping(jump.as_ptr(), destination, jump.len()) };
    }

    unsafe fn install_inline_hook(
        target: *mut u8,
        patch_length: usize,
        hook: *const c_void,
        original: &AtomicPtr<c_void>,
    ) -> Result<(), i32> {
        let trampoline_size = patch_length + ABSOLUTE_JUMP_LENGTH;
        let trampoline = unsafe {
            VirtualAlloc(
                ptr::null_mut(),
                trampoline_size,
                MEM_COMMIT | MEM_RESERVE,
                PAGE_EXECUTE_READWRITE,
            )
        }
        .cast::<u8>();
        if trampoline.is_null() {
            return Err(-4);
        }

        unsafe {
            ptr::copy_nonoverlapping(target, trampoline, patch_length);
            write_absolute_jump(
                trampoline.add(patch_length),
                target.add(patch_length).cast::<c_void>(),
            );
        }

        let mut old_protect = 0u32;
        if unsafe {
            VirtualProtect(
                target.cast::<c_void>(),
                patch_length,
                PAGE_EXECUTE_READWRITE,
                &mut old_protect,
            )
        } == 0
        {
            unsafe { VirtualFree(trampoline.cast::<c_void>(), 0, MEM_RELEASE) };
            return Err(-5);
        }

        // Publish the trampoline before another renderer thread can enter the hook.
        original.store(trampoline.cast::<c_void>(), Ordering::Release);
        unsafe {
            write_absolute_jump(target, hook);
            for index in ABSOLUTE_JUMP_LENGTH..patch_length {
                *target.add(index) = 0x90;
            }
            FlushInstructionCache(GetCurrentProcess(), target.cast::<c_void>(), patch_length);
            let mut ignored = 0u32;
            VirtualProtect(
                target.cast::<c_void>(),
                patch_length,
                old_protect,
                &mut ignored,
            );
        }
        Ok(())
    }

    pub fn install() -> i32 {
        if !ORIGINAL_MIXER.load(Ordering::Acquire).is_null() {
            return 0;
        }
        let _guard = INSTALL_LOCK
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        if !ORIGINAL_MIXER.load(Ordering::Acquire).is_null() {
            return 0;
        }

        let module_name: Vec<u16> = "VSM.dll\0".encode_utf16().collect();
        let module = unsafe { GetModuleHandleW(module_name.as_ptr()) }.cast::<u8>();
        if module.is_null() {
            return -6;
        }

        // Validate both exact-sample boundaries before changing either function.
        // This prevents a signature mismatch from leaving a needless partial hook.
        let core_target = match unsafe { find_target(module, CORE_TARGET_RVA, CORE_SIGNATURE) } {
            Ok(value) => value,
            Err(error) => return error,
        };
        let mixer_target = match unsafe { find_target(module, MIXER_TARGET_RVA, MIXER_SIGNATURE) } {
            Ok(value) => value,
            Err(error) => return error,
        };

        if ORIGINAL_CORE.load(Ordering::Acquire).is_null() {
            if let Err(error) = unsafe {
                install_inline_hook(
                    core_target,
                    CORE_PATCH_LENGTH,
                    traditional_render_core_hook as *const c_void,
                    &ORIGINAL_CORE,
                )
            } {
                return error;
            }
            BREATH_CORE_TARGET_RVA.store(CORE_TARGET_RVA as u64, Ordering::Relaxed);
        }

        if let Err(error) = unsafe {
            install_inline_hook(
                mixer_target,
                MIXER_PATCH_LENGTH,
                traditional_breath_mixer_hook as *const c_void,
                &ORIGINAL_MIXER,
            )
        } {
            return error;
        }
        BREATH_TARGET_RVA.store(MIXER_TARGET_RVA as u64, Ordering::Relaxed);
        1
    }

    #[cfg(test)]
    pub(super) fn test_core_signature(bytes: &[u8]) -> bool {
        signature_matches(bytes, CORE_SIGNATURE)
    }

    #[cfg(test)]
    pub(super) fn test_core_signature_bytes() -> Vec<u8> {
        CORE_SIGNATURE.to_vec()
    }

    #[cfg(test)]
    pub(super) fn test_mixer_signature(bytes: &[u8]) -> bool {
        signature_matches(bytes, MIXER_SIGNATURE)
    }

    #[cfg(test)]
    pub(super) fn test_mixer_signature_bytes() -> Vec<u8> {
        MIXER_SIGNATURE.to_vec()
    }

    #[cfg(test)]
    pub(super) fn test_context_mapping(context: u64, part: u64) -> u64 {
        register_context(context, part);
        find_part_for_context(context)
    }
}

#[cfg(windows)]
mod dse_hook {
    use super::*;

    const PAGE_EXECUTE_READWRITE: u32 = 0x40;
    const VTABLE_RVA: usize = 0x50b618;
    const SLOT_CREATE_BUFFER: usize = 7;
    const SLOT_ADD_EVENT: usize = 8;
    const SLOT_SET_PREROLL: usize = 9;
    const SLOT_START: usize = 10;
    const SLOT_STOP: usize = 11;
    const SLOT_STEP: usize = 15;
    const EXPECTED_TARGETS: &[(usize, usize)] = &[
        (SLOT_CREATE_BUFFER, 0x1d0110),
        (SLOT_ADD_EVENT, 0x1d0030),
        (SLOT_SET_PREROLL, 0x1d0240),
        (SLOT_START, 0x1d0290),
        (SLOT_STOP, 0x1d02b0),
        (SLOT_STEP, 0x1d00f0),
    ];
    const MAX_EVENT_VALUES: usize = 1_000_000;
    const MAX_OUTPUT_SAMPLES: usize = 4096;
    const LOUD_SAMPLE_THRESHOLD: u64 = 64;
    const METADATA_FIELD0_OFFSET: usize = 0x20;
    const METADATA_FIELD1_OFFSET: usize = 0x24;
    const METADATA_FIELD2_OFFSET: usize = 0x28;
    const METADATA_FIELD3_OFFSET: usize = 0x2c;
    const METADATA_FLAGS_OFFSET: usize = 0x30;
    const METADATA_FROM_POINTER_OFFSET: usize = 0x38;
    const METADATA_TO_POINTER_OFFSET: usize = 0x40;
    const FNV_OFFSET: u64 = 0xcbf29ce484222325;
    const FNV_PRIME: u64 = 0x100000001b3;

    static INSTALL_LOCK: Mutex<()> = Mutex::new(());
    static ORIGINAL_CREATE_BUFFER: AtomicPtr<c_void> = AtomicPtr::new(ptr::null_mut());
    static ORIGINAL_ADD_EVENT: AtomicPtr<c_void> = AtomicPtr::new(ptr::null_mut());
    static ORIGINAL_SET_PREROLL: AtomicPtr<c_void> = AtomicPtr::new(ptr::null_mut());
    static ORIGINAL_START: AtomicPtr<c_void> = AtomicPtr::new(ptr::null_mut());
    static ORIGINAL_STOP: AtomicPtr<c_void> = AtomicPtr::new(ptr::null_mut());
    static ORIGINAL_STEP: AtomicPtr<c_void> = AtomicPtr::new(ptr::null_mut());

    type CreateBuffer = unsafe extern "system" fn(*mut c_void, i64, i32) -> i32;
    type AddEvent = unsafe extern "system" fn(*mut c_void, *const DseMidiEvent) -> i32;
    type SetPreroll = unsafe extern "system" fn(*mut c_void, i32, *const f32) -> i32;
    type EngineStateCall = unsafe extern "system" fn(*mut c_void) -> i32;
    type Step =
        unsafe extern "system" fn(*mut c_void, *const c_void, *mut c_void, *mut c_void) -> i32;

    #[repr(C)]
    struct DseMidiEvent {
        field0: i32,
        field1: i32,
        field2: i32,
        field3: i32,
        value_count: i32,
        reserved: i32,
        primary_values: *const f32,
        secondary_values: *const f32,
    }

    #[repr(C)]
    struct DsePcmOutput {
        sample_count: i32,
        reserved: i32,
        samples: *const i16,
    }

    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn GetModuleHandleW(module_name: *const u16) -> *mut c_void;
        fn VirtualProtect(
            address: *mut c_void,
            size: usize,
            new_protect: u32,
            old_protect: *mut u32,
        ) -> i32;
    }

    fn fnv_extend(initial: u64, data: &[u8]) -> u64 {
        data.iter().fold(initial, |hash, value| {
            (hash ^ u64::from(*value)).wrapping_mul(FNV_PRIME)
        })
    }

    fn fnv_bytes(data: &[u8]) -> u64 {
        fnv_extend(FNV_OFFSET, data)
    }

    unsafe fn hash_event_values(values: *const f32, count: i32) -> u64 {
        if values.is_null() || count <= 0 || count as usize > 1_000_000 {
            return 0;
        }
        let sampled_count = (count as usize).min(MAX_EVENT_VALUES);
        let bytes = unsafe { slice::from_raw_parts(values.cast::<u8>(), sampled_count * 4) };
        fnv_bytes(bytes)
    }

    fn reset_render_diagnostics() {
        DSE_LAST_INPUT_FRAME.store(-1, Ordering::Relaxed);
        DSE_RENDER_OUTPUT_SAMPLES.store(0, Ordering::Relaxed);
        DSE_RENDER_OUTPUT_HASH.store(FNV_OFFSET, Ordering::Relaxed);
        DSE_RENDER_OUTPUT_PEAK.store(0, Ordering::Relaxed);
        DSE_RENDER_OUTPUT_ENERGY.store(0, Ordering::Relaxed);
        DSE_METADATA_STEPS.store(0, Ordering::Relaxed);
        DSE_POINTERLESS_STEPS.store(0, Ordering::Relaxed);
        DSE_POINTERLESS_ACTIVE_STEPS.store(0, Ordering::Relaxed);
        DSE_POINTERLESS_LOUD_STEPS.store(0, Ordering::Relaxed);
        DSE_POINTERLESS_FIRST_FRAME.store(-1, Ordering::Relaxed);
        DSE_POINTERLESS_LAST_FRAME.store(-1, Ordering::Relaxed);
        DSE_LAST_METADATA_FIELD01.store(0, Ordering::Relaxed);
        DSE_LAST_METADATA_FIELD23.store(0, Ordering::Relaxed);
        DSE_LAST_METADATA_FLAGS.store(0, Ordering::Relaxed);
        DSE_LAST_METADATA_POINTER_MASK.store(0, Ordering::Relaxed);
    }

    unsafe fn analyze_step_output(
        input: *const c_void,
        output: *const c_void,
        metadata: *const c_void,
    ) {
        let input_frame = if input.is_null() {
            -1
        } else {
            i64::from(unsafe { ptr::read_unaligned(input.cast::<i32>()) })
        };
        DSE_LAST_INPUT_FRAME.store(input_frame, Ordering::Relaxed);

        let mut peak = 0u64;
        if let Some(output) = unsafe { output.cast::<DsePcmOutput>().as_ref() } {
            if output.sample_count > 0
                && output.sample_count as usize <= MAX_OUTPUT_SAMPLES
                && !output.samples.is_null()
            {
                let samples =
                    unsafe { slice::from_raw_parts(output.samples, output.sample_count as usize) };
                let bytes = unsafe {
                    slice::from_raw_parts(samples.as_ptr().cast::<u8>(), samples.len() * 2)
                };
                let hash = fnv_extend(DSE_RENDER_OUTPUT_HASH.load(Ordering::Relaxed), bytes);
                let mut energy = 0u64;
                for sample in samples {
                    let absolute = i64::from(*sample).unsigned_abs();
                    peak = peak.max(absolute);
                    energy = energy.saturating_add(absolute.saturating_mul(absolute));
                }
                DSE_RENDER_OUTPUT_SAMPLES.fetch_add(samples.len() as u64, Ordering::Relaxed);
                DSE_RENDER_OUTPUT_HASH.store(hash, Ordering::Relaxed);
                DSE_RENDER_OUTPUT_PEAK.fetch_max(peak, Ordering::Relaxed);
                DSE_RENDER_OUTPUT_ENERGY.fetch_add(energy, Ordering::Relaxed);
            }
        }

        if metadata.is_null() {
            return;
        }
        let bytes = metadata.cast::<u8>();
        let field0 =
            unsafe { ptr::read_unaligned(bytes.add(METADATA_FIELD0_OFFSET).cast::<u32>()) };
        let field1 =
            unsafe { ptr::read_unaligned(bytes.add(METADATA_FIELD1_OFFSET).cast::<u32>()) };
        let field2 =
            unsafe { ptr::read_unaligned(bytes.add(METADATA_FIELD2_OFFSET).cast::<u32>()) };
        let field3 =
            unsafe { ptr::read_unaligned(bytes.add(METADATA_FIELD3_OFFSET).cast::<u32>()) };
        let flags = unsafe { ptr::read_unaligned(bytes.add(METADATA_FLAGS_OFFSET).cast::<u16>()) };
        let from =
            unsafe { ptr::read_unaligned(bytes.add(METADATA_FROM_POINTER_OFFSET).cast::<usize>()) };
        let to =
            unsafe { ptr::read_unaligned(bytes.add(METADATA_TO_POINTER_OFFSET).cast::<usize>()) };
        let pointer_mask = u64::from(from != 0) | (u64::from(to != 0) << 1);
        DSE_METADATA_STEPS.fetch_add(1, Ordering::Relaxed);
        DSE_LAST_METADATA_FIELD01.store(
            u64::from(field0) | (u64::from(field1) << 32),
            Ordering::Relaxed,
        );
        DSE_LAST_METADATA_FIELD23.store(
            u64::from(field2) | (u64::from(field3) << 32),
            Ordering::Relaxed,
        );
        DSE_LAST_METADATA_FLAGS.store(u64::from(flags), Ordering::Relaxed);
        DSE_LAST_METADATA_POINTER_MASK.store(pointer_mask, Ordering::Relaxed);
        if pointer_mask != 0 {
            return;
        }

        DSE_POINTERLESS_STEPS.fetch_add(1, Ordering::Relaxed);
        if peak == 0 {
            return;
        }
        DSE_POINTERLESS_ACTIVE_STEPS.fetch_add(1, Ordering::Relaxed);
        if peak >= LOUD_SAMPLE_THRESHOLD {
            DSE_POINTERLESS_LOUD_STEPS.fetch_add(1, Ordering::Relaxed);
        }
        let _ = DSE_POINTERLESS_FIRST_FRAME.compare_exchange(
            -1,
            input_frame,
            Ordering::Relaxed,
            Ordering::Relaxed,
        );
        DSE_POINTERLESS_LAST_FRAME.store(input_frame, Ordering::Relaxed);
    }

    unsafe extern "system" fn create_buffer_hook(
        engine: *mut c_void,
        count: i64,
        code: i32,
    ) -> i32 {
        DSE_CREATE_BUFFER_CALLS.fetch_add(1, Ordering::Relaxed);
        DSE_LAST_EVENT_COUNT.store(count, Ordering::Relaxed);
        DSE_LAST_EVENT_CODE.store(code, Ordering::Relaxed);
        let original: CreateBuffer =
            unsafe { std::mem::transmute(ORIGINAL_CREATE_BUFFER.load(Ordering::Acquire)) };
        unsafe { original(engine, count, code) }
    }

    unsafe extern "system" fn add_event_hook(
        engine: *mut c_void,
        event: *const DseMidiEvent,
    ) -> i32 {
        let sequence = DSE_ADD_EVENT_CALLS.fetch_add(1, Ordering::Relaxed) + 1;
        if let Some(event) = unsafe { event.as_ref() } {
            DSE_LAST_EVENT_SEQUENCE.store(sequence, Ordering::Relaxed);
            DSE_LAST_EVENT_FIELD01.store(
                u64::from(event.field0 as u32) | (u64::from(event.field1 as u32) << 32),
                Ordering::Relaxed,
            );
            DSE_LAST_EVENT_FIELD23.store(
                u64::from(event.field2 as u32) | (u64::from(event.field3 as u32) << 32),
                Ordering::Relaxed,
            );
            DSE_LAST_EVENT_VALUE_COUNT.store(event.value_count, Ordering::Relaxed);
            DSE_LAST_EVENT_VALUE_HASH.store(
                unsafe { hash_event_values(event.primary_values, event.value_count) },
                Ordering::Relaxed,
            );
            DSE_LAST_EVENT_SECONDARY_VALUE_HASH.store(
                unsafe { hash_event_values(event.secondary_values, event.value_count) },
                Ordering::Relaxed,
            );
            DSE_LAST_EVENT_SECONDARY_VALUE_COUNT
                .store(event.value_count.max(0) as u64, Ordering::Relaxed);
        }
        let original: AddEvent =
            unsafe { std::mem::transmute(ORIGINAL_ADD_EVENT.load(Ordering::Acquire)) };
        unsafe { original(engine, event) }
    }

    unsafe extern "system" fn set_preroll_hook(
        engine: *mut c_void,
        begin: i32,
        values: *const f32,
    ) -> i32 {
        DSE_SET_PREROLL_CALLS.fetch_add(1, Ordering::Relaxed);
        let original: SetPreroll =
            unsafe { std::mem::transmute(ORIGINAL_SET_PREROLL.load(Ordering::Acquire)) };
        unsafe { original(engine, begin, values) }
    }

    unsafe extern "system" fn start_hook(engine: *mut c_void) -> i32 {
        DSE_START_CALLS.fetch_add(1, Ordering::Relaxed);
        reset_render_diagnostics();
        let original: EngineStateCall =
            unsafe { std::mem::transmute(ORIGINAL_START.load(Ordering::Acquire)) };
        let result = unsafe { original(engine) };
        DSE_LAST_START_RESULT.store(result, Ordering::Relaxed);
        result
    }

    unsafe extern "system" fn stop_hook(engine: *mut c_void) -> i32 {
        DSE_STOP_CALLS.fetch_add(1, Ordering::Relaxed);
        let original: EngineStateCall =
            unsafe { std::mem::transmute(ORIGINAL_STOP.load(Ordering::Acquire)) };
        unsafe { original(engine) }
    }

    unsafe extern "system" fn step_hook(
        engine: *mut c_void,
        input: *const c_void,
        output: *mut c_void,
        metadata: *mut c_void,
    ) -> i32 {
        DSE_STEP_CALLS.fetch_add(1, Ordering::Relaxed);
        let original: Step = unsafe { std::mem::transmute(ORIGINAL_STEP.load(Ordering::Acquire)) };
        let result = unsafe { original(engine, input, output, metadata) };
        DSE_LAST_STEP_RESULT.store(result, Ordering::Relaxed);
        if result == 0 {
            DSE_STEP_SUCCESSES.fetch_add(1, Ordering::Relaxed);
            unsafe { analyze_step_output(input, output, metadata) };
        }
        result
    }

    pub fn install() -> i32 {
        if !ORIGINAL_ADD_EVENT.load(Ordering::Acquire).is_null() {
            return 0;
        }
        let _guard = INSTALL_LOCK
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        if !ORIGINAL_ADD_EVENT.load(Ordering::Acquire).is_null() {
            return 0;
        }

        let module_name: Vec<u16> = "DSE.dll\0".encode_utf16().collect();
        let module = unsafe { GetModuleHandleW(module_name.as_ptr()) }.cast::<u8>();
        if module.is_null() {
            return -6;
        }
        let vtable = unsafe { module.add(VTABLE_RVA) }.cast::<*mut c_void>();
        for (slot, expected_rva) in EXPECTED_TARGETS {
            let actual = unsafe { *vtable.add(*slot) } as usize;
            if actual != module as usize + expected_rva {
                return -3;
            }
        }

        let mut old_protect = 0u32;
        if unsafe {
            VirtualProtect(
                vtable.cast::<c_void>(),
                (SLOT_STEP + 1) * std::mem::size_of::<usize>(),
                PAGE_EXECUTE_READWRITE,
                &mut old_protect,
            )
        } == 0
        {
            return -5;
        }

        unsafe {
            ORIGINAL_CREATE_BUFFER.store(*vtable.add(SLOT_CREATE_BUFFER), Ordering::Release);
            ORIGINAL_ADD_EVENT.store(*vtable.add(SLOT_ADD_EVENT), Ordering::Release);
            ORIGINAL_SET_PREROLL.store(*vtable.add(SLOT_SET_PREROLL), Ordering::Release);
            ORIGINAL_START.store(*vtable.add(SLOT_START), Ordering::Release);
            ORIGINAL_STOP.store(*vtable.add(SLOT_STOP), Ordering::Release);
            ORIGINAL_STEP.store(*vtable.add(SLOT_STEP), Ordering::Release);
            *vtable.add(SLOT_CREATE_BUFFER) = create_buffer_hook as *mut c_void;
            *vtable.add(SLOT_ADD_EVENT) = add_event_hook as *mut c_void;
            *vtable.add(SLOT_SET_PREROLL) = set_preroll_hook as *mut c_void;
            *vtable.add(SLOT_START) = start_hook as *mut c_void;
            *vtable.add(SLOT_STOP) = stop_hook as *mut c_void;
            *vtable.add(SLOT_STEP) = step_hook as *mut c_void;
            let mut ignored = 0u32;
            VirtualProtect(
                vtable.cast::<c_void>(),
                (SLOT_STEP + 1) * std::mem::size_of::<usize>(),
                old_protect,
                &mut ignored,
            );
        }
        DSE_VTABLE_RVA.store(VTABLE_RVA as u64, Ordering::Relaxed);
        1
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn v6_breath_install() -> i32 {
    let result = {
        #[cfg(windows)]
        {
            breath_hook::install()
        }
        #[cfg(not(windows))]
        {
            -1
        }
    };
    BREATH_INSTALL_RESULT.store(result, Ordering::Relaxed);
    result
}

#[unsafe(no_mangle)]
/// Removes unread exact traditional automatic-breath mixer events.
pub extern "C" fn v6_breath_clear() {
    breath_events()
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
        .clear();
}

#[unsafe(no_mangle)]
/// Copies and consumes up to `capacity` exact mixer events.
///
/// # Safety
///
/// `output` must reference at least `capacity` writable [`BreathEvent`] values.
pub unsafe extern "C" fn v6_breath_read(output: *mut BreathEvent, capacity: i32) -> i32 {
    if output.is_null() || capacity <= 0 {
        return -1;
    }

    let mut queue = breath_events()
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    let count = (capacity as usize).min(queue.length);
    for index in 0..count {
        if let Some(event) = queue.pop() {
            unsafe { output.add(index).write(event) };
        }
    }
    count as i32
}

#[unsafe(no_mangle)]
/// Returns a lock-free diagnostic snapshot for the traditional breath mixer hook.
///
/// # Safety
///
/// `output` must reference one writable [`BreathCaptureStatus`] value.
pub unsafe extern "C" fn v6_breath_status(output: *mut BreathCaptureStatus) -> i32 {
    if output.is_null() {
        return -1;
    }

    let status = BreathCaptureStatus {
        install_result: BREATH_INSTALL_RESULT.load(Ordering::Relaxed),
        reserved: 0,
        target_rva: BREATH_TARGET_RVA.load(Ordering::Relaxed),
        core_target_rva: BREATH_CORE_TARGET_RVA.load(Ordering::Relaxed),
        core_calls: BREATH_CORE_CALLS.load(Ordering::Relaxed),
        mapped_contexts: BREATH_MAPPED_CONTEXTS.load(Ordering::Relaxed),
        context_misses: BREATH_CONTEXT_MISSES.load(Ordering::Relaxed),
        hook_calls: BREATH_HOOK_CALLS.load(Ordering::Relaxed),
        successful_blocks: BREATH_SUCCESSFUL_BLOCKS.load(Ordering::Relaxed),
        output_samples: BREATH_OUTPUT_SAMPLES.load(Ordering::Relaxed),
        output_peak: BREATH_OUTPUT_PEAK.load(Ordering::Relaxed),
        queued_events: BREATH_QUEUED_EVENTS.load(Ordering::Relaxed),
        dropped_events: BREATH_DROPPED_EVENTS.load(Ordering::Relaxed),
        invalid_calls: BREATH_INVALID_CALLS.load(Ordering::Relaxed),
        last_part_handle: BREATH_LAST_PART_HANDLE.load(Ordering::Relaxed),
        last_begin_frame: BREATH_LAST_BEGIN_FRAME.load(Ordering::Relaxed),
        last_end_frame: BREATH_LAST_END_FRAME.load(Ordering::Relaxed),
        last_result: BREATH_LAST_RESULT.load(Ordering::Relaxed),
        reserved2: 0,
    };
    unsafe { output.write(status) };
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn v6_dse_install() -> i32 {
    let result = {
        #[cfg(windows)]
        {
            dse_hook::install()
        }
        #[cfg(not(windows))]
        {
            -1
        }
    };
    DSE_INSTALL_RESULT.store(result, Ordering::Relaxed);
    result
}

#[unsafe(no_mangle)]
/// Returns a lock-free diagnostic snapshot for the exact-sample DSE vtable probe.
///
/// # Safety
///
/// `output` must reference one writable [`DseCaptureStatus`] value.
pub unsafe extern "C" fn v6_dse_status(output: *mut DseCaptureStatus) -> i32 {
    if output.is_null() {
        return -1;
    }
    let status = DseCaptureStatus {
        install_result: DSE_INSTALL_RESULT.load(Ordering::Relaxed),
        reserved: 0,
        vtable_rva: DSE_VTABLE_RVA.load(Ordering::Relaxed),
        create_buffer_calls: DSE_CREATE_BUFFER_CALLS.load(Ordering::Relaxed),
        add_event_calls: DSE_ADD_EVENT_CALLS.load(Ordering::Relaxed),
        set_preroll_calls: DSE_SET_PREROLL_CALLS.load(Ordering::Relaxed),
        start_calls: DSE_START_CALLS.load(Ordering::Relaxed),
        stop_calls: DSE_STOP_CALLS.load(Ordering::Relaxed),
        step_calls: DSE_STEP_CALLS.load(Ordering::Relaxed),
        step_successes: DSE_STEP_SUCCESSES.load(Ordering::Relaxed),
        last_event_count: DSE_LAST_EVENT_COUNT.load(Ordering::Relaxed),
        last_event_code: DSE_LAST_EVENT_CODE.load(Ordering::Relaxed),
        last_start_result: DSE_LAST_START_RESULT.load(Ordering::Relaxed),
        last_step_result: DSE_LAST_STEP_RESULT.load(Ordering::Relaxed),
        last_event_value_count: DSE_LAST_EVENT_VALUE_COUNT.load(Ordering::Relaxed),
        last_event_sequence: DSE_LAST_EVENT_SEQUENCE.load(Ordering::Relaxed),
        last_event_field01: DSE_LAST_EVENT_FIELD01.load(Ordering::Relaxed),
        last_event_field23: DSE_LAST_EVENT_FIELD23.load(Ordering::Relaxed),
        last_event_value_hash: DSE_LAST_EVENT_VALUE_HASH.load(Ordering::Relaxed),
        last_event_secondary_value_hash: DSE_LAST_EVENT_SECONDARY_VALUE_HASH
            .load(Ordering::Relaxed),
        last_event_secondary_value_count: DSE_LAST_EVENT_SECONDARY_VALUE_COUNT
            .load(Ordering::Relaxed),
        last_input_frame: DSE_LAST_INPUT_FRAME.load(Ordering::Relaxed),
        render_output_samples: DSE_RENDER_OUTPUT_SAMPLES.load(Ordering::Relaxed),
        render_output_hash: DSE_RENDER_OUTPUT_HASH.load(Ordering::Relaxed),
        render_output_peak: DSE_RENDER_OUTPUT_PEAK.load(Ordering::Relaxed),
        render_output_energy: DSE_RENDER_OUTPUT_ENERGY.load(Ordering::Relaxed),
        metadata_steps: DSE_METADATA_STEPS.load(Ordering::Relaxed),
        pointerless_steps: DSE_POINTERLESS_STEPS.load(Ordering::Relaxed),
        pointerless_active_steps: DSE_POINTERLESS_ACTIVE_STEPS.load(Ordering::Relaxed),
        pointerless_loud_steps: DSE_POINTERLESS_LOUD_STEPS.load(Ordering::Relaxed),
        pointerless_first_frame: DSE_POINTERLESS_FIRST_FRAME.load(Ordering::Relaxed),
        pointerless_last_frame: DSE_POINTERLESS_LAST_FRAME.load(Ordering::Relaxed),
        last_metadata_field01: DSE_LAST_METADATA_FIELD01.load(Ordering::Relaxed),
        last_metadata_field23: DSE_LAST_METADATA_FIELD23.load(Ordering::Relaxed),
        last_metadata_flags: DSE_LAST_METADATA_FLAGS.load(Ordering::Relaxed),
        last_metadata_pointer_mask: DSE_LAST_METADATA_POINTER_MASK.load(Ordering::Relaxed),
    };
    unsafe { output.write(status) };
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn v6_clock_abi_version() -> u32 {
    ABI_VERSION
}

#[unsafe(no_mangle)]
pub extern "C" fn v6_clock_reset(
    engine_time: f64,
    ticks: i64,
    frequency: i64,
    latency_seconds: f64,
) -> i32 {
    if !valid_time(engine_time) || frequency <= 0 || !valid_time(latency_seconds) {
        return -1;
    }

    let mut state = clock()
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    state.reset(engine_time, ticks, frequency, latency_seconds);
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn v6_clock_observe(engine_time: f64, ticks: i64, latency_target: f64) -> i32 {
    if !valid_time(engine_time) || !valid_time(latency_target) {
        return -1;
    }

    let mut state = clock()
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    state.observe(engine_time, ticks, latency_target)
}

#[unsafe(no_mangle)]
/// Writes a playback-clock projection to an ABI-compatible output structure.
///
/// # Safety
///
/// `output` must be writable and correctly aligned for one [`ClockSnapshot`]
/// for the duration of this call.
pub unsafe extern "C" fn v6_clock_snapshot(
    ticks: i64,
    display_lead: f64,
    horizon: f64,
    output: *mut ClockSnapshot,
) -> i32 {
    if output.is_null() || !display_lead.is_finite() || !valid_time(horizon) {
        return -1;
    }

    let mut state = clock()
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    if !state.initialized {
        return -2;
    }

    // SAFETY: The caller supplied a non-null output pointer to an ABI-matched
    // ClockSnapshot and retains it for the duration of this call.
    unsafe { output.write(state.snapshot(ticks, display_lead, horizon)) };
    0
}

fn calculate_correlation(source: &[f32], output: &[f32], length: usize, lag: usize) -> f64 {
    let count = length - lag;
    let mut product = 0.0_f64;
    let mut source_power = 0.0_f64;
    let mut output_power = 0.0_f64;

    for index in 0..count {
        let source_value = source[index] as f64;
        let output_value = output[index + lag] as f64;
        product += source_value * output_value;
        source_power += source_value * source_value;
        output_power += output_value * output_value;
    }

    let denominator = (source_power * output_power).sqrt();
    if denominator <= f64::EPSILON {
        0.0
    } else {
        product / denominator
    }
}

fn find_best_lag(
    source: &[f32],
    output: &[f32],
    length: usize,
    min_lag: usize,
    max_lag: usize,
    exclusion: usize,
) -> CorrelationResult {
    let mut best_lag = min_lag;
    let mut best_correlation = f64::NEG_INFINITY;

    for lag in (min_lag..=max_lag).step_by(2) {
        let correlation = calculate_correlation(source, output, length, lag);
        if correlation > best_correlation {
            best_correlation = correlation;
            best_lag = lag;
        }
    }

    let begin = min_lag.max(best_lag.saturating_sub(2));
    let end = max_lag.min(best_lag.saturating_add(2));
    for lag in begin..=end {
        let correlation = calculate_correlation(source, output, length, lag);
        if correlation > best_correlation {
            best_correlation = correlation;
            best_lag = lag;
        }
    }

    let mut second_correlation = f64::NEG_INFINITY;
    for lag in (min_lag..=max_lag).step_by(2) {
        if lag.abs_diff(best_lag) <= exclusion {
            continue;
        }
        second_correlation =
            second_correlation.max(calculate_correlation(source, output, length, lag));
    }

    CorrelationResult {
        lag: best_lag as i32,
        correlation: best_correlation,
        prominence: if second_correlation.is_finite() {
            best_correlation - second_correlation
        } else {
            best_correlation
        },
    }
}

#[unsafe(no_mangle)]
/// Finds the normalized-correlation peak between two sample buffers.
///
/// # Safety
///
/// `source` and `output` must each reference at least `length` readable `f32`
/// values. `result` must be writable and correctly aligned for one
/// [`CorrelationResult`]. All three buffers must remain valid for this call.
pub unsafe extern "C" fn v6_clock_correlate_f32(
    source: *const f32,
    output: *const f32,
    length: i32,
    min_lag: i32,
    max_lag: i32,
    exclusion: i32,
    result: *mut CorrelationResult,
) -> i32 {
    if source.is_null()
        || output.is_null()
        || result.is_null()
        || length <= 0
        || min_lag < 0
        || max_lag < min_lag
        || max_lag >= length
        || exclusion < 0
    {
        return -1;
    }

    let length = length as usize;
    // SAFETY: The validated pointers refer to at least `length` f32 values for
    // the duration of this synchronous call. The buffers are only read.
    let source = unsafe { slice::from_raw_parts(source, length) };
    // SAFETY: Same contract as `source` above.
    let output = unsafe { slice::from_raw_parts(output, length) };
    let value = find_best_lag(
        source,
        output,
        length,
        min_lag as usize,
        max_lag as usize,
        exclusion as usize,
    );
    // SAFETY: The caller supplied a non-null ABI-matched result pointer.
    unsafe { result.write(value) };
    0
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn clock_projects_monotonically_between_engine_observations() {
        let mut clock = ClockState::default();
        clock.reset(10.0, 1_000, 1_000, 0.020);
        let first = clock.snapshot(1_010, 0.0, 0.1);
        let second = clock.snapshot(1_020, 0.0, 0.1);
        assert!(second.current_time > first.current_time);
        assert!((second.projected_time - second.current_time - 0.1).abs() < 1e-9);
    }

    #[test]
    fn clock_resynchronizes_on_a_backward_jump() {
        let mut clock = ClockState::default();
        clock.reset(10.0, 1_000, 1_000, 0.0);
        let generation = clock.generation;
        assert_eq!(clock.observe(2.0, 1_100, 0.0), 1);
        assert_ne!(clock.generation, generation);
        assert!((clock.snapshot(1_100, 0.0, 0.0).current_time - 2.0).abs() < 1e-9);
    }

    #[test]
    fn clock_marks_missing_engine_feedback_as_stale() {
        let mut clock = ClockState::default();
        clock.reset(10.0, 1_000, 1_000, 0.0);
        assert_eq!(clock.snapshot(1_200, 0.0, 0.0).flags & SNAPSHOT_STALE, 0);
        assert_ne!(clock.snapshot(1_300, 0.0, 0.0).flags & SNAPSHOT_STALE, 0);
    }

    #[test]
    fn clock_learns_only_a_bounded_long_term_rate() {
        let mut clock = ClockState::default();
        clock.reset(0.0, 0, 1_000, 0.0);
        for ticks in (100..=6_000).step_by(100) {
            let engine_time = ticks as f64 / 1_000.0 * 1.0005;
            clock.observe(engine_time, ticks, 0.0);
        }

        assert!(clock.playback_rate > 1.0);
        assert!(clock.playback_rate <= MAX_PLAYBACK_RATE);
    }

    #[test]
    fn correlation_finds_a_known_delay() {
        let mut source = vec![0.0_f32; 256];
        for (index, value) in source.iter_mut().enumerate() {
            *value = ((index as f32 * 0.173).sin() + (index as f32 * 0.071).cos()) * 0.5;
        }
        let lag = 17;
        let mut output = vec![0.0_f32; source.len()];
        output[lag..].copy_from_slice(&source[..source.len() - lag]);

        let result = find_best_lag(&source, &output, source.len(), 0, 40, 3);
        assert_eq!(result.lag, lag as i32);
        assert!(result.correlation > 0.99);
    }

    #[test]
    fn breath_mixer_blocks_merge_only_for_the_same_part() {
        let mut queue = BreathEventQueue::default();
        assert!(queue.push(0x1000, 160, 161));
        assert!(queue.push(0x1000, 161, 162));
        assert!(queue.push(0x2000, 161, 162));
        assert_eq!(queue.length, 2);

        let first = queue.pop().unwrap();
        assert_eq!(first.part_handle, 0x1000);
        assert_eq!(first.begin_frame, 160);
        assert_eq!(first.end_frame, 162);

        let second = queue.pop().unwrap();
        assert_eq!(second.part_handle, 0x2000);
        assert_eq!(second.begin_frame, 161);
        assert_eq!(second.end_frame, 162);
    }

    #[cfg(windows)]
    #[test]
    fn traditional_render_core_and_breath_mixer_signatures_are_stable() {
        let mut signature = breath_hook::test_core_signature_bytes();
        assert!(breath_hook::test_core_signature(&signature));
        signature[10] ^= 1;
        assert!(!breath_hook::test_core_signature(&signature));

        let mut signature = breath_hook::test_mixer_signature_bytes();
        assert!(breath_hook::test_mixer_signature(&signature));
        signature[10] ^= 1;
        assert!(!breath_hook::test_mixer_signature(&signature));
    }

    #[cfg(windows)]
    #[test]
    fn traditional_render_context_maps_to_the_managed_part_handle() {
        assert_eq!(
            breath_hook::test_context_mapping(0x1234_5000, 0x9876_5000),
            0x9876_5000
        );
        assert_eq!(
            breath_hook::test_context_mapping(0x1234_5000, 0xabcd_5000),
            0xabcd_5000
        );
    }
}
