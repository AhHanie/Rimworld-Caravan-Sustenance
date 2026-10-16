using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        [HarmonyInfix(typeof(Caravan_ForageTracker), nameof(Caravan_ForageTracker.ForageTrackerTickInterval))]
        private static void Postfix(int delta, Caravan ___caravan)
        {
            CaravanHuntingUtility.ProcessTickInterval(___caravan, delta);
            CaravanFishingUtility.ProcessTickInterval(___caravan, delta);
        }
    }

    [HarmonyPatch]
    internal static class Patch_Caravan_GetGizmos
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.StateMachineMoveNext(AccessTools.Method(typeof(Caravan), nameof(Caravan.GetGizmos)));
        }

        [HarmonyInfix(typeof(Caravan_ForageTracker), nameof(Caravan_ForageTracker.GetGizmos))]
        private static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Caravan ___caravan)
        {
            IEnumerable<Gizmo> result = __result;
            Gizmo huntGizmo = CaravanHuntingUtility.GetGizmo(___caravan);
            if (huntGizmo != null)
            {
                result = result.Append(huntGizmo);
            }
            Gizmo fishGizmo = CaravanFishingUtility.GetGizmo(___caravan);
            if (fishGizmo != null)
            {
                result = result.Append(fishGizmo);
            }
            return result;
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
        [HarmonyInfix(typeof(CaravanTicksPerMoveUtility), nameof(CaravanTicksPerMoveUtility.GetTicksPerMove), typeof(CaravanTicksPerMoveUtility.CaravanInfo), typeof(StringBuilder))]
        private static int Postfix(int __result, StringBuilder explanation, [HarmonyOuter] Caravan caravan)
        {
            HuntingStatus status = CaravanHuntingUtility.GetStatus(caravan);
            if (!status.CanActivelyHunt)
            {
                return __result;
            }
            float penalty = Mathf.Clamp(Mod.Settings.huntingSpeedPenalty, 0f, 0.95f);
            if (penalty <= 0f)
            {
                return __result;
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
            return newResult;
        }
    }
}
