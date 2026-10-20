using System;
using System.Numerics;

namespace VOCALOIDPatcher.AceParameters;

// Independent implementation of the differentiated LF pulse, with normalized
// period T0=1. Solve continuity and zero-area constraints, then integrate the
// two elementary functions analytically in the frequency domain. No sampled
// pulse FFT (and hence no folded high harmonics) is used for the source table.
// References and the limits of Rd inference are in docs/ace-acoustic-backend.md.
internal sealed class LfGlottalModel
{
    internal const double MinimumRd = .5;
    internal const double MaximumRd = 2.7;
    private readonly double _te, _ta, _omega, _epsilon, _alpha, _e0;

    internal LfGlottalModel(double rd)
    {
        if (!double.IsFinite(rd)) throw new ArgumentOutOfRangeException(nameof(rd));
        rd = Math.Clamp(rd, MinimumRd, MaximumRd);
        _ta = -.01 + .048 * rd;
        double rk = .224 + .118 * rd;
        double rg = rk * (.5 + 1.2 * rk) / (4 * (.11 * rd - _ta * (.5 + 1.2 * rk)));
        double tp = 1 / (2 * rg);
        _te = tp * (1 + rk);
        _omega = Math.PI / tp;
        double duration = 1 - _te;
        if (duration <= _ta) throw new ArgumentException("Invalid LF timing.");

        double lo = 1e-8, hi = 1 / _ta;
        for (int i = 0; i < 70; i++)
        {
            double mid = (lo + hi) / 2;
            if ((1 - Math.Exp(-mid * duration)) / mid > _ta) lo = mid;
            else hi = mid;
        }
        _epsilon = (lo + hi) / 2;
        double returnArea = -((_ta - duration * Math.Exp(-_epsilon * duration)) / (_epsilon * _ta));
        double OpenArea(double alpha) =>
            -(alpha * Math.Sin(_omega * _te) - _omega * Math.Cos(_omega * _te) +
              _omega * Math.Exp(-alpha * _te)) /
            ((alpha * alpha + _omega * _omega) * Math.Sin(_omega * _te));
        lo = -100; hi = 100;
        for (int i = 0; i < 80; i++)
        {
            double mid = (lo + hi) / 2;
            if (OpenArea(mid) + returnArea > 0) lo = mid;
            else hi = mid;
        }
        _alpha = (lo + hi) / 2;
        _e0 = -Math.Exp(-_alpha * _te) / Math.Sin(_omega * _te);
    }

    internal double Derivative(double phase) => phase <= _te
        ? _e0 * Math.Exp(_alpha * phase) * Math.Sin(_omega * phase)
        : -(Math.Exp(-_epsilon * (phase - _te)) - Math.Exp(-_epsilon * (1 - _te))) / (_epsilon * _ta);

    internal Complex Spectrum(double harmonic)
    {
        if (harmonic == 0) return Complex.Zero;
        double w = 2 * Math.PI * harmonic, d = 1 - _te;
        var first = new Complex(_alpha, _omega - w);
        var second = new Complex(_alpha, -_omega - w);
        Complex open = _e0 / (2 * Complex.ImaginaryOne) *
            ((Complex.Exp(first * _te) - 1) / first - (Complex.Exp(second * _te) - 1) / second);
        var pole = new Complex(_epsilon, w);
        Complex tail = Complex.Exp(new Complex(0, -w * _te)) * (1 - Complex.Exp(-pole * d)) / pole;
        Complex constant = Math.Exp(-_epsilon * d) *
            (Complex.Exp(new Complex(0, -w * _te)) - Complex.Exp(new Complex(0, -w))) / new Complex(0, w);
        return open - (tail - constant) / (_epsilon * _ta);
    }
}
