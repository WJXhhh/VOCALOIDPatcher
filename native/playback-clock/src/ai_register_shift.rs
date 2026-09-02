use crate::register_shift_hook::{self, RegisterNote};
use std::alloc::{Layout, alloc_zeroed, dealloc};
use std::cell::RefCell;
use std::ffi::{c_char, c_void};
use std::ptr::{self, NonNull};
use std::slice;
use std::sync::Arc;
use std::sync::OnceLock;
use std::sync::atomic::{AtomicI32, AtomicPtr, AtomicU32, AtomicU64, Ordering};

const PAGE_EXECUTE_READWRITE: u32 = 0x40;
const PAGE_GUARD: u32 = 0x100;
const PAGE_NOACCESS: u32 = 0x01;
const MEM_COMMIT: u32 = 0x1000;
const IMAGE_SCN_MEM_EXECUTE: u32 = 0x2000_0000;
const IMAGE_SCN_MEM_READ: u32 = 0x4000_0000;
const WRAPPER_SLOT_INDEX: usize = 14;
const WRAPPER_SLOT_OFFSET: usize = WRAPPER_SLOT_INDEX * std::mem::size_of::<usize>();
const INPUT_RECORD_SIZE: usize = 0x88;
const SEGMENT_RECORD_SIZE: usize = 0x28;
const INPUT_DESCRIPTOR_SIZE: usize = 0x18;
const ARRAY_DESCRIPTOR_SIZE: usize = 0x10;
const FEATURE_DESCRIPTOR_SIZE: usize = 0x18;
const MAX_RECORDS: usize = 100_000;
const MAX_FEATURE_STRIDE: usize = 4096;
const SCRATCH_BUDGET: usize = 64 * 1024 * 1024;
const BIT_WRAPPER_LOCATED: u32 = 1;
const BIT_WRAPPER_INSTALLED: u32 = 1 << 1;
const COMPENSATION_BASELINE: u32 = 0;
const COMPENSATION_EXACT_BASE_DELTA: u32 = 3;

const FUNC_9: &[u8] = b"Func_9cbce37f\0";
const FUNC_CFC: &[u8] = b"Func_cfc85a30\0";
const FUNC_417: &[u8] = b"Func_417eec3e\0";
const FUNC_236: &[u8] = b"Func_236a1ecf\0";
const FUNC_950: &[u8] = b"Func_95031259\0";
const FUNC_52: &[u8] = b"Func_52f85f15\0";
const FUNC_2D: &[u8] = b"Func_2db29822\0";

const WRAPPER_PREFIX: &[u8] = &[
    0x48, 0x89, 0x5c, 0x24, 0x10, 0x57, 0x48, 0x83, 0xec, 0x20, 0x48, 0x8b, 0x59, 0x08, 0xb9, 0x08,
    0x00, 0x00, 0x00, 0xe8,
];
const WRAPPER_MIDDLE: &[u8] = &[
    0x48, 0x8b, 0xf8, 0x48, 0x89, 0x44, 0x24, 0x30, 0x48, 0x89, 0x18, 0x48, 0x89, 0x44, 0x24, 0x30,
    0x48, 0x8b, 0xc8, 0xe8,
];
const OUTER_PREFIX: &[u8] = &[
    0x48, 0x8b, 0xc4, 0x48, 0x89, 0x58, 0x10, 0x48, 0x89, 0x70, 0x18, 0x48, 0x89, 0x78, 0x20, 0x55,
    0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57, 0x48, 0x8d, 0xa8, 0x38, 0xfa, 0xff, 0xff,
];

#[repr(C)]
struct MemoryBasicInformation {
    base_address: *mut c_void,
    allocation_base: *mut c_void,
    allocation_protect: u32,
    partition_id: u16,
    _padding0: u16,
    region_size: usize,
    state: u32,
    protect: u32,
    kind: u32,
    _padding1: u32,
}

#[derive(Clone, Copy)]
struct ImageLayout {
    size: usize,
    code_start: usize,
    code_size: usize,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct Originals {
    module: usize,
    func9: usize,
    func_cfc: usize,
    func417: usize,
    func236: usize,
    func950: usize,
    func52: usize,
    func2d: usize,
}

type TaskFn = unsafe extern "system-unwind" fn(*mut c_void, *mut c_void, *mut c_void, *mut c_void);
type Func9 = unsafe extern "system-unwind" fn(*mut c_void, *const u8) -> u64;
type FuncCfc = unsafe extern "system-unwind" fn(*mut c_void, *const u8, *mut u8) -> u64;
type Func417 = unsafe extern "system-unwind" fn(*mut c_void, *const u8, *mut u8) -> u64;
type Func236 = unsafe extern "system-unwind" fn(*mut c_void, *const u8) -> *mut c_void;
type Func950 = unsafe extern "system-unwind" fn(*mut c_void);
type Func52 = unsafe extern "system-unwind" fn(*mut c_void) -> u32;
type Func2d = unsafe extern "system-unwind" fn(*mut c_void, *const u8, *mut f32) -> u64;

#[link(name = "kernel32")]
unsafe extern "system" {
    fn GetModuleHandleW(module_name: *const u16) -> *mut c_void;
    fn GetProcAddress(module: *mut c_void, name: *const c_char) -> *mut c_void;
    fn GetCurrentProcess() -> *mut c_void;
    fn VirtualProtect(
        address: *mut c_void,
        size: usize,
        new_protect: u32,
        old_protect: *mut u32,
    ) -> i32;
    fn VirtualQuery(
        address: *const c_void,
        information: *mut MemoryBasicInformation,
        length: usize,
    ) -> usize;
    fn FlushInstructionCache(process: *mut c_void, base_address: *const c_void, size: usize)
    -> i32;
}

static INSTALL_LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());
static ORIGINAL_TASK: AtomicPtr<c_void> = AtomicPtr::new(ptr::null_mut());
static ORIGINALS: OnceLock<Originals> = OnceLock::new();
static INSTALL_RESULT: AtomicI32 = AtomicI32::new(i32::MIN);
static INSTALL_BITMAP: AtomicU32 = AtomicU32::new(0);
static TABLE_READY_SCOPES: AtomicU64 = AtomicU64::new(0);
static FALLBACK_SCOPES: AtomicU64 = AtomicU64::new(0);
static VALIDATION_FAILURES: AtomicU64 = AtomicU64::new(0);
static LAST_PART: AtomicU64 = AtomicU64::new(0);
static LAST_EPOCH: AtomicU64 = AtomicU64::new(0);
static LAST_TABLE: AtomicU64 = AtomicU64::new(0);
static LAST_FUNC417_CALLS: AtomicU64 = AtomicU64::new(0);
static LAST_FUNC2D_CALLS: AtomicU64 = AtomicU64::new(0);
static LAST_EMITTED_ROWS: AtomicU64 = AtomicU64::new(0);
static LAST_CONSUMED_ROWS: AtomicU64 = AtomicU64::new(0);
static LAST_ERROR: AtomicI32 = AtomicI32::new(0);
static LAST_COMPENSATION_MODE: AtomicU32 = AtomicU32::new(COMPENSATION_BASELINE);

thread_local! {
    static SCOPES: RefCell<Vec<Scope>> = const { RefCell::new(Vec::new()) };
}

