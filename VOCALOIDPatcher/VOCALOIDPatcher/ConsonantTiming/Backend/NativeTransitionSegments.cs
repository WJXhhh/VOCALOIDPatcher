using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace VOCALOIDPatcher.ConsonantTiming;

internal sealed unsafe partial class NativeConsonantTimingEngine
{
    internal ImmutableArray<TransitionSegment> ReadTransitions(int origin)
    {
        if (!_started || _queryMutex == 0) throw new InvalidOperationException("Transition planner is not locked.");
        return ReadTransitionSchedule(_library, _state, origin, boundedPlanner: true);
    }

    // Caller owns the state mutex or is its native render-thread observer.
    internal static ImmutableArray<TransitionSegment> ReadTransitionSchedule(nint library, nint state, int origin,
        int first = 3087, int last = 7183, bool boundedPlanner = false)
    {
        if (library != LocalTimingNative.Dse || state == 0 || last < first || (long)last - first > 8192)
            throw new ArgumentException("Invalid transition schedule read.");
        var p = LocalTimingNative.Profile;
        nint schedule = Marshal.ReadIntPtr(state, p.StateSchedule);
        if (schedule == 0) throw new NotSupportedException("Missing transition schedule.");
        int end = ((delegate* unmanaged<nint, int>)(library + p.DseScheduleEnd))(schedule);
        int capacity = Marshal.ReadInt32(schedule, p.ScheduleCapacity);
        // Capacity is ring storage, not the absolute end cursor of a long Part.
        if (end < 0 || capacity is < 1 or > 1048576 || (boundedPlanner && end > 7183))
            throw new NotSupportedException("Transition schedule exceeds the bounded planner.");
        var rows = new Dictionary<nint, (TransitionSegment Segment, List<TransitionFrame> Frames)>();
        for (int at = Math.Max(3087, first); at < Math.Min(end, last); at++)
        {
            nint entry = ((delegate* unmanaged<nint, int, nint>)(library + p.DseScheduleRow))(schedule, at);
            if (entry == 0) throw new NotSupportedException("Missing transition frame entry.");
            nint row = Marshal.ReadIntPtr(entry);
            if (row == 0 || Marshal.ReadInt32(row, p.RowDictionary) < 0) continue;
            if (!rows.TryGetValue(row, out var value))
            {
                if (rows.Count >= 2048) throw new NotSupportedException("Too many transition work items.");
                int begin = Marshal.ReadInt32(row, p.RowBegin), finish = Marshal.ReadInt32(row, p.RowEnd);
                int from = Marshal.ReadInt32(row, p.RowFrom), to = Marshal.ReadInt32(row, p.RowTo);
                if (begin > at || finish <= at || from is < 0 or > 255 || to is < -1 or > 255)
                    throw new NotSupportedException("Invalid transition work item.");
                nint dictionary = ((delegate* unmanaged<nint, int, int, nint>)(library + p.DseDictionary))(
                    Marshal.ReadIntPtr(state), Marshal.ReadInt32(row, p.RowDictionary), Marshal.ReadInt32(row, p.RowSinger));
                if (dictionary == 0) throw new NotSupportedException("Missing transition dictionary.");
                nint phones = Marshal.ReadIntPtr(dictionary, p.DictionaryPhones);
                string Phone(int index)
                {
                    nint address = ((delegate* unmanaged<nint, byte, nint>)(library + p.DsePhoneName))(phones, checked((byte)index));
                    if (address == 0) throw new NotSupportedException("Missing transition phoneme name.");
                    int length = 0;
                    while (length < 64 && Marshal.ReadByte(address, length) != 0) length++;
                    if (length is 0 or 64) throw new NotSupportedException("Invalid transition phoneme name.");
                    return Marshal.PtrToStringAnsi(address, length)!;
                }
                nint material = Marshal.ReadIntPtr(row, p.RowMaterial);
                if (material == 0) throw new NotSupportedException("Missing transition source material.");
                nint alignment = Marshal.ReadIntPtr(material, p.MaterialAlignmentBegin);
                long bytes = Marshal.ReadIntPtr(material, p.MaterialAlignmentEnd) - alignment;
                if (bytes is < 0 or > 4096 || bytes % 16 != 0 || (bytes > 0 && alignment == 0))
                    throw new NotSupportedException("Invalid transition material alignment.");
                int stage = Marshal.ReadInt32(row, p.RowStage);
                if (to != -1 && (stage < 0 || stage >= Marshal.ReadInt32(row, p.RowStageLimit) || (stage * 2L + 2) * 16 > bytes))
                    throw new NotSupportedException("Invalid transition material stage.");
                using var metadata = new MemoryStream();
                using (var writer = new BinaryWriter(metadata, System.Text.Encoding.UTF8, true))
                {
                    writer.Write(Marshal.ReadInt32(material, p.MaterialFrames));
                    foreach (int offset in new[] { p.MaterialPitch, p.MaterialPitch2, p.MaterialDynamics, p.MaterialTempo })
                        writer.Write(Marshal.ReadInt32(material, offset));
                    for (int i = 0; i < bytes; i += 4) writer.Write(Marshal.ReadInt32(alignment, i));
                }
                var segment = new TransitionSegment(checked(begin + origin),
                    checked(begin + Marshal.ReadInt32(row, to == -1 ? p.RowSustainSplit : p.RowSplit) + origin),
                    checked(finish + origin), Phone(from), to == -1 ? null : Phone(to), from, to, stage,
                    Marshal.ReadByte(row, p.RowReverse) != 0, Convert.ToHexString(SHA256.HashData(metadata.ToArray())), []);
                value = (segment, new()); rows.Add(row, value);
            }
            value.Frames.Add(new(checked(at + origin), Marshal.ReadInt32(entry, 8), Marshal.ReadByte(entry, 12), Marshal.ReadByte(entry, 13)));
        }
        return rows.Values.OrderBy(r => r.Segment.Begin).Select(r => r.Segment with { Frames = r.Frames.ToImmutableArray() }).ToImmutableArray();
    }
}
