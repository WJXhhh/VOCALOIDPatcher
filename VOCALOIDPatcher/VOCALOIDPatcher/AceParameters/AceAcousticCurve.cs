using System;
using System.Collections.Generic;
using System.Linq;

namespace VOCALOIDPatcher.AceParameters;

internal readonly record struct AceAcousticPoint(double Seconds, double Value);

// Immutable worker input. Values are signed deviations from the existing UI
// neutral 50, not native controller values. No VSM object crosses to the worker.
internal sealed class AceAcousticCurve
{
    private readonly AceAcousticPoint[] _points;

    internal AceAcousticCurve(IEnumerable<AceAcousticPoint> points)
    {
        _points = points.ToArray();
        if (_points.Length > 500_000) throw new ArgumentException("Too many acoustic control points.");
        for (int i = 0; i < _points.Length; i++)
            if (!double.IsFinite(_points[i].Seconds) || !double.IsFinite(_points[i].Value) ||
                _points[i].Value is < -1 or > 1 || (i > 0 && _points[i].Seconds <= _points[i - 1].Seconds))
                throw new ArgumentException("Invalid acoustic curve.");
    }

    internal bool IsNeutral => _points.All(p => p.Value == 0);

    internal double At(double seconds)
    {
        if (_points.Length == 0 || seconds < _points[0].Seconds) return 0;
        int low = 0, high = _points.Length - 1;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (_points[mid].Seconds <= seconds) low = mid;
            else high = mid - 1;
        }
        if (low == _points.Length - 1) return _points[low].Value;
        var a = _points[low];
        var b = _points[low + 1];
        return a.Value + (b.Value - a.Value) * ((seconds - a.Seconds) / (b.Seconds - a.Seconds));
    }

    internal static AceAcousticCurve Neutral { get; } = new(Array.Empty<AceAcousticPoint>());
}

internal sealed record AceAcousticControls(
    AceAcousticCurve Energy,
    AceAcousticCurve Air,
    AceAcousticCurve Tension,
    AceAcousticCurve Falsetto)
{
    internal bool IsNeutral => Energy.IsNeutral && Air.IsNeutral && Tension.IsNeutral && Falsetto.IsNeutral;
    internal bool HasSourceChanges => !Air.IsNeutral || !Tension.IsNeutral || !Falsetto.IsNeutral;
}
