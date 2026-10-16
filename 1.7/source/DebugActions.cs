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
        private const int SimulatedFisherCount = 2;
        // FishingSpeed x FishingYield for a level-10 colonist is about 1.0.
        private const float SimulatedFisherContribution = 1f;
        private const int TeleportSearchDepth = 100;

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

        [DebugOutput("Caravan Sustenance", onlyWhenPlaying: true)]
        public static void FishingTimeByFish()
        {
            float dailyNutrition = CaravanFishingUtility.CalculateDailyNutrition(SimulatedFisherCount * SimulatedFisherContribution);

            List<TableDataGetter<ThingDef>> columns = new List<TableDataGetter<ThingDef>>
            {
                new TableDataGetter<ThingDef>("fish", (ThingDef d) => d.defName),
                new TableDataGetter<ThingDef>("nutrition", (ThingDef d) => d.GetStatValueAbstract(StatDefOf.Nutrition)),
                new TableDataGetter<ThingDef>("mass", (ThingDef d) => d.GetStatValueAbstract(StatDefOf.Mass)),
                new TableDataGetter<ThingDef>("credit per catch", (ThingDef d) => CaravanFishingUtility.CalculateHarvestThreshold(d)),
                new TableDataGetter<ThingDef>("days per catch", (ThingDef d) => CaravanFishingUtility.CalculateHarvestThreshold(d) / dailyNutrition)
            };

            HashSet<ThingDef> fish = new HashSet<ThingDef>();
            foreach (BiomeDef biome in DefDatabase<BiomeDef>.AllDefsListForReading)
            {
                BiomeFishTypes types = biome.fishTypes;
                if (types == null)
                {
                    continue;
                }
                foreach (List<FishChance> list in new[] { types.freshwater_Common, types.freshwater_Uncommon, types.saltwater_Common, types.saltwater_Uncommon })
                {
                    foreach (FishChance chance in list)
                    {
                        if (chance?.fishDef != null)
                        {
                            fish.Add(chance.fishDef);
                        }
                    }
                }
            }
            if (ThingDefOf.Fish_Toxfish != null)
            {
                fish.Add(ThingDefOf.Fish_Toxfish);
            }

            DebugTables.MakeTablesDialog(fish.OrderBy(d => d.GetStatValueAbstract(StatDefOf.Nutrition)).ThenBy(d => d.defName), columns.ToArray());
        }

        [DebugAction("Caravan Sustenance", "Teleport selected caravan to nearest ocean", allowedGameStates = AllowedGameStates.PlayingOnWorld, requiresOdyssey = true)]
        public static void TeleportCaravanToOcean()
        {
            Caravan caravan = Find.WorldSelector.SingleSelectedObject as Caravan;
            if (caravan == null)
            {
                Messages.Message("Select a single caravan first.", MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            WorldGrid grid = Find.WorldGrid;
            HashSet<int> visited = new HashSet<int> { caravan.Tile.tileId };
            List<PlanetTile> frontier = new List<PlanetTile> { caravan.Tile };
            List<PlanetTile> next = new List<PlanetTile>();
            List<PlanetTile> neighbors = new List<PlanetTile>();
            for (int depth = 0; depth < TeleportSearchDepth && frontier.Count > 0; depth++)
            {
                next.Clear();
                foreach (PlanetTile current in frontier)
                {
                    grid.GetTileNeighbors(current, neighbors);
                    foreach (PlanetTile neighbor in neighbors)
                    {
                        if (!visited.Add(neighbor.tileId))
                        {
                            continue;
                        }
                        Tile tile = grid[neighbor];
                        if (tile != null && tile.WaterCovered && tile.PrimaryBiome != null && tile.PrimaryBiome.isWaterBiome)
                        {
                            caravan.pather.StopDead();
                            caravan.Tile = neighbor;
                            Messages.Message($"Teleported caravan to {tile.PrimaryBiome.defName} tile {neighbor.tileId}.", caravan, MessageTypeDefOf.NeutralEvent, historical: false);
                            return;
                        }
                        next.Add(neighbor);
                    }
                }
                List<PlanetTile> swap = frontier;
                frontier = next;
                next = swap;
            }
            Messages.Message("No ocean or lake tile found nearby.", MessageTypeDefOf.RejectInput, historical: false);
        }
    }
}
