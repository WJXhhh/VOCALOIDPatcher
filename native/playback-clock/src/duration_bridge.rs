//! Stack-owned Win64 observation frames. No mutable code-page context slots.
//! Internal DSE LTCG calls require preserving volatile SIMD/GP state too.
use super::*;
use std::cell::RefCell;

pub(super) struct Call {
    pub args: [usize; 14],
    pub caller: usize,
    pub result: usize,
    pub token: usize,
}
thread_local! { static CALLS: RefCell<Vec<(u32, Call)>> = const { RefCell::new(Vec::new()) }; }
static AFTER: [AtomicU64; 5] = [const { AtomicU64::new(0) }; 5];
static TRAMPOLINES: [AtomicPtr<c_void>; 5] = [const { AtomicPtr::new(ptr::null_mut()) }; 5];

// The return bridge temporarily replaces the native return address on the
// stack. Consumers which distinguish native callsites must use the saved
// caller, rather than attempting to unwind through a generated bridge page.
pub(super) fn original_caller(id: u32) -> Option<usize> {
    CALLS.with(|calls| {
        calls
            .borrow()
            .iter()
            .rev()
            .find(|(hook, _)| *hook == id)
            .map(|(_, call)| call.caller)
    })
}

// Saved frame: XMM0..15 at 0x20, MXCSR at 0x120; pushed R11..RAX and FLAGS.
const SAVED: usize = 0x138;
const REG_ARGS: [usize; 4] = [SAVED + 40, SAVED + 32, SAVED + 24, SAVED + 16];
const RETURN: usize = SAVED + 64;
unsafe extern "system" fn enter(id: u32, frame: *mut u8) {
    let mut call = Call {
        args: [0; 14],
        caller: unsafe { frame.add(RETURN).cast::<usize>().read_unaligned() },
        result: 0,
        token: 0,
    };
    for (i, arg) in call.args.iter_mut().enumerate() {
        let at = if i < 4 {
            REG_ARGS[i]
        } else {
            RETURN + 8 * (i + 1)
        };
        *arg = unsafe { frame.add(at).cast::<usize>().read_unaligned() };
    }
    // Release the TLS borrow before callbacks: original allocation may reenter.
    unsafe {
        super::duration_selection::before(id, &mut call);
    }
    for (i, at) in REG_ARGS.into_iter().enumerate() {
        unsafe {
            frame.add(at).cast::<usize>().write_unaligned(call.args[i]);
        }
    }
    unsafe {
        frame
            .add(RETURN)
            .cast::<usize>()
            .write_unaligned(AFTER[id as usize].load(Ordering::Acquire) as usize);
    }
    CALLS.with(|v| v.borrow_mut().push((id, call)));
}
unsafe extern "system" fn leave(id: u32, frame: *mut u8) {
    let (_, mut call) = CALLS
        .with(|v| v.borrow_mut().pop())
        .expect("balanced native observation");
    call.result = unsafe { frame.add(SAVED + 48).cast::<usize>().read_unaligned() };
    unsafe {
        super::duration_selection::after(id, &mut call);
    }
    unsafe {
        frame
            .add(SAVED + 48)
            .cast::<usize>()
            .write_unaligned(call.result);
        frame
            .add(RETURN)
            .cast::<usize>()
            .write_unaligned(call.caller);
    }
}

