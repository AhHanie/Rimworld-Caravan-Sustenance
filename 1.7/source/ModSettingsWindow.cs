using UnityEngine;
using Verse;

namespace Caravan_Sustenance
{
    public static class ModSettingsWindow
    {
        private const float MinYieldMultiplier = 0f;
        private const float MaxYieldMultiplier = 3f;
        private const float MinSpeedPenalty = 0f;
        private const float MaxSpeedPenalty = 0.9f;
        private const float MinLargeGameCurveExponent = 0.1f;
        private const float MaxLargeGameCurveExponent = 1f;

        public static void Draw(Rect inRect, ModSettings settings)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.Label("CaravanSustenance.Settings.YieldMultiplier".Translate(settings.huntingYieldMultiplier.ToStringPercent()));
            float newYield = listing.Slider(settings.huntingYieldMultiplier, MinYieldMultiplier, MaxYieldMultiplier);
            if (!Mathf.Approximately(newYield, settings.huntingYieldMultiplier))
            {
                settings.huntingYieldMultiplier = newYield;
                settings.Write();
            }

            listing.Gap();

            listing.Label("CaravanSustenance.Settings.SpeedPenalty".Translate(settings.huntingSpeedPenalty.ToStringPercent()));
            float newPenalty = listing.Slider(settings.huntingSpeedPenalty, MinSpeedPenalty, MaxSpeedPenalty);
            if (!Mathf.Approximately(newPenalty, settings.huntingSpeedPenalty))
            {
                settings.huntingSpeedPenalty = newPenalty;
                settings.Write();
            }

            listing.Gap();

            listing.Label("CaravanSustenance.Settings.LargeGameCurveExponent".Translate(settings.largeGameCurveExponent.ToString("0.00")));
            float newCurveExponent = listing.Slider(settings.largeGameCurveExponent, MinLargeGameCurveExponent, MaxLargeGameCurveExponent);
            if (!Mathf.Approximately(newCurveExponent, settings.largeGameCurveExponent))
            {
                settings.largeGameCurveExponent = newCurveExponent;
                settings.Write();
            }

            listing.Gap(24f);

            if (listing.ButtonText("CaravanSustenance.Settings.ResetDefaults".Translate()))
            {
                settings.huntingYieldMultiplier = ModSettings.DefaultYieldMultiplier;
                settings.huntingSpeedPenalty = ModSettings.DefaultSpeedPenalty;
                settings.largeGameCurveExponent = ModSettings.DefaultLargeGameCurveExponent;
                settings.Write();
            }

            listing.End();
        }
    }
}