pub(super) struct AiStatus {
    pub install_result: i32,
    pub install_bitmap: u32,
    pub table_ready_scopes: u64,
    pub fallback_scopes: u64,
    pub validation_failures: u64,
    pub last_part: u64,
    pub last_epoch: u64,
    pub last_table: u64,
    pub last_func417_calls: u64,
    pub last_func2d_calls: u64,
    pub last_emitted_rows: u64,
    pub last_consumed_rows: u64,
    pub last_error: i32,
    pub last_compensation_mode: u32,
}

struct AlignedBuffer {
    pointer: NonNull<u8>,
    length: usize,
    layout: Option<Layout>,
}

impl AlignedBuffer {
    fn zeroed(length: usize) -> Option<Self> {
        if length == 0 {
            return Some(Self {
                pointer: NonNull::<[u8; 32]>::dangling().cast(),
                length: 0,
                layout: None,
            });
        }
        let layout = Layout::from_size_align(length, 32).ok()?;
        let pointer = NonNull::new(unsafe { alloc_zeroed(layout) })?;
        Some(Self {
            pointer,
            length,
            layout: Some(layout),
        })
    }

    fn from_slice(bytes: &[u8]) -> Option<Self> {
        let value = Self::zeroed(bytes.len())?;
        if !bytes.is_empty() {
            unsafe {
                ptr::copy_nonoverlapping(bytes.as_ptr(), value.pointer.as_ptr(), bytes.len())
            };
        }
        Some(value)
    }

    fn as_ptr(&self) -> *const u8 {
        self.pointer.as_ptr()
    }

    fn as_mut_ptr(&mut self) -> *mut u8 {
        self.pointer.as_ptr()
    }

    fn as_slice(&self) -> &[u8] {
        unsafe { slice::from_raw_parts(self.as_ptr(), self.length) }
    }
}

impl Drop for AlignedBuffer {
    fn drop(&mut self) {
        if let Some(layout) = self.layout {
            unsafe { dealloc(self.pointer.as_ptr(), layout) };
        }
    }
}

struct PendingFill {
    descriptor_tail: [u8; INPUT_DESCRIPTOR_SIZE - 8],
    baseline: AlignedBuffer,
    shifted: AlignedBuffer,
    record_count: usize,
    expected_count: usize,
}

struct Segments {
    baseline: Vec<u8>,
    shifted: Vec<u8>,
    count: usize,
}

enum Phase {
    AwaitCount,
    Pending(PendingFill),
    Segments(Segments),
    Disabled,
}

struct CurrentWindow {
    baseline_features: AlignedBuffer,
    actual_features: *mut u8,
    feature_bytes: usize,
    stride: usize,
    rows: usize,
    cursor: usize,
    shadow_state: *mut c_void,
    actual_state: *mut c_void,
    exact: bool,
}

struct Scope {
    part: u64,
    epoch: u64,
    table: usize,
    notes: Option<Arc<[RegisterNote]>>,
    phase: Phase,
    window: Option<CurrentWindow>,
    table_ready: bool,
    busy: bool,
    fallback: bool,
    validation_failures: u64,
    error: i32,
    func417_calls: u64,
    func2d_calls: u64,
    emitted_rows: u64,
    consumed_rows: u64,
    compensation_mode: u32,
}

impl Scope {
    fn new(part: u64, table: usize, table_ready: bool) -> Self {
        let snapshot = register_shift_hook::try_part_snapshot(part);
        let (epoch, notes) = snapshot.map_or((0, None), |(epoch, notes)| (epoch, Some(notes)));
        Self {
            part,
            epoch,
            table,
            notes,
            phase: Phase::AwaitCount,
            window: None,
            table_ready,
            busy: false,
            fallback: !table_ready,
            validation_failures: 0,
            error: 0,
            func417_calls: 0,
            func2d_calls: 0,
            emitted_rows: 0,
            consumed_rows: 0,
            compensation_mode: COMPENSATION_BASELINE,
        }
    }

    fn fail(&mut self, error: i32) {
        self.phase = Phase::Disabled;
        self.fallback = true;
        self.validation_failures = self.validation_failures.saturating_add(1);
        self.error = error;
    }
}

struct ScopeGuard;

impl Drop for ScopeGuard {
    fn drop(&mut self) {
        let mut removed = SCOPES.with(|scopes| scopes.try_borrow_mut().ok()?.pop());
        let Some(mut scope) = removed.take() else {
            return;
        };
        if let Some(window) = scope.window.as_mut()
            && !window.shadow_state.is_null()
            && let Some(originals) = ORIGINALS.get().copied()
        {
            let release: Func950 = unsafe { std::mem::transmute(originals.func950) };
            let shadow = std::mem::replace(&mut window.shadow_state, ptr::null_mut());
            unsafe { release(shadow) };
        }
        if scope.table_ready {
            TABLE_READY_SCOPES.fetch_add(1, Ordering::Relaxed);
        }
        if scope.fallback {
            FALLBACK_SCOPES.fetch_add(1, Ordering::Relaxed);
        }
        VALIDATION_FAILURES.fetch_add(scope.validation_failures, Ordering::Relaxed);
        LAST_PART.store(scope.part, Ordering::Relaxed);
        LAST_EPOCH.store(scope.epoch, Ordering::Relaxed);
        LAST_TABLE.store(scope.table as u64, Ordering::Relaxed);
        LAST_FUNC417_CALLS.store(scope.func417_calls, Ordering::Relaxed);
        LAST_FUNC2D_CALLS.store(scope.func2d_calls, Ordering::Relaxed);
        LAST_EMITTED_ROWS.store(scope.emitted_rows, Ordering::Relaxed);
        LAST_CONSUMED_ROWS.store(scope.consumed_rows, Ordering::Relaxed);
        LAST_ERROR.store(scope.error, Ordering::Relaxed);
        LAST_COMPENSATION_MODE.store(scope.compensation_mode, Ordering::Relaxed);
    }
}

pub(super) fn status() -> AiStatus {
    AiStatus {
        install_result: INSTALL_RESULT.load(Ordering::Relaxed),
        install_bitmap: INSTALL_BITMAP.load(Ordering::Acquire),
        table_ready_scopes: TABLE_READY_SCOPES.load(Ordering::Relaxed),
        fallback_scopes: FALLBACK_SCOPES.load(Ordering::Relaxed),
        validation_failures: VALIDATION_FAILURES.load(Ordering::Relaxed),
        last_part: LAST_PART.load(Ordering::Relaxed),
        last_epoch: LAST_EPOCH.load(Ordering::Relaxed),
        last_table: LAST_TABLE.load(Ordering::Relaxed),
        last_func417_calls: LAST_FUNC417_CALLS.load(Ordering::Relaxed),
        last_func2d_calls: LAST_FUNC2D_CALLS.load(Ordering::Relaxed),
        last_emitted_rows: LAST_EMITTED_ROWS.load(Ordering::Relaxed),
        last_consumed_rows: LAST_CONSUMED_ROWS.load(Ordering::Relaxed),
        last_error: LAST_ERROR.load(Ordering::Relaxed),
        last_compensation_mode: LAST_COMPENSATION_MODE.load(Ordering::Relaxed),
    }
}

fn checked_bytes(count: usize, stride: usize, limit: usize) -> Option<usize> {
    let bytes = count.checked_mul(stride)?;
    (bytes <= limit).then_some(bytes)
}

