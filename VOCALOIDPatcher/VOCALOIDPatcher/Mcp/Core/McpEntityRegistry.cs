using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using VOCALOIDPatcher.McpBridge;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Mcp.Core;

internal static class McpEntityRegistry
{
    private sealed class Identity
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
    }

    private static readonly ConditionalWeakTable<object, Identity> Identities = new();
    private static readonly Dictionary<(string Kind, IntPtr Handle), string> NativeIdentities = new();
    private static string _projectId = string.Empty;

    public static EntityRef Reference(
        string projectId,
        long revision,
        string kind,
        object entity,
        int trackIndex = -1,
        int partIndex = -1,
        int itemIndex = -1,
        string? clientTag = null)
    {
        EnsureProject(projectId);
        string id;
        IntPtr handle = NativeHandle(entity);
        if (handle != IntPtr.Zero)
        {
            lock (NativeIdentities)
            {
                if (!NativeIdentities.TryGetValue((kind, handle), out id!))
                    NativeIdentities[(kind, handle)] = id = Guid.NewGuid().ToString("N");
            }
        }
        else
        {
            id = Identities.GetOrCreateValue(entity).Id;
        }
        return new EntityRef(projectId, revision, kind, trackIndex, partIndex, itemIndex, id, clientTag);
    }

    public static (string Kind, int TrackIndex, int PartIndex, int ItemIndex, object Entity)? Resolve(
        WIVSMSequence sequence,
        string projectId,
        string entityId)
    {
        EnsureProject(projectId);
        for (int trackIndex = 0; trackIndex < sequence.Tracks.Count; trackIndex++)
        {
            WIVSMTrack track = sequence.Tracks[trackIndex];
            if (Matches(track, entityId))
                return ("track", trackIndex, -1, -1, track);
            for (int partIndex = 0; partIndex < track.Parts.Count; partIndex++)
            {
                WIVSMPart part = track.Parts[partIndex];
                if (Matches(part, entityId))
                    return ("part", trackIndex, partIndex, -1, part);
                if (part is not WIVSMMidiPart midi)
                    continue;
                IReadOnlyList<WIVSMNote> notes = midi.Notes;
                for (int noteIndex = 0; noteIndex < notes.Count; noteIndex++)
                {
                    WIVSMNote note = notes[noteIndex];
                    if (Matches(note, entityId))
                        return ("note", trackIndex, partIndex, noteIndex, note);
                }
            }
        }
        return null;
    }

    public static void ProjectReplaced(string projectId)
    {
        _projectId = projectId;
        lock (NativeIdentities)
            NativeIdentities.Clear();
        // ConditionalWeakTable entries disappear with the old Yamaha wrappers. Project ID validation
        // prevents an ID from an older document from resolving even if a native address is reused.
    }

    private static bool Matches(object entity, string entityId)
    {
        IntPtr handle = NativeHandle(entity);
        if (handle != IntPtr.Zero)
        {
            lock (NativeIdentities)
                return NativeIdentities.Any(item => item.Key.Handle == handle && string.Equals(item.Value, entityId, StringComparison.Ordinal));
        }
        return Identities.TryGetValue(entity, out Identity? identity)
               && string.Equals(identity.Id, entityId, StringComparison.Ordinal);
    }

    private static IntPtr NativeHandle(object entity) => entity switch
    {
        WIVSMNote note => (IntPtr)note,
        WIVSMPart part => (IntPtr)part,
        WIVSMTrack track => (IntPtr)track,
        _ => IntPtr.Zero,
    };

    private static void EnsureProject(string projectId)
    {
        if (!string.Equals(_projectId, projectId, StringComparison.Ordinal))
            _projectId = projectId;
    }
}
