using System.Runtime.InteropServices;
using VOCALOIDPatcher.ConsonantTiming;

internal static partial class Program
{
    // 0x1722C0 keeps one smoothing value in DLL BSS, across engines and renders.
    // Keep that history in this render scope and exchange it only while owning
    // the same recursive native mutex as all sample reads. Probe-process only.
    private sealed unsafe class DurationLayerScope : IDisposable
    {
        private static DurationLayerScope? _active;
        private readonly DurationPostCallHook _hook;
        private int _history, _saved;
        private nint _mutex;
        private Exception? _error;
        internal int Calls { get; private set; }
        internal DurationLayerScope()
        {
            _active = this;
            _hook = new(0x1722c0, [0x48,0x8b,0xc4,0x44,0x89,0x48,0x20,0x44,0x89,0x40,0x18,0x48,0x89,0x50,0x10],
                (nint)(delegate* unmanaged<nint, void>)&After, (nint)(delegate* unmanaged<nint, void>)&Before);
        }
        internal void Reset() { _history = 0; Calls = 0; _error = null; }
        internal void Check()
        {
            if (_error != null || _mutex != 0) throw new InvalidOperationException("Layer history scope failed.", _error);
            if (Calls > 0 && Marshal.ReadInt32(LocalTimingNative.Dse + 0xa20050) != _saved)
                throw new InvalidOperationException("Layer scope modified another session's history.");
        }
        [UnmanagedCallersOnly]
        private static void Before(nint context)
        {
            var owner = _active!;
            try
            {
                if (owner._mutex != 0) throw new InvalidOperationException("Reentrant layer history operation.");
                nint mutex = Marshal.ReadIntPtr(Marshal.ReadIntPtr(context), 0x2aef0);
                if (WaitForSingleObject(mutex, 1000) != 0) throw new InvalidOperationException("Layer history lock unavailable.");
                owner._mutex = mutex;
                owner._saved = Marshal.ReadInt32(LocalTimingNative.Dse + 0xa20050);
                Marshal.WriteInt32(LocalTimingNative.Dse + 0xa20050, owner._history);
            }
            catch (Exception error) { owner._error ??= error; }
        }
        [UnmanagedCallersOnly]
        private static void After(nint context)
        {
            var owner = _active!;
            if (owner._mutex == 0) return;
            owner._history = Marshal.ReadInt32(LocalTimingNative.Dse + 0xa20050);
            Marshal.WriteInt32(LocalTimingNative.Dse + 0xa20050, owner._saved);
            ReleaseMutex(owner._mutex); owner._mutex = 0; owner.Calls++;
        }
        public void Dispose() { _hook.Dispose(); Check(); _active = null; }
        [DllImport("kernel32")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
        [DllImport("kernel32")] private static extern bool ReleaseMutex(nint handle);
    }
}
