using System.Runtime.InteropServices;
using VOCALOIDPatcher.ConsonantTiming;

internal static partial class Program
{
    private static unsafe int AdvanceBorrowEiEntry(nint state)
    {
        nint schedule = Marshal.ReadIntPtr(state, LocalTimingNative.Profile.StateSchedule);
        int end = ((delegate* unmanaged<nint, int>)(LocalTimingNative.Dse + LocalTimingNative.Profile.DseScheduleEnd))(schedule);
        int current = Marshal.ReadInt32(state, LocalTimingNative.Profile.StateCurrentFrame), changed = 0;
        for (int at = Math.Max(current, 3343); at < Math.Min(end, 3388); at++)
        {
            nint entry = ((delegate* unmanaged<nint, int, nint>)(LocalTimingNative.Dse + LocalTimingNative.Profile.DseScheduleRow))(schedule, at);
            nint row = Marshal.ReadIntPtr(entry);
            if (row == 0 || Marshal.ReadInt32(row) != 3343 || Marshal.ReadInt32(row, 4) != 3388 ||
                Marshal.ReadInt32(row, 0x10) != 60 || Marshal.ReadInt32(row, 0x14) != 4) continue;
            int bits = BitConverter.SingleToInt32Bits(2 + (at - 3343) * 37f / 44);
            if (Marshal.ReadInt32(entry, 8) == bits) continue;
            Marshal.WriteInt32(entry, 8, bits); changed++;
        }
        return changed;
    }

    private sealed unsafe partial class ExplicitBorrowReader
    {
        internal sealed record FullRead(int Frame, int Source, double Weight, int Bins, ulong Flags, bool Identity, int Batch);
        internal List<FullRead> FullSource { get; } = new();

        private void PoolSource(nint context)
        {
            var donor = _donor!;
            nint state = Marshal.ReadIntPtr(context), analysis = Marshal.ReadIntPtr(context, 24);
            int frame = Marshal.ReadInt32(context, 48), original = Marshal.ReadInt32(context, 16);
            int batch = FullSource.Count + 1;
            var read = (delegate* unmanaged<nint, nint, int, nint, int, int, int, byte, uint, void>)_hook.NativeBody;
            void Load(int source) => read(state, _view, source, analysis, Marshal.ReadInt32(context, 32),
                Marshal.ReadInt32(context, 40), frame, Marshal.ReadByte(context, 56), unchecked((uint)Marshal.ReadInt32(context, 64)));
            if (_mode == "reread")
            {
                Load(original);
                FullSource.Add(new(frame, original, 1, Marshal.ReadInt32(analysis, 0x1a8), unchecked((ulong)Marshal.ReadInt64(analysis, 0x150)), true, batch));
                return;
            }
            // Area integration over two ordered cells, never a sum of gains:
            // every selected-stage s frame contributes with normalized weight.
            int count = donor.Last - donor.First + 1;
            double begin = (frame - 3341) * count / 2d, end = (frame - 3341 + 1) * count / 2d;
            double[]? power = null;
            for (int at = (int)Math.Floor(begin); at < Math.Ceiling(end); at++)
            {
                double weight = Math.Max(0, Math.Min(end, at + 1) - Math.Max(begin, at)) / (end - begin);
                int source = donor.First + at;
                Require(source >= donor.First && source <= donor.Last, "Full-source pooling left donor s.");
                Load(source);
                int bins = Marshal.ReadInt32(analysis, 0x1a8);
                Require(bins is > 0 and <= 4097, "Unbounded full-source spectrum.");
                power ??= new double[bins]; Require(power.Length == bins, "Full-source spectral dimensions changed.");
                float* real = (float*)Marshal.ReadIntPtr(analysis, 0x1b0), imag = (float*)Marshal.ReadIntPtr(analysis, 0x1b8);
                for (int bin = 0; bin < bins; bin++)
                {
                    Require(float.IsFinite(real[bin]) && float.IsFinite(imag[bin]), "Nonfinite donor spectrum.");
                    power[bin] += weight * ((double)real[bin] * real[bin] + (double)imag[bin] * imag[bin]);
                }
                FullSource.Add(new(frame, source, weight, bins, unchecked((ulong)Marshal.ReadInt64(analysis, 0x150)), false, batch));
            }
            // Restore the representative native metadata and phase. Only the
            // nonstationary noise power envelope is integrated; no gain boost.
            Load(original);
            float* outputReal = (float*)Marshal.ReadIntPtr(analysis, 0x1b0), outputImag = (float*)Marshal.ReadIntPtr(analysis, 0x1b8);
            for (int bin = 0; bin < power!.Length; bin++)
            {
                double radius = Math.Sqrt((double)outputReal[bin] * outputReal[bin] + (double)outputImag[bin] * outputImag[bin]);
                double desired = Math.Sqrt(power[bin]);
                if (radius > 1e-20) { outputReal[bin] = (float)(outputReal[bin] * desired / radius); outputImag[bin] = (float)(outputImag[bin] * desired / radius); }
                else { outputReal[bin] = (float)desired; outputImag[bin] = 0; }
            }
        }
    }
}