unsafe fn image_layout(module: *mut u8) -> Result<ImageLayout, i32> {
    if module.is_null() || unsafe { ptr::read_unaligned(module.cast::<u16>()) } != 0x5a4d {
        return Err(-2);
    }
    let nt_offset = unsafe { ptr::read_unaligned(module.add(0x3c).cast::<u32>()) } as usize;
    let nt = unsafe { module.add(nt_offset) };
    if unsafe { ptr::read_unaligned(nt.cast::<u32>()) } != 0x0000_4550 {
        return Err(-2);
    }
    let optional = unsafe { nt.add(24) };
    if unsafe { ptr::read_unaligned(optional.cast::<u16>()) } != 0x20b {
        return Err(-2);
    }
    let size = unsafe { ptr::read_unaligned(optional.add(56).cast::<u32>()) } as usize;
    if !(0x1000..=0x1000_0000).contains(&size) {
        return Err(-2);
    }
    let section_count = unsafe { ptr::read_unaligned(nt.add(6).cast::<u16>()) } as usize;
    let optional_size = unsafe { ptr::read_unaligned(nt.add(20).cast::<u16>()) } as usize;
    let sections = unsafe { nt.add(24 + optional_size) };
    let mut code = None;
    for index in 0..section_count {
        let section = unsafe { sections.add(index * 40) };
        let characteristics = unsafe { ptr::read_unaligned(section.add(36).cast::<u32>()) };
        if characteristics & IMAGE_SCN_MEM_EXECUTE == 0 {
            continue;
        }
        let start = unsafe { ptr::read_unaligned(section.add(12).cast::<u32>()) } as usize;
        let length = unsafe { ptr::read_unaligned(section.add(8).cast::<u32>()) } as usize;
        let length = length.min(size.saturating_sub(start));
        if length == 0 || code.replace((start, length)).is_some() {
            return Err(-9);
        }
    }
    let (code_start, code_size) = code.ok_or(-3)?;
    Ok(ImageLayout {
        size,
        code_start,
        code_size,
    })
}

unsafe fn section_ranges(
    module: *mut u8,
    layout: ImageLayout,
    readable_non_executable: bool,
) -> Result<Vec<(usize, usize)>, i32> {
    let nt_offset = unsafe { ptr::read_unaligned(module.add(0x3c).cast::<u32>()) } as usize;
    let nt = unsafe { module.add(nt_offset) };
    let section_count = unsafe { ptr::read_unaligned(nt.add(6).cast::<u16>()) } as usize;
    let optional_size = unsafe { ptr::read_unaligned(nt.add(20).cast::<u16>()) } as usize;
    let sections = unsafe { nt.add(24 + optional_size) };
    let mut result = Vec::new();
    for index in 0..section_count {
        let section = unsafe { sections.add(index * 40) };
        let characteristics = unsafe { ptr::read_unaligned(section.add(36).cast::<u32>()) };
        if readable_non_executable
            && (characteristics & IMAGE_SCN_MEM_READ == 0
                || characteristics & IMAGE_SCN_MEM_EXECUTE != 0)
        {
            continue;
        }
        let start = unsafe { ptr::read_unaligned(section.add(12).cast::<u32>()) } as usize;
        let length = unsafe { ptr::read_unaligned(section.add(8).cast::<u32>()) } as usize;
        let end = start.saturating_add(length).min(layout.size);
        if start < end {
            result.push((start, end));
        }
    }
    Ok(result)
}

unsafe fn resolve_rel32(call: *const u8) -> Option<*mut u8> {
    if call.is_null() || unsafe { *call } != 0xe8 {
        return None;
    }
    let displacement = unsafe { ptr::read_unaligned(call.add(1).cast::<i32>()) } as isize;
    Some(unsafe { call.add(5).offset(displacement) }.cast_mut())
}

unsafe fn find_wrapper(module: *mut u8, layout: ImageLayout) -> Result<*mut u8, i32> {
    let code = unsafe { module.add(layout.code_start) };
    let bytes = unsafe { slice::from_raw_parts(code, layout.code_size) };
    if bytes.len() < 48 {
        return Err(-3);
    }
    let mut found = None;
    for offset in 0..=bytes.len().saturating_sub(48) {
        let candidate = unsafe { code.add(offset) };
        if &bytes[offset..offset + WRAPPER_PREFIX.len()] != WRAPPER_PREFIX
            || &bytes[offset + 24..offset + 24 + WRAPPER_MIDDLE.len()] != WRAPPER_MIDDLE
        {
            continue;
        }
        let Some(target) = (unsafe { resolve_rel32(candidate.add(43)) }) else {
            continue;
        };
        let target_value = target as usize;
        let code_begin = code as usize;
        if target_value < code_begin
            || target_value
                .checked_add(OUTER_PREFIX.len())
                .is_none_or(|end| end > code_begin + layout.code_size)
            || unsafe { slice::from_raw_parts(target, OUTER_PREFIX.len()) } != OUTER_PREFIX
        {
            continue;
        }
        if found.replace(candidate).is_some() {
            return Err(-9);
        }
    }
    found.ok_or(-3)
}

unsafe fn find_wrapper_slot(
    module: *mut u8,
    layout: ImageLayout,
    wrapper: *mut u8,
) -> Result<*mut *mut c_void, i32> {
    let code_begin = unsafe { module.add(layout.code_start) } as usize;
    let code_end = code_begin + layout.code_size;
    let mut found = None;
    for (start, end) in unsafe { section_ranges(module, layout, true)? } {
        let aligned = (start + 7) & !7;
        for offset in (aligned..=end.saturating_sub(8)).step_by(8) {
            let location = unsafe { module.add(offset) }.cast::<*mut c_void>();
            if unsafe { ptr::read_unaligned(location) } != wrapper.cast()
                || offset < WRAPPER_SLOT_OFFSET
            {
                continue;
            }
            let vtable = unsafe { location.sub(WRAPPER_SLOT_INDEX) };
            let mut plausible = true;
            for index in [0usize, 1, 2, 13, 15] {
                let value = unsafe { ptr::read_unaligned(vtable.add(index)) } as usize;
                plausible &= value >= code_begin && value < code_end;
            }
            if !plausible {
                continue;
            }
            if found.replace(location).is_some() {
                return Err(-9);
            }
        }
    }
    found.ok_or(-3)
}

