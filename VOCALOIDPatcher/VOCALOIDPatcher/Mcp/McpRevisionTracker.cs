using System;
using VOCALOIDPatcher.Mcp.Core;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Mcp;

internal static class McpRevisionTracker
{
    private static readonly object Gate = new();
    private static IntPtr _sequencePointer;
    private static string _projectId = Guid.NewGuid().ToString("N");
    private static long _revision = 1;

    public static (string ProjectId, long Revision) Current()
    {
        lock (Gate)
        {
            IntPtr pointer = CurrentSequencePointer();
            if (pointer != _sequencePointer)
            {
                _sequencePointer = pointer;
                _projectId = Guid.NewGuid().ToString("N");
                _revision = 1;
                McpEntityRegistry.ProjectReplaced(_projectId);
                McpEventHub.Publish("document_replaced", _projectId, _revision);
            }
            return (_projectId, _revision);
        }
    }

    public static long Changed()
    {
        lock (Gate)
        {
            Current();
            long revision = ++_revision;
            McpEventHub.Publish("project_revision_changed", _projectId, revision);
            return revision;
        }
    }

    internal static void ChangedForSource(WIVSMSequence source)
    {
        // Planning creates/commits private sequences. Those commits must never
        // invalidate the real project's optimistic revision.
        if ((nint)source == CurrentSequencePointer()) Changed();
    }

    public static void ProjectReplaced()
    {
        lock (Gate)
        {
            _sequencePointer = CurrentSequencePointer();
            _projectId = Guid.NewGuid().ToString("N");
            _revision = 1;
            McpEntityRegistry.ProjectReplaced(_projectId);
            McpEventHub.Publish("document_replaced", _projectId, _revision);
        }
    }

    private static IntPtr CurrentSequencePointer()
    {
        try
        {
#if PATCHER_ISOLATED_VALIDATION
            if (VocaloidMcpFacade.IsolatedTimingSource is { } isolated) return (nint)isolated;
#endif
            var sequence = App.Shared?.Document?.Sequence?.VSMSequence;
            return sequence == null ? IntPtr.Zero : (IntPtr)sequence;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }
}
