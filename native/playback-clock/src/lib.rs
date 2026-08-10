use std::slice;
use std::sync::{Mutex, OnceLock};

const ABI_VERSION: u32 = 1;
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
}