pub(super) fn install() -> i32 {
    if !ORIGINAL_TASK.load(Ordering::Acquire).is_null() {
        return 0;
    }
    let _guard = INSTALL_LOCK
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    if !ORIGINAL_TASK.load(Ordering::Acquire).is_null() {
        return 0;
    }
    let name: Vec<u16> = "VSM.dll\0".encode_utf16().collect();
    let module = unsafe { GetModuleHandleW(name.as_ptr()) }.cast::<u8>();
    if module.is_null() {
        INSTALL_RESULT.store(-6, Ordering::Relaxed);
        return -6;
    }
    let result = (|| -> Result<(), i32> {
        let layout = unsafe { image_layout(module)? };
        let wrapper = unsafe { find_wrapper(module, layout)? };
        let slot = unsafe { find_wrapper_slot(module, layout, wrapper)? };
        INSTALL_BITMAP.fetch_or(BIT_WRAPPER_LOCATED, Ordering::Release);
        let mut old = 0u32;
        if unsafe {
            VirtualProtect(
                slot.cast(),
                std::mem::size_of::<usize>(),
                PAGE_EXECUTE_READWRITE,
                &mut old,
            )
        } == 0
        {
            return Err(-5);
        }
        let atomic = unsafe { &*slot.cast::<AtomicPtr<c_void>>() };
        let previous = atomic
            .compare_exchange(
                wrapper.cast(),
                task_hook as *mut c_void,
                Ordering::AcqRel,
                Ordering::Acquire,
            )
            .unwrap_or_else(|value| value);
        let mut ignored = 0u32;
        unsafe {
            VirtualProtect(slot.cast(), std::mem::size_of::<usize>(), old, &mut ignored);
            FlushInstructionCache(
                GetCurrentProcess(),
                slot.cast(),
                std::mem::size_of::<usize>(),
            );
        }
        if previous != wrapper.cast() && previous != task_hook as *mut c_void {
            return Err(-10);
        }
        ORIGINAL_TASK.store(wrapper.cast(), Ordering::Release);
        INSTALL_BITMAP.fetch_or(BIT_WRAPPER_INSTALLED, Ordering::Release);
        Ok(())
    })();
    let value = match result {
        Ok(()) => 1,
        Err(error) => error,
    };
    INSTALL_RESULT.store(value, Ordering::Relaxed);
    value
}

fn protection_readable(protect: u32) -> bool {
    protect & (PAGE_GUARD | PAGE_NOACCESS) == 0
        && matches!(protect & 0xff, 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80)
}

fn protection_writable(protect: u32) -> bool {
    protect & (PAGE_GUARD | PAGE_NOACCESS) == 0
        && matches!(protect & 0xff, 0x04 | 0x08 | 0x40 | 0x80)
}

unsafe fn valid_range(pointer: *const u8, length: usize, writable: bool) -> bool {
    if length == 0 {
        return true;
    }
    let Some(end) = (pointer as usize).checked_add(length) else {
        return false;
    };
    if pointer.is_null() || end <= pointer as usize {
        return false;
    }
    let mut cursor = pointer as usize;
    while cursor < end {
        let mut information = MemoryBasicInformation {
            base_address: ptr::null_mut(),
            allocation_base: ptr::null_mut(),
            allocation_protect: 0,
            partition_id: 0,
            _padding0: 0,
            region_size: 0,
            state: 0,
            protect: 0,
            kind: 0,
            _padding1: 0,
        };
        if unsafe {
            VirtualQuery(
                cursor as *const c_void,
                &mut information,
                std::mem::size_of::<MemoryBasicInformation>(),
            )
        } != std::mem::size_of::<MemoryBasicInformation>()
            || information.state != MEM_COMMIT
            || if writable {
                !protection_writable(information.protect)
            } else {
                !protection_readable(information.protect)
            }
        {
            return false;
        }
        let region_end =
            (information.base_address as usize).saturating_add(information.region_size);
        if region_end <= cursor {
            return false;
        }
        cursor = region_end.min(end);
    }
    true
}

unsafe fn read_pointer(address: *const u8) -> Option<*mut u8> {
    if !unsafe { valid_range(address, std::mem::size_of::<usize>(), false) } {
        return None;
    }
    let value = unsafe { ptr::read_unaligned(address.cast::<*mut u8>()) };
    (!value.is_null()).then_some(value)
}

fn add_address(pointer: *mut u8, offset: usize) -> Option<*mut u8> {
    (pointer as usize)
        .checked_add(offset)
        .map(|value| value as *mut u8)
}

unsafe fn resolve_task_context(task: *mut c_void) -> Option<(u64, *mut u8)> {
    let task = task.cast::<u8>();
    let renderer = unsafe { read_pointer(add_address(task, 8)?)? };
    let part = unsafe { read_pointer(add_address(renderer, 0x10)?)? } as usize as u64;
    let p0 = unsafe { read_pointer(renderer)? };
    let p1 = unsafe { read_pointer(p0)? };
    let host = unsafe { read_pointer(add_address(p1, 0x28)?)? };
    let table = add_address(host, 0x150)?;
    if unsafe { valid_range(table, 0x78, false) } {
        Some((part, table))
    } else {
        None
    }
}

unsafe fn exported(module: *mut c_void, name: &[u8]) -> Option<usize> {
    let value = unsafe { GetProcAddress(module, name.as_ptr().cast()) };
    (!value.is_null()).then_some(value as usize)
}

unsafe fn resolve_originals(table: *mut u8) -> Option<Originals> {
    let module = unsafe { read_pointer(table)? }.cast::<c_void>();
    unsafe { image_layout(module.cast()) }.ok()?;
    let originals = Originals {
        module: module as usize,
        func9: unsafe { exported(module, FUNC_9)? },
        func_cfc: unsafe { exported(module, FUNC_CFC)? },
        func417: unsafe { exported(module, FUNC_417)? },
        func236: unsafe { exported(module, FUNC_236)? },
        func950: unsafe { exported(module, FUNC_950)? },
        func52: unsafe { exported(module, FUNC_52)? },
        func2d: unsafe { exported(module, FUNC_2D)? },
    };
    if let Some(published) = ORIGINALS.get() {
        (*published == originals).then_some(originals)
    } else {
        match ORIGINALS.set(originals) {
            Ok(()) => Some(originals),
            Err(value) => ORIGINALS
                .get()
                .is_some_and(|published| *published == value)
                .then_some(value),
        }
    }
}

unsafe fn replace_slot(table: *mut u8, offset: usize, original: usize, hook: *mut c_void) -> bool {
    let slot = unsafe { table.add(offset) }.cast::<*mut c_void>();
    if slot as usize & (std::mem::align_of::<AtomicPtr<c_void>>() - 1) != 0 {
        return false;
    }
    if !unsafe { valid_range(slot.cast(), std::mem::size_of::<usize>(), true) } {
        return false;
    }
    let current = unsafe { ptr::read_volatile(slot) };
    if current == hook {
        return true;
    }
    if current as usize != original {
        return false;
    }
    let atomic = unsafe { &*slot.cast::<AtomicPtr<c_void>>() };
    atomic
        .compare_exchange(current, hook, Ordering::AcqRel, Ordering::Acquire)
        .is_ok()
}

unsafe fn ensure_table(table: *mut u8) -> bool {
    let Some(originals) = (unsafe { resolve_originals(table) }) else {
        return false;
    };
    if unsafe { ptr::read_unaligned(table.add(0x68).cast::<usize>()) } != originals.func52 {
        return false;
    }
    let replacements = [
        (0x20, originals.func9, func9_hook as *mut c_void),
        (0x28, originals.func_cfc, func_cfc_hook as *mut c_void),
        (0x50, originals.func417, func417_hook as *mut c_void),
        (0x58, originals.func236, func236_hook as *mut c_void),
        (0x60, originals.func950, func950_hook as *mut c_void),
        (0x70, originals.func2d, func2d_hook as *mut c_void),
    ];
    for (offset, original, hook) in replacements {
        if !unsafe { replace_slot(table, offset, original, hook) } {
            return false;
        }
    }
    (unsafe { ptr::read_volatile(table.add(0x68).cast::<usize>()) }) == originals.func52
}