#[link(name = "kernel32")]
unsafe extern "system" {
    fn VirtualAlloc(address: *mut c_void, size: usize, kind: u32, protection: u32) -> *mut c_void;
    fn VirtualProtect(
        address: *mut c_void,
        size: usize,
        protection: u32,
        previous: *mut u32,
    ) -> i32;
    fn GetCurrentProcess() -> *mut c_void;
    fn FlushInstructionCache(process: *mut c_void, address: *const c_void, size: usize) -> i32;
}
struct Code(Vec<u8>);
impl Code {
    fn bytes(&mut self, bytes: &[u8]) {
        self.0.extend_from_slice(bytes);
    }
    fn d(&mut self, value: u32) {
        self.bytes(&value.to_le_bytes());
    }
    fn q(&mut self, value: usize) {
        self.bytes(&value.to_le_bytes());
    }
    fn callback(&mut self, id: u32, callback: usize) {
        self.bytes(&[
            0x9c, 0x50, 0x51, 0x52, 0x41, 0x50, 0x41, 0x51, 0x41, 0x52, 0x41, 0x53,
        ]);
        self.bytes(&[0x48, 0x81, 0xec]);
        self.d(SAVED as u32);
        for reg in 0..16 {
            self.simd(reg, false);
        }
        self.bytes(&[0x0f, 0xae, 0x9c, 0x24]);
        self.d(0x120);
        self.bytes(&[0xb9]);
        self.d(id); // ECX = hook ID; RDX = saved frame
        self.bytes(&[0x48, 0x89, 0xe2, 0x48, 0xb8]);
        self.q(callback);
        self.bytes(&[0xff, 0xd0, 0x0f, 0xae, 0x94, 0x24]);
        self.d(0x120);
        for reg in 0..16 {
            self.simd(reg, true);
        }
        self.bytes(&[0x48, 0x81, 0xc4]);
        self.d(SAVED as u32);
        self.bytes(&[
            0x41, 0x5b, 0x41, 0x5a, 0x41, 0x59, 0x41, 0x58, 0x5a, 0x59, 0x58, 0x9d,
        ]);
    }
    fn simd(&mut self, reg: u8, load: bool) {
        self.bytes(&[0xf3]);
        if reg >= 8 {
            self.bytes(&[0x44]);
        }
        self.bytes(&[
            0x0f,
            if load { 0x6f } else { 0x7f },
            0x84 + ((reg & 7) << 3),
            0x24,
        ]);
        self.d(0x20 + 16 * u32::from(reg));
    }
    fn jump(&mut self, address: usize) {
        self.bytes(&[0xff, 0x25, 0, 0, 0, 0]);
        self.q(address);
    }
}

