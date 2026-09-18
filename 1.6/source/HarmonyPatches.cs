using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Caravan_Sustenance
{
    [HarmonyPatch(typeof(Caravan), "TickInterval")]
    internal static class Patch_Caravan_TickInterval
    {
        private static void Postfix(Caravan __instance, int delta)
        {
            CaravanHuntingUtility.ProcessTickInterval(__instance, delta);
        }
    }

    [HarmonyPatch(typeof(Caravan), nameof(Caravan.GetGizmos))]
    internal static class Patch_Caravan_GetGizmos
    {
        private static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Caravan __instance)
        {
            foreach (Gizmo gizmo in __result)
            {
                yield return gizmo;
            }
            if (__instance.IsPlayerControlled)
            {
                Gizmo huntGizmo = CaravanHuntingUtility.GetGizmo(__instance);
                if (huntGizmo != null)
                {
                    yield return huntGizmo;
                }
            }
        }
    }

    [HarmonyPatch(typeof(Caravan), nameof(Caravan.PostRemove))]
    internal static class Patch_Caravan_PostRemove
    {
        private static void Postfix(Caravan __instance)
        {
            CaravanHuntingUtility.Manager?.RemoveState(__instance.ID);
        }
    }

    [HarmonyPatch(typeof(CaravanTicksPerMoveUtility), nameof(CaravanTicksPerMoveUtility.GetTicksPerMove), new[] { typeof(Caravan), typeof(StringBuilder) })]
    internal static class Patch_CaravanTicksPerMoveUtility_GetTicksPerMove
    {
        private static void Postfix(Caravan caravan, StringBuilder explanation, ref int __result)
        {
            if (caravan == null)
            {
                return;
            }
            HuntingStatus status = CaravanHuntingUtility.GetStatus(caravan);
            if (!status.CanActivelyHunt)
            {
                return;
            }
            float penalty = Mathf.Clamp(Mod.Settings.huntingSpeedPenalty, 0f, 0.95f);
            if (penalty <= 0f)
            {
                return;
            }
            int newResult = Mathf.CeilToInt(__result / (1f - penalty));
            if (explanation != null)
            {
                explanation.AppendLine();
                explanation.Append("CaravanSustenance.Tooltip.SpeedPenaltyExplanation".Translate(penalty.ToStringPercent()));
                float tilesPerDay = 60000f / newResult;
                explanation.AppendLine();
                explanation.Append("CaravanSustenance.Tooltip.FinalSpeed".Translate(tilesPerDay.ToString("0.#")));
            }
            __result = newResult;
        }
    }
}