unsafe extern "system-unwind" fn task_hook(
    task: *mut c_void,
    arg2: *mut c_void,
    arg3: *mut c_void,
    arg4: *mut c_void,
) {
    let original_pointer = ORIGINAL_TASK.load(Ordering::Acquire);
    if original_pointer.is_null() {
        return;
    }
    let original: TaskFn = unsafe { std::mem::transmute(original_pointer) };
    let context = unsafe { resolve_task_context(task) };
    let (part, table, ready) = match context {
        Some((part, table)) => (part, table as usize, unsafe { ensure_table(table) }),
        None => (0, 0, false),
    };
    let pushed = SCOPES.with(|scopes| {
        let Some(mut scopes) = scopes.try_borrow_mut().ok() else {
            return false;
        };
        scopes.push(Scope::new(part, table, ready));
        true
    });
    if !pushed {
        unsafe { original(task, arg2, arg3, arg4) };
        return;
    }
    let _guard = ScopeGuard;
    unsafe { original(task, arg2, arg3, arg4) };
}

fn with_scope_mut<R>(callback: impl FnOnce(&mut Scope) -> R) -> Option<R> {
    SCOPES.with(|scopes| {
        let mut scopes = scopes.try_borrow_mut().ok()?;
        let scope = scopes.last_mut()?;
        Some(callback(scope))
    })
}

unsafe fn descriptor(pointer: *const u8, size: usize) -> Option<Vec<u8>> {
    if !unsafe { valid_range(pointer, size, false) } {
        return None;
    }
    Some(unsafe { slice::from_raw_parts(pointer, size) }.to_vec())
}

fn read_i32(bytes: &[u8], offset: usize) -> i32 {
    i32::from_ne_bytes(bytes[offset..offset + 4].try_into().unwrap_or_default())
}

fn write_i32(bytes: &mut [u8], offset: usize, value: i32) {
    bytes[offset..offset + 4].copy_from_slice(&value.to_ne_bytes());
}

fn write_pointer(bytes: &mut [u8], pointer: *const u8) {
    bytes[..8].copy_from_slice(&(pointer as usize).to_ne_bytes());
}

fn same_time(left: f64, right: f64) -> bool {
    left.to_bits() == right.to_bits()
        || (left - right).abs()
            <= 1e-7_f64.max(16.0 * f64::EPSILON * left.abs().max(right.abs()).max(1.0))
}

fn build_shifted_input(
    input: *const u8,
    descriptor_bytes: &[u8],
    notes: &[RegisterNote],
) -> Option<(AlignedBuffer, AlignedBuffer, usize)> {
    let count = read_i32(descriptor_bytes, 8);
    if count < 0 || count as usize > MAX_RECORDS {
        return None;
    }
    let count = count as usize;
    let bytes = checked_bytes(count, INPUT_RECORD_SIZE, SCRATCH_BUDGET)?;
    if !unsafe { valid_range(input, bytes, false) } {
        return None;
    }
    let source = if bytes == 0 {
        &[]
    } else {
        unsafe { slice::from_raw_parts(input, bytes) }
    };
    let baseline = AlignedBuffer::from_slice(source)?;
    let mut shifted = AlignedBuffer::from_slice(source)?;
    let mut last_ordinal = -1i32;
    for index in 0..count {
        let record = &source[index * INPUT_RECORD_SIZE..(index + 1) * INPUT_RECORD_SIZE];
        if record[0x20] > 1 {
            return None;
        }
        if record[0x20] == 1 {
            continue;
        }
        let begin = f64::from_ne_bytes(record[0..8].try_into().ok()?);
        let end = f64::from_ne_bytes(record[8..16].try_into().ok()?);
        let pitch = f32::from_ne_bytes(record[0x10..0x14].try_into().ok()?);
        let phonemes = read_i32(record, 0x24);
        if !begin.is_finite()
            || !end.is_finite()
            || end < begin
            || !pitch.is_finite()
            || !(0..=8).contains(&phonemes)
        {
            return None;
        }
        let mut matched = None;
        for note in notes.iter().filter(|note| note.ordinal > last_ordinal) {
            let expected_pitch = (note.pitch_cents.clamp(-4500, 2400)) as f32;
            if expected_pitch.to_bits() == pitch.to_bits()
                && same_time(note.begin_seconds, begin)
                && same_time(note.end_seconds, end)
            {
                if matched.is_some() {
                    return None;
                }
                matched = Some(note);
            }
        }
        let note = matched?;
        last_ordinal = note.ordinal;
        let shifted_pitch = (note.pitch_cents + note.semitones * 100).clamp(-4500, 2400) as f32;
        let destination = unsafe { shifted.as_mut_ptr().add(index * INPUT_RECORD_SIZE + 0x10) };
        unsafe { ptr::write_unaligned(destination.cast::<f32>(), shifted_pitch) };
    }
    Some((baseline, shifted, count))
}

unsafe fn call_func9(original: Func9, state: *mut c_void, descriptor: &[u8]) -> u64 {
    unsafe { original(state, descriptor.as_ptr()) }
}

unsafe extern "system-unwind" fn func9_hook(state: *mut c_void, input: *const u8) -> u64 {
    let Some(originals) = ORIGINALS.get().copied() else {
        return 0;
    };
    let original: Func9 = unsafe { std::mem::transmute(originals.func9) };
    let Some(mut desc) = (unsafe { descriptor(input, INPUT_DESCRIPTOR_SIZE) }) else {
        return unsafe { original(state, input) };
    };
    let input_pointer = usize::from_ne_bytes(desc[..8].try_into().unwrap_or_default()) as *const u8;
    let prepared = with_scope_mut(|scope| {
        if !scope.table_ready || scope.busy || !matches!(scope.phase, Phase::AwaitCount) {
            return None;
        }
        if scope.notes.is_none()
            && let Some((epoch, notes)) = register_shift_hook::try_part_snapshot(scope.part)
        {
            scope.epoch = epoch;
            scope.notes = Some(notes);
        }
        let notes = Arc::clone(scope.notes.as_ref()?);
        let Some((baseline, shifted, count)) = build_shifted_input(input_pointer, &desc, &notes)
        else {
            scope.fail(-20);
            return None;
        };
        scope.busy = true;
        Some((baseline, shifted, count))
    })
    .flatten();
    let Some((baseline, shifted, count)) = prepared else {
        return unsafe { original(state, input) };
    };
    let mut shifted_desc = desc.clone();
    write_pointer(&mut shifted_desc, shifted.as_ptr());
    let shifted_result = unsafe { call_func9(original, state, &shifted_desc) };
    let baseline_result = unsafe { original(state, input) };
    let shifted_count = shifted_result as u32 as i32;
    let baseline_count = baseline_result as u32 as i32;
    let valid = shifted_count >= 0
        && baseline_count == shifted_count
        && baseline_count as usize <= MAX_RECORDS;
    let mut tail = [0u8; INPUT_DESCRIPTOR_SIZE - 8];
    tail.copy_from_slice(&desc.split_off(8));
    with_scope_mut(|scope| {
        scope.busy = false;
        if valid {
            scope.phase = Phase::Pending(PendingFill {
                descriptor_tail: tail,
                baseline,
                shifted,
                record_count: count,
                expected_count: baseline_count as usize,
            });
        } else {
            scope.fail(-21);
        }
    });
    baseline_result
}

