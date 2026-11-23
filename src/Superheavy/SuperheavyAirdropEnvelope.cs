using System;

namespace Japensou.NuclearOption.Superheavy
{
    internal static class SuperheavyAirdropEnvelope
    {
        internal const float MaximumSeconds = 480f;
        // Release eligibility is decided by the trajectory predictor, not a
        // fixed speed/altitude band. Reject corrupt inputs before physics work.
        internal static bool ValidMotion(float x, float y, float z, float delay) =>
            Finite(x) && Finite(y) && Finite(z) && Finite(delay) && delay >= 0f;

        internal static bool AboveSurface(float clearance) => Finite(clearance) && clearance > 0f;
        internal static int ForecastSteps(float clearance) => (int)Math.Min(MaximumSeconds/.06d, Math.Max(1100d,
            Math.Ceiling((2d * Math.Sqrt(2d * Math.Max(0d, clearance) / 9.81d) + 60d) / .06d)));
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
