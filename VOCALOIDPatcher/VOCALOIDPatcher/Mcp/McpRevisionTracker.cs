using System;
using Yamaha.VOCALOID;

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
            }
            return (_projectId, _revision);
        }
    }

    public static long Changed()
    {
        lock (Gate)
        {
            Current();
            return ++_revision;
        }
    }

    public static void ProjectReplaced()
    {
        lock (Gate)
        {
            _sequencePointer = CurrentSequencePointer();
            _projectId = Guid.NewGuid().ToString("N");
            _revision = 1;
        }
    }

    private static IntPtr CurrentSequencePointer()
    {
        try
        {
            var sequence = App.Shared?.Document?.Sequence?.VSMSequence;
            return sequence == null ? IntPtr.Zero : (IntPtr)sequence;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }
}
