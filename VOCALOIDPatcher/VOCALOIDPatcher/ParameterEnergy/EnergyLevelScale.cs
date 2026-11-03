using System;

namespace VOCALOIDPatcher.ParameterEnergy;

internal static class EnergyLevelScale
{
    internal static double ToY(double db, double height, double minimum, double maximum)
        => 4 + Math.Max(0, height - 8) * (1 - Math.Clamp((db - minimum) / (maximum - minimum), 0, 1));

    internal static double FromY(double y, double height, double minimum, double maximum)
    {
        if (!double.IsFinite(y) || height <= 8 || maximum <= minimum) return minimum;
        return Math.Clamp(Math.Round(maximum - Math.Clamp((y - 4) / (height - 8), 0, 1) * (maximum - minimum), 1), minimum, maximum);
    }
}