fn segments_compatible(baseline: &[u8], shifted: &[u8], count: usize) -> bool {
    for index in 0..count {
        let begin = index * SEGMENT_RECORD_SIZE;
        let end = begin + SEGMENT_RECORD_SIZE;
        for offset in begin..end {
            if (begin + 0x10..begin + 0x14).contains(&offset) {
                continue;
            }
            if baseline[offset] != shifted[offset] {
                return false;
            }
        }
    }
    true
}

unsafe extern "system-unwind" fn func_cfc_hook(
    state: *mut c_void,
    input: *const u8,
    output: *mut u8,
) -> u64 {
    let Some(originals) = ORIGINALS.get().copied() else {
        return 0;
    };
    let original: FuncCfc = unsafe { std::mem::transmute(originals.func_cfc) };
    let Some(input_desc) = (unsafe { descriptor(input, INPUT_DESCRIPTOR_SIZE) }) else {
        return unsafe { original(state, input, output) };
    };
    let Some(output_desc) = (unsafe { descriptor(output, ARRAY_DESCRIPTOR_SIZE) }) else {
        return unsafe { original(state, input, output) };
    };
    let input_pointer =
        usize::from_ne_bytes(input_desc[..8].try_into().unwrap_or_default()) as *const u8;
    let pending = with_scope_mut(|scope| {
        if scope.busy {
            return None;
        }
        let phase = std::mem::replace(&mut scope.phase, Phase::Disabled);
        let Phase::Pending(pending) = phase else {
            scope.phase = phase;
            return None;
        };
        let bytes = pending.record_count.checked_mul(INPUT_RECORD_SIZE)?;
        let current = if bytes == 0 {
            &[]
        } else if unsafe { valid_range(input_pointer, bytes, false) } {
            unsafe { slice::from_raw_parts(input_pointer, bytes) }
        } else {
            scope.fail(-22);
            return None;
        };
        if input_desc[8..] != pending.descriptor_tail || current != pending.baseline.as_slice() {
            scope.fail(-22);
            return None;
        }
        scope.busy = true;
        Some(pending)
    })
    .flatten();
    let Some(pending) = pending else {
        return unsafe { original(state, input, output) };
    };
    let segment_bytes =
        match checked_bytes(pending.expected_count, SEGMENT_RECORD_SIZE, SCRATCH_BUDGET) {
            Some(value) => value,
            None => {
                with_scope_mut(|scope| {
                    scope.busy = false;
                    scope.fail(-23);
                });
                return unsafe { original(state, input, output) };
            }
        };
    let Some(shifted_output) = AlignedBuffer::zeroed(segment_bytes) else {
        with_scope_mut(|scope| {
            scope.busy = false;
            scope.fail(-24);
        });
        return unsafe { original(state, input, output) };
    };
    let mut shifted_input_desc = input_desc.clone();
    write_pointer(&mut shifted_input_desc, pending.shifted.as_ptr());
    let mut shifted_output_desc = output_desc.clone();
    write_pointer(&mut shifted_output_desc, shifted_output.as_ptr());
    write_i32(&mut shifted_output_desc, 8, pending.expected_count as i32);
    let shifted_result = unsafe {
        original(
            state,
            shifted_input_desc.as_ptr(),
            shifted_output_desc.as_mut_ptr(),
        )
    };
    let baseline_result = unsafe { original(state, input, output) };
    let shifted_rows = read_i32(&shifted_output_desc, 8);
    let baseline_rows = unsafe { ptr::read_unaligned(output.add(8).cast::<i32>()) };
    let output_pointer = unsafe { ptr::read_unaligned(output.cast::<*mut u8>()) };
    let valid = shifted_rows >= 0
        && baseline_rows == shifted_rows
        && baseline_rows as usize == pending.expected_count
        && shifted_result == 0
        && baseline_result == 0
        && unsafe { valid_range(output_pointer, segment_bytes, false) };
    if valid {
        let baseline = if segment_bytes == 0 {
            Vec::new()
        } else {
            unsafe { slice::from_raw_parts(output_pointer, segment_bytes) }.to_vec()
        };
        let shifted = shifted_output.as_slice().to_vec();
        let compatible = segments_compatible(&baseline, &shifted, pending.expected_count);
        with_scope_mut(|scope| {
            scope.busy = false;
            if compatible {
                scope.phase = Phase::Segments(Segments {
                    baseline,
                    shifted,
                    count: pending.expected_count,
                });
            } else {
                scope.fail(-25);
            }
        });
    } else {
        with_scope_mut(|scope| {
            scope.busy = false;
            scope.fail(-26);
        });
    }
    drop(shifted_output);
    drop(pending);
    baseline_result
}

fn build_shifted_segments(
    input: *const u8,
    count: usize,
    segments: &Segments,
) -> Option<AlignedBuffer> {
    let bytes = checked_bytes(count, SEGMENT_RECORD_SIZE, SCRATCH_BUDGET)?;
    if !unsafe { valid_range(input, bytes, false) } {
        return None;
    }
    let source = if bytes == 0 {
        &[]
    } else {
        unsafe { slice::from_raw_parts(input, bytes) }
    };
    let mut result = AlignedBuffer::from_slice(source)?;
    let mut last = None;
    for input_index in 0..count {
        let record =
            &source[input_index * SEGMENT_RECORD_SIZE..(input_index + 1) * SEGMENT_RECORD_SIZE];
        let start = last.map_or(0, |value| value + 1);
        let matches: Vec<_> = (start..segments.count)
            .filter(|index| {
                let begin = index * SEGMENT_RECORD_SIZE;
                &segments.baseline[begin..begin + SEGMENT_RECORD_SIZE] == record
            })
            .collect();
        if matches.len() != 1 {
            return None;
        }
        let segment_index = matches[0];
        last = Some(segment_index);
        let source_pitch = segment_index * SEGMENT_RECORD_SIZE + 0x10;
        let target_pitch = input_index * SEGMENT_RECORD_SIZE + 0x10;
        unsafe {
            ptr::copy_nonoverlapping(
                segments.shifted.as_ptr().add(source_pitch),
                result.as_mut_ptr().add(target_pitch),
                4,
            );
        }
    }
    Some(result)
}

unsafe fn copy_baseline_to_actual(window: &CurrentWindow) -> bool {
    if window.feature_bytes == 0 {
        return true;
    }
    if !unsafe { valid_range(window.actual_features, window.feature_bytes, true) } {
        return false;
    }
    unsafe {
        ptr::copy_nonoverlapping(
            window.baseline_features.as_ptr(),
            window.actual_features,
            window.feature_bytes,
        )
    };
    true
}

