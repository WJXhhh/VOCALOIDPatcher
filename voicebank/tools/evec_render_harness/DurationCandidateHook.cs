using System.Runtime.InteropServices;

internal static partial class Program
{
    // Diagnostic-only enlargement of the existing pitch eligibility window.
    // This is a separate experimental variable, never implied by duration alone.
    private sealed unsafe class DurationCandidateHook : IDisposable
    {
        private static DurationCandidateHook? _active;
        private readonly ResearchDurationDetour _filter;
        internal float? PitchWidth { get; set; }
        internal bool CaptureBounds { get; set; }
        internal int Calls { get; private set; }
        internal int MaximumCandidates { get; private set; }
        internal sealed record CandidateBounds(float LowDynamics, float HighDynamics, float DynamicsWidth, int Count);
        internal List<CandidateBounds> Bounds { get; } = new();
        internal DurationCandidateHook()
        {
            if (_active != null) throw new InvalidOperationException("Candidate probe already active.");
            _active = this;
            _filter = new(0x1ab350, [0x40,0x55,0x53,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57,
                0x48,0x8d,0xac,0x24,0x68,0xff,0xff,0xff],
                (nint)(delegate* unmanaged<nint, nint, int, nint, nint, int, float, float, float, float, float, float, byte, byte, byte>)&Filter);
        }
        internal void Reset() { Calls = MaximumCandidates = 0; Bounds.Clear(); }
        [UnmanagedCallersOnly]
        private static byte Filter(nint work, nint previous, int item, nint output, nint counts, int position,
            float low, float high, float lowDynamics, float highDynamics, float pitchWidth, float dynamicsWidth, byte first, byte last)
        {
            var owner = _active!;
            var original = (delegate* unmanaged<nint, nint, int, nint, nint, int, float, float, float, float, float, float, byte, byte, byte>)owner._filter.Trampoline;
            byte ok = original(work, previous, item, output, counts, position, low, high, lowDynamics, highDynamics,
                owner.PitchWidth ?? pitchWidth, dynamicsWidth, first, last);
            owner.Calls++;
            if (ok != 0 && counts != 0) owner.MaximumCandidates = Math.Max(owner.MaximumCandidates, Marshal.ReadInt32(counts, position * 4));
            if (owner.CaptureBounds && owner.Bounds.Count < 4096)
                owner.Bounds.Add(new(lowDynamics, highDynamics, dynamicsWidth, ok != 0 && counts != 0 ? Marshal.ReadInt32(counts, position * 4) : 0));
            return ok;
        }
        public void Dispose() { _filter.Dispose(); _active = null; }
    }
}
