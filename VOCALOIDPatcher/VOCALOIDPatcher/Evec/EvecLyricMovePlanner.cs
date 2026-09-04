using System;
using System.Collections.Generic;

namespace VOCALOIDPatcher.Evec;

internal readonly record struct EvecLyricMoveAssignment(
    int TargetIndex,
    int? SourceIndex);

internal static class EvecLyricMovePlanner
{
    internal static IReadOnlyList<EvecLyricMoveAssignment> Build(
        int noteCount,
        int firstSelected,
        int lastSelected,
        bool singleSelection,
        bool moveRight)
    {
        if (noteCount <= 0 || firstSelected < 0 || lastSelected < firstSelected ||
            firstSelected >= noteCount || lastSelected >= noteCount)
        {
            return Array.Empty<EvecLyricMoveAssignment>();
        }

        int firstTarget;
        int lastTarget;
        if (moveRight)
        {
            firstTarget = firstSelected;
            lastTarget = singleSelection
                ? noteCount - 1
                : Math.Min(noteCount - 1, lastSelected + 1);
        }
        else
        {
            firstTarget = Math.Max(0, firstSelected - 1);
            lastTarget = singleSelection ? noteCount - 1 : lastSelected;
        }

        var assignments = new List<EvecLyricMoveAssignment>(lastTarget - firstTarget + 1);
        for (int targetIndex = firstTarget; targetIndex <= lastTarget; targetIndex++)
        {
            bool hasSource = moveRight
                ? targetIndex > firstTarget
                : targetIndex < lastTarget;
            assignments.Add(new EvecLyricMoveAssignment(
                targetIndex,
                hasSource
                    ? moveRight ? targetIndex - 1 : targetIndex + 1
                    : null));
        }

        return assignments;
    }
}