unsafe extern "system-unwind" fn func417_hook(
    state: *mut c_void,
    input: *const u8,
    output: *mut u8,
) -> u64 {
    let Some(originals) = ORIGINALS.get().copied() else {
        return 0;
    };
    let original: Func417 = unsafe { std::mem::transmute(originals.func417) };
    let Some(input_desc) = (unsafe { descriptor(input, ARRAY_DESCRIPTOR_SIZE) }) else {
        return unsafe { original(state, input, output) };
    };
    let Some(output_desc) = (unsafe { descriptor(output, ARRAY_DESCRIPTOR_SIZE) }) else {
        return unsafe { original(state, input, output) };
    };
    let input_count = read_i32(&input_desc, 8);
    let capacity = read_i32(&output_desc, 8);
    let stride = read_i32(&output_desc, 12);
    let input_pointer =
        usize::from_ne_bytes(input_desc[..8].try_into().unwrap_or_default()) as *const u8;
    let output_pointer =
        usize::from_ne_bytes(output_desc[..8].try_into().unwrap_or_default()) as *mut u8;
    if input_count < 0
        || capacity < 0
        || stride <= 0
        || input_count as usize > MAX_RECORDS
        || stride as usize > MAX_FEATURE_STRIDE
    {
        with_scope_mut(|scope| scope.fail(-27));
        return unsafe { original(state, input, output) };
    }
    let feature_bytes = match (capacity as usize)
        .checked_mul(stride as usize)
        .and_then(|value| value.checked_mul(4))
        .filter(|value| *value <= SCRATCH_BUDGET)
    {
        Some(value) => value,
        None => {
            with_scope_mut(|scope| scope.fail(-28));
            return unsafe { original(state, input, output) };
        }
    };
    let shifted_input = with_scope_mut(|scope| {
        scope.func417_calls = scope.func417_calls.saturating_add(1);
        scope.window = None;
        if scope.busy {
            return None;
        }
        let Phase::Segments(segments) = &scope.phase else {
            return None;
        };
        let resident = segments
            .baseline
            .len()
            .checked_add(segments.shifted.len())
            .and_then(|value| value.checked_add(input_count as usize * SEGMENT_RECORD_SIZE));
        if resident
            .and_then(|value| value.checked_add(feature_bytes))
            .is_none_or(|value| value > SCRATCH_BUDGET)
        {
            scope.fail(-28);
            return None;
        }
        let value = build_shifted_segments(input_pointer, input_count as usize, segments);
        if value.is_none() {
            scope.fail(-29);
            return None;
        }
        scope.busy = true;
        value
    })
    .flatten();
    let Some(shifted_input) = shifted_input else {
        return unsafe { original(state, input, output) };
    };
    let Some(mut baseline_features) = AlignedBuffer::zeroed(feature_bytes) else {
        with_scope_mut(|scope| {
            scope.busy = false;
            scope.fail(-30);
        });
        return unsafe { original(state, input, output) };
    };
    let mut baseline_output_desc = output_desc.clone();
    write_pointer(&mut baseline_output_desc, baseline_features.as_ptr());
    let baseline_result = unsafe { original(state, input, baseline_output_desc.as_mut_ptr()) };
    let baseline_rows = read_i32(&baseline_output_desc, 8);
    if baseline_rows < 0 || baseline_rows > capacity {
        with_scope_mut(|scope| {
            scope.busy = false;
            scope.fail(-31);
        });
        return unsafe { original(state, input, output) };
    }
    let mut shifted_input_desc = input_desc.clone();
    write_pointer(&mut shifted_input_desc, shifted_input.as_ptr());
    unsafe { ptr::write_unaligned(output.add(8).cast::<i32>(), capacity) };
    let shifted_result = unsafe { original(state, shifted_input_desc.as_ptr(), output) };
    let shifted_rows = unsafe { ptr::read_unaligned(output.add(8).cast::<i32>()) };
    let exact = shifted_rows == baseline_rows
        && shifted_rows >= 0
        && baseline_result == 0
        && shifted_result == 0
        && unsafe {
            valid_range(
                output_pointer,
                shifted_rows as usize * stride as usize * 4,
                true,
            )
        };
    let rows = baseline_rows.max(0) as usize;
    let used_bytes = rows * stride as usize * 4;
    if !exact {
        let fallback_window = CurrentWindow {
            baseline_features,
            actual_features: output_pointer,
            feature_bytes: used_bytes,
            stride: stride as usize,
            rows,
            cursor: 0,
            shadow_state: ptr::null_mut(),
            actual_state: ptr::null_mut(),
            exact: false,
        };
        unsafe { copy_baseline_to_actual(&fallback_window) };
        unsafe { ptr::write_unaligned(output.add(8).cast::<i32>(), baseline_rows) };
        with_scope_mut(|scope| {
            scope.busy = false;
            scope.fallback = true;
            scope.validation_failures = scope.validation_failures.saturating_add(1);
            scope.error = -32;
            scope.emitted_rows = scope.emitted_rows.saturating_add(rows as u64);
            scope.window = Some(fallback_window);
        });
        return shifted_result;
    }
    baseline_features.length = used_bytes;
    with_scope_mut(|scope| {
        scope.busy = false;
        scope.func417_calls = scope.func417_calls.saturating_add(1);
        scope.emitted_rows = scope.emitted_rows.saturating_add(rows as u64);
        scope.compensation_mode = COMPENSATION_EXACT_BASE_DELTA;
        scope.window = Some(CurrentWindow {
            baseline_features,
            actual_features: output_pointer,
            feature_bytes: used_bytes,
            stride: stride as usize,
            rows,
            cursor: 0,
            shadow_state: ptr::null_mut(),
            actual_state: ptr::null_mut(),
            exact: true,
        });
    });
    shifted_result
}

unsafe fn table_func52_unchanged(scope: &Scope, originals: Originals) -> bool {
    scope.table != 0
        && unsafe { valid_range((scope.table + 0x68) as *const u8, 8, false) }
        && unsafe { ptr::read_unaligned((scope.table + 0x68) as *const usize) } == originals.func52
}

