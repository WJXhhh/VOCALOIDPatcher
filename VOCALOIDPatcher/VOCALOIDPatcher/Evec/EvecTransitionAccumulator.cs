using System;
using System.Collections.Generic;
using System.Linq;

namespace VOCALOIDPatcher.Evec;

internal sealed class EvecTransitionAccumulator<TKey, TValue>
    where TKey : notnull
{
    internal Dictionary<TKey, TValue> Before { get; } = new();
    internal Dictionary<TKey, TValue> After { get; } = new();

    internal void Apply(
        IEnumerable<TValue> before,
        IEnumerable<TValue> after,
        Func<TValue, TKey> keySelector)
    {
        TValue[] beforeValues = before.ToArray();
        bool continuesExistingTransition = beforeValues.Any(value =>
            After.ContainsKey(keySelector(value)));
        if (!continuesExistingTransition)
        {
            foreach (var value in beforeValues)
                Before.TryAdd(keySelector(value), value);
        }

        foreach (var value in beforeValues)
            After.Remove(keySelector(value));
        foreach (var value in after)
            After[keySelector(value)] = value;
    }
}
