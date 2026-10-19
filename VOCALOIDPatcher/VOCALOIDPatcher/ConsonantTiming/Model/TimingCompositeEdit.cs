using System;
using System.Collections.Generic;
using VOCALOIDPatcher.BreathVolume;

namespace VOCALOIDPatcher.ConsonantTiming;

internal sealed class TimingCompositeEdit : ICustomParameterHistoryEdit
{
    private readonly IReadOnlyList<ICustomParameterHistoryEdit> _edits;
    private readonly Action<Action> _batch;
    private TimingCompositeEdit(IReadOnlyList<ICustomParameterHistoryEdit> edits, Action<Action> batch)
    { _edits = edits; _batch = batch; }

    internal static void Execute(Action<Action<ICustomParameterHistoryEdit>> update,
        Action<ICustomParameterHistoryEdit> push, Action<Action> batch)
        => batch(() =>
        {
            var edits = new List<ICustomParameterHistoryEdit>();
            try
            {
                update(edits.Add);
                if (edits.Count != 0) push(new TimingCompositeEdit(edits.ToArray(), batch));
            }
            catch
            {
                for (int i = edits.Count - 1; i >= 0; i--) edits[i].ApplyBefore();
                foreach (var edit in edits) edit.AfterApply();
                throw;
            }
        });

    public void ApplyBefore() { for (int i = _edits.Count - 1; i >= 0; i--) _edits[i].ApplyBefore(); }
    public void ApplyAfter() { foreach (var edit in _edits) edit.ApplyAfter(); }
    public void AfterApply() => _batch(() => { foreach (var edit in _edits) edit.AfterApply(); });
}