unsafe extern "system-unwind" fn func236_hook(
    state: *mut c_void,
    descriptor_pointer: *const u8,
) -> *mut c_void {
    let Some(originals) = ORIGINALS.get().copied() else {
        return ptr::null_mut();
    };
    let original: Func236 = unsafe { std::mem::transmute(originals.func236) };
    let release: Func950 = unsafe { std::mem::transmute(originals.func950) };
    let check: Func52 = unsafe { std::mem::transmute(originals.func52) };
    let Some(desc) = (unsafe { descriptor(descriptor_pointer, FEATURE_DESCRIPTOR_SIZE) }) else {
        return unsafe { original(state, descriptor_pointer) };
    };
    let prepared = with_scope_mut(|scope| {
        let window = scope.window.as_ref()?;
        if scope.busy || !window.exact || window.rows == 0 {
            return None;
        }
        if !unsafe { table_func52_unchanged(scope, originals) } {
            let restored = scope
                .window
                .as_mut()
                .is_some_and(|window| unsafe { copy_baseline_to_actual(window) });
            scope.fallback = true;
            scope.validation_failures = scope.validation_failures.saturating_add(1);
            scope.error = -36;
            if let Some(window) = scope.window.as_mut() {
                window.exact = false;
            }
            return Some((ptr::null(), restored));
        }
        let baseline = window.baseline_features.as_ptr();
        scope.busy = true;
        Some((baseline, false))
    })
    .flatten();
    let Some((baseline_pointer, forced_fallback)) = prepared else {
        return unsafe { original(state, descriptor_pointer) };
    };
    if forced_fallback || baseline_pointer.is_null() {
        return unsafe { original(state, descriptor_pointer) };
    }
    let mut baseline_desc = desc.clone();
    write_pointer(&mut baseline_desc, baseline_pointer);
    let shadow = unsafe { original(state, baseline_desc.as_ptr()) };
    let shadow_valid = !shadow.is_null() && unsafe { check(shadow) } == 0;
    if !shadow_valid {
        if !shadow.is_null() {
            unsafe { release(shadow) };
        }
        with_scope_mut(|scope| {
            scope.busy = false;
            scope.fallback = true;
            scope.validation_failures = scope.validation_failures.saturating_add(1);
            scope.error = -33;
            if let Some(window) = scope.window.as_mut() {
                unsafe { copy_baseline_to_actual(window) };
                window.exact = false;
            }
        });
        return unsafe { original(state, descriptor_pointer) };
    }
    let actual = unsafe { original(state, descriptor_pointer) };
    if actual == shadow {
        unsafe { release(shadow) };
        with_scope_mut(|scope| {
            scope.busy = false;
            scope.fallback = true;
            scope.validation_failures = scope.validation_failures.saturating_add(1);
            scope.error = -34;
            if let Some(window) = scope.window.as_mut() {
                unsafe { copy_baseline_to_actual(window) };
                window.exact = false;
            }
        });
        return unsafe { original(state, descriptor_pointer) };
    }
    let actual_valid = !actual.is_null() && unsafe { check(actual) } == 0;
    if !actual_valid {
        if !actual.is_null() {
            unsafe { release(actual) };
        }
        with_scope_mut(|scope| {
            scope.busy = false;
            scope.fallback = true;
            scope.validation_failures = scope.validation_failures.saturating_add(1);
            scope.error = -35;
            if let Some(window) = scope.window.as_mut() {
                unsafe { copy_baseline_to_actual(window) };
                window.exact = false;
                window.shadow_state = ptr::null_mut();
                window.actual_state = ptr::null_mut();
            }
        });
        return shadow;
    }
    with_scope_mut(|scope| {
        scope.busy = false;
        if let Some(window) = scope.window.as_mut() {
            window.shadow_state = shadow;
            window.actual_state = actual;
        }
    });
    actual
}

unsafe extern "system-unwind" fn func2d_hook(
    state: *mut c_void,
    descriptor_pointer: *const u8,
    output: *mut f32,
) -> u64 {
    let Some(originals) = ORIGINALS.get().copied() else {
        return 0;
    };
    let original: Func2d = unsafe { std::mem::transmute(originals.func2d) };
    let Some(mut desc) = (unsafe { descriptor(descriptor_pointer, FEATURE_DESCRIPTOR_SIZE) })
    else {
        return unsafe { original(state, descriptor_pointer, output) };
    };
    let prepared = with_scope_mut(|scope| {
        scope.func2d_calls = scope.func2d_calls.saturating_add(1);
        scope.consumed_rows = scope.consumed_rows.saturating_add(1);
        let window = scope.window.as_mut()?;
        if scope.busy
            || !window.exact
            || window.actual_state != state
            || window.shadow_state.is_null()
            || window.cursor >= window.rows
        {
            return None;
        }
        let row = unsafe {
            window
                .baseline_features
                .as_ptr()
                .add(window.cursor * window.stride * 4)
        };
        scope.busy = true;
        Some((window.shadow_state, row))
    })
    .flatten();
    let Some((shadow_state, row)) = prepared else {
        return unsafe { original(state, descriptor_pointer, output) };
    };
    write_pointer(&mut desc, row);
    let mut shadow_output = [0.0f32; 2];
    let _shadow_result =
        unsafe { original(shadow_state, desc.as_ptr(), shadow_output.as_mut_ptr()) };
    let actual_result = unsafe { original(state, descriptor_pointer, output) };
    if unsafe { valid_range(output.cast(), 8, true) } {
        let actual_residual = unsafe { ptr::read_unaligned(output.add(1)) };
        let compensated = (shadow_output[0] - shadow_output[1]) + actual_residual;
        unsafe { ptr::write_unaligned(output, compensated) };
    }
    with_scope_mut(|scope| {
        scope.busy = false;
        scope.func2d_calls = scope.func2d_calls.saturating_add(1);
        if let Some(window) = scope.window.as_mut() {
            window.cursor = window.cursor.saturating_add(1);
        }
    });
    actual_result
}

unsafe extern "system-unwind" fn func950_hook(state: *mut c_void) {
    let Some(originals) = ORIGINALS.get().copied() else {
        return;
    };
    let original: Func950 = unsafe { std::mem::transmute(originals.func950) };
    let paired_shadow = with_scope_mut(|scope| {
        let window = scope.window.as_mut()?;
        if window.actual_state != state || window.shadow_state.is_null() {
            return None;
        }
        let shadow = std::mem::replace(&mut window.shadow_state, ptr::null_mut());
        window.actual_state = ptr::null_mut();
        Some(shadow)
    })
    .flatten();
    if let Some(shadow) = paired_shadow {
        unsafe { original(shadow) };
    }
    unsafe { original(state) };
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn aligned_buffer_is_zeroed_and_32_byte_aligned() {
        let buffer = AlignedBuffer::zeroed(257).unwrap();
        assert_eq!(buffer.as_ptr() as usize & 31, 0);
        assert!(buffer.as_slice().iter().all(|value| *value == 0));
        let empty = AlignedBuffer::zeroed(0).unwrap();
        assert!(empty.as_slice().is_empty());
    }

    #[test]
    fn scratch_size_checks_overflow_and_budget() {
        assert_eq!(checked_bytes(4, 8, 32), Some(32));
        assert_eq!(checked_bytes(5, 8, 32), None);
        assert_eq!(checked_bytes(usize::MAX, 2, usize::MAX), None);
    }

    #[test]
    fn managed_and_native_times_use_a_strict_small_tolerance() {
        assert!(same_time(10.0, 10.0 + 5e-8));
        assert!(!same_time(10.0, 10.0 + 2e-7));
    }

    #[test]
    fn register_note_layout_and_ai_pitch_rewrite_are_stable() {
        assert_eq!(std::mem::size_of::<RegisterNote>(), 48);
        assert_eq!(std::mem::offset_of!(RegisterNote, begin_seconds), 0x20);
        assert_eq!(std::mem::offset_of!(RegisterNote, end_seconds), 0x28);

        let mut record = [0u8; INPUT_RECORD_SIZE];
        record[0..8].copy_from_slice(&0.5f64.to_ne_bytes());
        record[8..16].copy_from_slice(&1.0f64.to_ne_bytes());
        record[0x10..0x14].copy_from_slice(&0.0f32.to_ne_bytes());
        let mut desc = [0u8; INPUT_DESCRIPTOR_SIZE];
        write_pointer(&mut desc, record.as_ptr());
        write_i32(&mut desc, 8, 1);
        let notes = [RegisterNote {
            begin_frame: 0,
            end_frame: 1,
            pitch_cents: 0,
            semitones: 12,
            ordinal: 0,
            reserved: 0,
            begin_seconds: 0.5,
            end_seconds: 1.0,
        }];
        let (_, shifted, count) = build_shifted_input(record.as_ptr(), &desc, &notes).unwrap();
        assert_eq!(count, 1);
        assert_eq!(
            f32::from_ne_bytes(shifted.as_slice()[0x10..0x14].try_into().unwrap()),
            1200.0
        );
    }
}
