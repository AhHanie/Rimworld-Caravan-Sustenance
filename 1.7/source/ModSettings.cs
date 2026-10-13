using Verse;

namespace Caravan_Sustenance
{
    public class ModSettings : Verse.ModSettings
    {
        public const float DefaultYieldMultiplier = 1f;
        public const float DefaultSpeedPenalty = 0.10f;
        public const float DefaultLargeGameCurveExponent = 0.4f;

        public float huntingYieldMultiplier = DefaultYieldMultiplier;
        public float huntingSpeedPenalty = DefaultSpeedPenalty;
        public float largeGameCurveExponent = DefaultLargeGameCurveExponent;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref huntingYieldMultiplier, "huntingYieldMultiplier", DefaultYieldMultiplier);
            Scribe_Values.Look(ref huntingSpeedPenalty, "huntingSpeedPenalty", DefaultSpeedPenalty);
            Scribe_Values.Look(ref largeGameCurveExponent, "largeGameCurveExponent", DefaultLargeGameCurveExponent);
        }
    }
}