pub(super) unsafe fn install(module: *mut u8) -> Result<(), i32> {
    let prefixes: &[(usize, &[u8])] = &[
        (
            super::timing_profile::DseDurationScore,
            &[
                0x48, 0x8b, 0xc4, 0x4c, 0x89, 0x48, 0x20, 0x44, 0x89, 0x40, 0x18, 0x48, 0x89, 0x50,
                0x10,
            ],
        ),
        (
            super::timing_profile::DseDurationNote,
            &[
                0x48, 0x89, 0x5c, 0x24, 0x18, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56,
                0x41, 0x57,
            ],
        ),
        (
            super::timing_profile::DseDurationWork,
            &[
                0x48, 0x83, 0xec, 0x48, 0x85, 0xd2, 0x0f, 0x8e, 0x7b, 0x01, 0, 0, 0x48, 0x8b, 0x09,
            ],
        ),
        (
            super::timing_profile::DseDurationLookup,
            &[
                0x48, 0x89, 0x5c, 0x24, 0x08, 0x48, 0x89, 0x74, 0x24, 0x10, 0x48, 0x89, 0x7c, 0x24,
                0x18,
            ],
        ),
        (
            super::timing_profile::DseDurationSelect,
            &[
                0x48, 0x8b, 0xc4, 0x44, 0x89, 0x48, 0x20, 0x44, 0x89, 0x40, 0x18, 0x48, 0x89, 0x50,
                0x10,
            ],
        ),
    ];
    for (id, (rva, prefix)) in prefixes.iter().enumerate() {
        if TRAMPOLINES[id].load(Ordering::Acquire).is_null()
            && unsafe { slice::from_raw_parts(module.add(*rva), prefix.len()) } != *prefix
        {
            return Err(-3);
        }
    }
    for (id, (rva, prefix)) in prefixes.iter().enumerate() {
        if !TRAMPOLINES[id].load(Ordering::Acquire).is_null() {
            continue;
        }
        // 3 pages: entry, return bridge, explicitly relocated trampoline.
        let memory = unsafe { VirtualAlloc(ptr::null_mut(), 0x3000, 0x3000, 0x40) }.cast::<u8>();
        if memory.is_null() {
            return Err(-4);
        }
        let mut original = Code(Vec::new());
        if id == 2 {
            original.bytes(&prefix[..6]);
            original.bytes(&[0x7f, 14]);
            original.jump(unsafe { module.add(*rva + 0x187) } as usize);
            original.bytes(&prefix[12..]);
        } else {
            original.bytes(prefix);
        }
        original.jump(unsafe { module.add(*rva + prefix.len()) } as usize);
        unsafe {
            ptr::copy_nonoverlapping(original.0.as_ptr(), memory.add(0x2000), original.0.len());
        }
        let mut entry = Code(Vec::new());
        entry.callback(id as u32, enter as *const () as usize);
        entry.jump(unsafe { memory.add(0x2000) } as usize);
        let mut after = Code(Vec::new());
        // Reserve a return slot without altering flags. Both bridges now have
        // native-entry stack alignment; leave writes the actual caller there.
        after.bytes(&[0x48, 0x8d, 0x64, 0x24, 0xf8]);
        after.callback(id as u32, leave as *const () as usize);
        after.bytes(&[0xc3]);
        if entry.0.len() >= 0x1000 || after.0.len() >= 0x1000 {
            return Err(-4);
        }
        unsafe {
            ptr::copy_nonoverlapping(entry.0.as_ptr(), memory, entry.0.len());
            ptr::copy_nonoverlapping(after.0.as_ptr(), memory.add(0x1000), after.0.len());
            FlushInstructionCache(GetCurrentProcess(), memory.cast(), 0x3000);
            let mut protection = 0;
            if VirtualProtect(module.add(*rva).cast(), prefix.len(), 0x40, &mut protection) == 0 {
                return Err(-5);
            }
            AFTER[id].store(memory.add(0x1000) as u64, Ordering::Release);
            TRAMPOLINES[id].store(memory.add(0x2000).cast(), Ordering::Release);
            let mut patch = Code(Vec::new());
            patch.jump(memory as usize);
            patch.0.resize(prefix.len(), 0x90);
            ptr::copy_nonoverlapping(patch.0.as_ptr(), module.add(*rva), prefix.len());
            FlushInstructionCache(GetCurrentProcess(), module.add(*rva).cast(), prefix.len());
            let mut ignored = 0;
            VirtualProtect(
                module.add(*rva).cast(),
                prefix.len(),
                protection,
                &mut ignored,
            );
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn frames_leave_original_arguments_and_return_slot_in_place() {
        assert_eq!(RETURN, 0x178);
        assert_eq!(REG_ARGS, [0x160, 0x158, 0x150, 0x148]);
        let mut code = Code(Vec::new());
        code.callback(2, 0x1234);
        assert!(code.0.len() < 0x1000);
        assert_eq!(SAVED % 16, 8);
    }

    #[test]
    fn original_caller_uses_innermost_matching_thread_owned_bridge() {
        fn call(caller: usize) -> Call {
            Call { args: [0; 14], caller, result: 0, token: 0 }
        }
        assert!(original_caller(2).is_none());
        CALLS.with(|calls| calls.borrow_mut().push((2, call(super::timing_profile::DsePaddingCaller))));
        CALLS.with(|calls| calls.borrow_mut().push((3, call(super::timing_profile::DseDurationLookup))));
        assert_eq!(original_caller(2), Some(super::timing_profile::DsePaddingCaller));
        assert!(std::thread::spawn(|| original_caller(2)).join().unwrap().is_none());
        CALLS.with(|calls| calls.borrow_mut().push((2, call(super::timing_profile::DurationCaller4))));
        assert_eq!(original_caller(2), Some(super::timing_profile::DurationCaller4));
        CALLS.with(|calls| { calls.borrow_mut().pop(); });
        assert_eq!(original_caller(2), Some(super::timing_profile::DsePaddingCaller));
        CALLS.with(|calls| { calls.borrow_mut().pop(); calls.borrow_mut().pop(); });
        assert!(original_caller(2).is_none());
    }
}
