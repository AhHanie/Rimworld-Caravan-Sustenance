using System.Collections.Generic;
using System.Linq;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Caravan_Sustenance
{
    public static class DebugActions
    {
        private const int SimulatedHunterCount = 2;
        private const int SimulatedHunterSkill = 10;
        private const int SimulatedCookSkill = 10;

        // Temperate Forest's animal density; the same reference point CalculateDailyNutrition
        // normalizes against, so this simulation's "days" line up with what a caravan on an
        // average-density tile would actually experience.
        private const float ReferenceAnimalDensity = 3.7f;

        [DebugOutput("Caravan Sustenance", onlyWhenPlaying: true)]
        public static void HuntingTimeByAnimal()
        {
            Pawn cook = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            try
            {
                cook.skills.GetSkill(SkillDefOf.Cooking).Level = SimulatedCookSkill;

                float sumSkillContribution = SimulatedHunterCount * 0.09f * SimulatedHunterSkill;
                float dailyNutrition = CaravanHuntingUtility.CalculateDailyNutrition(sumSkillContribution, ReferenceAnimalDensity);

                List<TableDataGetter<PawnKindDef>> columns = new List<TableDataGetter<PawnKindDef>>
                {
                    new TableDataGetter<PawnKindDef>("animal", (PawnKindDef k) => k.defName),
                    new TableDataGetter<PawnKindDef>("body size", (PawnKindDef k) => k.RaceProps.baseBodySize),
                    new TableDataGetter<PawnKindDef>("raw meat", (PawnKindDef k) => k.race.GetStatValueAbstract(StatDefOf.MeatAmount)),
                    new TableDataGetter<PawnKindDef>("credit needed", (PawnKindDef k) => CaravanHuntingUtility.CalculateHarvestThreshold(k, cook)),
                    new TableDataGetter<PawnKindDef>("days (moving)", (PawnKindDef k) => CaravanHuntingUtility.CalculateHarvestThreshold(k, cook) / dailyNutrition),
                    new TableDataGetter<PawnKindDef>("days (stationary)", (PawnKindDef k) => CaravanHuntingUtility.CalculateHarvestThreshold(k, cook) / (dailyNutrition * 2f))
                };

                IEnumerable<PawnKindDef> animalKinds = DefDatabase<PawnKindDef>.AllDefs
                    .Where(k => k.RaceProps != null && k.RaceProps.Animal)
                    .OrderBy(k => k.RaceProps.baseBodySize);

                DebugTables.MakeTablesDialog(animalKinds, columns.ToArray());
            }
            finally
            {
                Find.WorldPawns.PassToWorld(cook, PawnDiscardDecideMode.Discard);
            }
        }
    }
}
