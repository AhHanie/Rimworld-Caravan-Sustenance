using System.Collections.Generic;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Caravan_Sustenance
{
    // Weighted common/uncommon fish lists for one stretch of water. Not saved.
    public class FishPool
    {
        public readonly List<ThingDef> commonDefs = new List<ThingDef>();
        public readonly List<float> commonWeights = new List<float>();
        public readonly List<ThingDef> uncommonDefs = new List<ThingDef>();
        public readonly List<float> uncommonWeights = new List<float>();

        public bool IsEmpty => commonDefs.Count == 0 && uncommonDefs.Count == 0;

        public bool Contains(ThingDef def)
        {
            return commonDefs.Contains(def) || uncommonDefs.Contains(def);
        }
    }

    public class FishingStatus
    {
        public bool RequirementsMet;
        public bool CanActivelyFish;
        public string ReasonKey;
        public bool Saltwater;
        public int FisherCount;
        public ThingDef Fish;
        public float DailyNutrition;
        public float HarvestThreshold;
        public float HarvestMass;
    }

    [StaticConstructorOnStartup]
    public static class CaravanFishingUtility
    {
        private const int UpdateIntervalTicks = 15;
        private const int DebugLogThrottleTicks = 300;
        private const int FisherCacheTicks = 250; // ~4 real seconds at normal (1x) game speed.
        private const int FishPerCatch = 4;
        private const int MaxPoolSearchDepth = 30;
        // Nutrition/day of a level-10 hunter on an average tile is ~2.34; a level-10 fisher
        // (FishingSpeed x FishingYield ~ 1.0) is calibrated to land in the same range.
        private const float DailyNutritionPerContribution = 2.4f;
        // Mass assumed for the cargo check before a fish has been rolled (4 of the lightest fish).
        private const float UnrolledCatchMass = FishPerCatch * 0.5f;

        private static readonly Texture2D FishIcon = ModsConfig.OdysseyActive
            ? ContentFinder<Texture2D>.Get("UI/Designators/ZoneCreate_Fishing", reportFailure: false)
            : null;

        private static readonly List<PlanetTile> tmpNeighbors = new List<PlanetTile>();

        // Debug counters, for confirming the caching behaviour.
        public static int FisherScanCount;

        public static CaravanHuntingState GetState(Caravan caravan)
        {
            return CaravanHuntingUtility.GetState(caravan);
        }

        public static float CalculateDailyNutrition(float sumContribution)
        {
            return sumContribution * DailyNutritionPerContribution * Mod.Settings.fishingYieldMultiplier;
        }

        public static float CalculateHarvestThreshold(ThingDef fish)
        {
            return FishPerCatch * fish.GetStatValueAbstract(StatDefOf.Nutrition);
        }

        public static Gizmo GetGizmo(Caravan caravan)
        {
            if (!ModsConfig.OdysseyActive || FishIcon == null || caravan == null || !caravan.IsPlayerControlled)
            {
                return null;
            }
            CaravanHuntingState state = GetState(caravan);
            if (state == null)
            {
                return null;
            }

            Command_Toggle toggle = new Command_Toggle
            {
                defaultLabel = "CaravanSustenance.Fishing.Gizmo.Label".Translate(),
                icon = FishIcon,
                isActive = () => state.fishingEnabledByPlayer,
                toggleAction = delegate
                {
                    state.fishingEnabledByPlayer = !state.fishingEnabledByPlayer;
                    if (state.fishingEnabledByPlayer)
                    {
                        // A fresh toggle shouldn't be stuck with a stale fisher scan.
                        state.cachedFishersTick = -99999;
                    }
                }
            };
            // Like the hunt toggle, this only records intent; the tooltip explains current eligibility.
            toggle.defaultDesc = BuildTooltip(BuildStatus(caravan, state, allowSelection: false));
            return toggle;
        }

        public static void ProcessTickInterval(Caravan caravan, int delta)
        {
            if (!ModsConfig.OdysseyActive || caravan == null || !caravan.IsPlayerControlled)
            {
                return;
            }
            if (!caravan.IsHashIntervalTick(UpdateIntervalTicks, delta))
            {
                return;
            }

            CaravanHuntingState state = GetState(caravan);
            if (state == null || !state.fishingEnabledByPlayer)
            {
                return;
            }

            int ticksNow = Find.TickManager.TicksGame;
            // The extra salt keeps this stream independent of the hunting roll for the same caravan/tick.
            Rand.PushState(Gen.HashCombineInt(Gen.HashCombineInt(caravan.ID, ticksNow), 0x46495348));
            try
            {
                bool shouldLog = ticksNow - state.lastDebugLogTick >= DebugLogThrottleTicks;
                FishingStatus status = BuildStatus(caravan, state, allowSelection: true);
                if (!status.RequirementsMet)
                {
                    if (shouldLog)
                    {
                        Logger.Message($"Caravan {caravan.ID} fishing paused: {status.ReasonKey}");
                        state.lastDebugLogTick = ticksNow;
                    }
                    return;
                }

                // Fixed interval size rather than `delta`, same reasoning as the hunting tick.
                float creditGain = status.DailyNutrition / 60000f * UpdateIntervalTicks;
                state.fishingCredit += creditGain;
                if (shouldLog)
                {
                    Logger.Message($"Caravan {caravan.ID} fishing {status.Fish.defName}: credit {state.fishingCredit:0.####}/{status.HarvestThreshold:0.####} (rate {status.DailyNutrition:0.####}/day).");
                    state.lastDebugLogTick = ticksNow;
                }

                if (status.HarvestThreshold > 0f && state.fishingCredit >= status.HarvestThreshold)
                {
                    if (TryHarvest(caravan, status, state))
                    {
                        state.fishingCredit -= status.HarvestThreshold;
                        Logger.Message($"Caravan {caravan.ID} caught {status.Fish.defName}; credit now {state.fishingCredit:0.###}.");
                    }
                    else
                    {
                        Logger.Message($"Caravan {caravan.ID} deferred catching {status.Fish.defName}: insufficient cargo space.");
                    }
                }
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static FishingStatus BuildStatus(Caravan caravan, CaravanHuntingState state, bool allowSelection)
        {
            FishingStatus status = new FishingStatus();

            if (!caravan.IsPlayerControlled)
            {
                status.ReasonKey = "CaravanSustenance.Reason.NotPlayerControlled".Translate();
                return status;
            }

            if (!TryGetWater(caravan, out bool saltwater))
            {
                status.ReasonKey = "CaravanSustenance.Fishing.Reason.NotOnWater".Translate();
                return status;
            }
            status.Saltwater = saltwater;

            RefreshFishers(caravan, state);
            float contribution = 0f;
            for (int i = 0; i < state.cachedFishers.Count; i++)
            {
                Pawn p = state.cachedFishers[i];
                // The cache can be up to FisherCacheTicks old; a flag check is cheaper than a rescan.
                if (p.Destroyed || p.Dead || p.Downed)
                {
                    continue;
                }
                status.FisherCount++;
                contribution += state.cachedFisherContributions[i];
            }
            if (status.FisherCount == 0)
            {
                status.ReasonKey = "CaravanSustenance.Fishing.Reason.NoFisher".Translate();
                return status;
            }
            status.DailyNutrition = CalculateDailyNutrition(contribution);

            PlanetTile planetTile = caravan.Tile;
            FishPool pool = GetPool(state, planetTile, saltwater);
            ThingDef fish = GetFish(state, pool, planetTile, allowSelection);
            if (fish == null && allowSelection)
            {
                status.ReasonKey = "CaravanSustenance.Fishing.Reason.NoFish".Translate();
                return status;
            }
            status.Fish = fish;
            // Without a rolled fish (tooltip only) the cargo check falls back to a minimum catch.
            status.HarvestMass = fish != null ? FishPerCatch * fish.GetStatValueAbstract(StatDefOf.Mass) : UnrolledCatchMass;
            if (fish != null)
            {
                status.HarvestThreshold = CalculateHarvestThreshold(fish);
            }

            if (caravan.MassCapacity - caravan.MassUsage < status.HarvestMass)
            {
                status.ReasonKey = "CaravanSustenance.Fishing.Reason.NoCargoSpace".Translate();
                return status;
            }

            status.RequirementsMet = true;
            status.CanActivelyFish = state.fishingEnabledByPlayer;
            return status;
        }

        // The tooltip is built every GUI frame without a pending fish yet, so the pre-roll case
        // reports a placeholder and only the tick (allowSelection) ever rolls.
        private static ThingDef GetFish(CaravanHuntingState state, FishPool pool, PlanetTile planetTile, bool allowSelection)
        {
            if (!string.IsNullOrEmpty(state.pendingFishDefName))
            {
                ThingDef pending = DefDatabase<ThingDef>.GetNamedSilentFail(state.pendingFishDefName);
                if (pending != null && (pending == ThingDefOf.Fish_Toxfish || pool.Contains(pending)))
                {
                    return pending;
                }
                state.pendingFishDefName = null;
            }
            if (!allowSelection)
            {
                return null;
            }

            ThingDef rolled = null;
            Tile tile = Find.WorldGrid[planetTile];
            if (ModsConfig.BiotechActive && ThingDefOf.Fish_Toxfish != null && tile != null
                && Rand.Chance(FishingUtility.PollutionToxfishChanceCurve.Evaluate(tile.pollution)))
            {
                rolled = ThingDefOf.Fish_Toxfish;
            }
            else if (!TryRollFromPool(pool, out rolled))
            {
                return null;
            }
            state.pendingFishDefName = rolled.defName;
            Logger.Message($"Selected fish {rolled.defName} on tile {planetTile.tileId}.");
            return rolled;
        }

        private static bool TryRollFromPool(FishPool pool, out ThingDef fish)
        {
            fish = null;
            bool uncommon = pool.uncommonDefs.Count > 0 && Rand.Chance(FishingUtility.ChanceToCatchUncommonFish);
            List<ThingDef> defs = uncommon || pool.commonDefs.Count == 0 ? pool.uncommonDefs : pool.commonDefs;
            List<float> weights = uncommon || pool.commonDefs.Count == 0 ? pool.uncommonWeights : pool.commonWeights;
            if (defs.Count == 0)
            {
                return false;
            }

            float total = 0f;
            for (int i = 0; i < weights.Count; i++)
            {
                total += weights[i];
            }
            if (total <= 0f)
            {
                fish = defs[Rand.Range(0, defs.Count)];
                return true;
            }
            float roll = Rand.Value * total;
            float cumulative = 0f;
            for (int i = 0; i < weights.Count; i++)
            {
                cumulative += weights[i];
                if (roll <= cumulative)
                {
                    fish = defs[i];
                    return true;
                }
            }
            fish = defs[defs.Count - 1];
            return true;
        }

        private static bool TryGetWater(Caravan caravan, out bool saltwater)
        {
            saltwater = true;
            PlanetTile planetTile = caravan.Tile;
            if (!planetTile.Valid || !planetTile.Layer.IsRootSurface)
            {
                return false;
            }
            Tile tile = Find.WorldGrid[planetTile];
            if (tile == null || !tile.WaterCovered)
            {
                return false;
            }
            BiomeDef biome = tile.PrimaryBiome;
            if (biome == null || !biome.isWaterBiome)
            {
                return false;
            }
            saltwater = biome != BiomeDefOf.Lake;
            return true;
        }

        // Ocean and Lake define no fishTypes of their own, so the pool comes from the nearest land biomes.
        private static FishPool GetPool(CaravanHuntingState state, PlanetTile planetTile, bool saltwater)
        {
            if (state.cachedPool != null && state.cachedPoolTile == planetTile.tileId)
            {
                return state.cachedPool;
            }

            HashSet<BiomeDef> landBiomes = FindNearestLandBiomes(planetTile);
            Dictionary<ThingDef, float> common = new Dictionary<ThingDef, float>();
            Dictionary<ThingDef, float> uncommon = new Dictionary<ThingDef, float>();
            foreach (BiomeDef biome in landBiomes)
            {
                AddFishTypes(biome, saltwater, common, uncommon);
            }
            if (common.Count == 0 && uncommon.Count == 0)
            {
                foreach (BiomeDef biome in DefDatabase<BiomeDef>.AllDefsListForReading)
                {
                    AddFishTypes(biome, saltwater, common, uncommon, flatWeight: true);
                }
            }

            FishPool pool = new FishPool();
            foreach (KeyValuePair<ThingDef, float> pair in common)
            {
                pool.commonDefs.Add(pair.Key);
                pool.commonWeights.Add(pair.Value);
            }
            foreach (KeyValuePair<ThingDef, float> pair in uncommon)
            {
                pool.uncommonDefs.Add(pair.Key);
                pool.uncommonWeights.Add(pair.Value);
            }
            state.cachedPool = pool;
            state.cachedPoolTile = planetTile.tileId;
            return pool;
        }

        private static void AddFishTypes(BiomeDef biome, bool saltwater, Dictionary<ThingDef, float> common, Dictionary<ThingDef, float> uncommon, bool flatWeight = false)
        {
            BiomeFishTypes types = biome?.fishTypes;
            if (types == null)
            {
                return;
            }
            AddFishChances(saltwater ? types.saltwater_Common : types.freshwater_Common, common, flatWeight);
            AddFishChances(saltwater ? types.saltwater_Uncommon : types.freshwater_Uncommon, uncommon, flatWeight);
        }

        private static void AddFishChances(List<FishChance> chances, Dictionary<ThingDef, float> into, bool flatWeight)
        {
            if (chances == null)
            {
                return;
            }
            for (int i = 0; i < chances.Count; i++)
            {
                FishChance entry = chances[i];
                if (entry?.fishDef == null)
                {
                    continue;
                }
                into.TryGetValue(entry.fishDef, out float existing);
                into[entry.fishDef] = flatWeight ? 1f : existing + entry.chance;
            }
        }

        private static HashSet<BiomeDef> FindNearestLandBiomes(PlanetTile start)
        {
            HashSet<BiomeDef> found = new HashSet<BiomeDef>();
            HashSet<int> visited = new HashSet<int> { start.tileId };
            List<PlanetTile> frontier = new List<PlanetTile> { start };
            List<PlanetTile> next = new List<PlanetTile>();
            WorldGrid grid = Find.WorldGrid;

            for (int depth = 0; depth < MaxPoolSearchDepth && frontier.Count > 0; depth++)
            {
                next.Clear();
                for (int i = 0; i < frontier.Count; i++)
                {
                    grid.GetTileNeighbors(frontier[i], tmpNeighbors);
                    for (int j = 0; j < tmpNeighbors.Count; j++)
                    {
                        PlanetTile neighbor = tmpNeighbors[j];
                        if (visited.Add(neighbor.tileId))
                        {
                            next.Add(neighbor);
                        }
                    }
                }
                for (int i = 0; i < next.Count; i++)
                {
                    Tile tile = grid[next[i]];
                    if (tile != null && !tile.WaterCovered && tile.PrimaryBiome != null)
                    {
                        found.Add(tile.PrimaryBiome);
                    }
                }
                if (found.Count > 0)
                {
                    break;
                }
                List<PlanetTile> swap = frontier;
                frontier = next;
                next = swap;
            }
            return found;
        }

        private static void RefreshFishers(Caravan caravan, CaravanHuntingState state)
        {
            int ticksNow = Find.TickManager.TicksGame;
            if (ticksNow - state.cachedFishersTick < FisherCacheTicks && ticksNow >= state.cachedFishersTick)
            {
                return;
            }
            state.cachedFishersTick = ticksNow;
            state.cachedFishers.Clear();
            state.cachedFisherContributions.Clear();
            FisherScanCount++;

            List<Pawn> pawns = CaravanPawnUtility.GetPawns(caravan);
            for (int i = 0; i < pawns.Count; i++)
            {
                if (IsEligibleFisher(pawns[i], out float contribution))
                {
                    state.cachedFishers.Add(pawns[i]);
                    state.cachedFisherContributions.Add(contribution);
                }
            }
            Logger.Message($"Caravan {caravan.ID} fisher scan #{FisherScanCount}: {state.cachedFishers.Count} fisher(s) among {pawns.Count} pawn(s).");
        }

        private static bool IsEligibleFisher(Pawn p, out float contribution)
        {
            contribution = 0f;
            if (p == null || p.Destroyed || p.Dead || !CaravanHuntingUtility.IsActiveCaravanPawn(p))
            {
                return false;
            }
            if (!p.RaceProps.Humanlike || p.skills == null || p.IsPrisoner)
            {
                return false;
            }
            if (p.WorkTypeIsDisabled(WorkTypeDefOf.Fishing))
            {
                return false;
            }
            SkillRecord animals = p.skills.GetSkill(SkillDefOf.Animals);
            if (animals == null || animals.TotallyDisabled)
            {
                return false;
            }
            if (!p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
            {
                return false;
            }
            contribution = p.GetStatValue(StatDefOf.FishingSpeed) * p.GetStatValue(StatDefOf.FishingYield);
            return contribution > 0f;
        }

        private static bool TryHarvest(Caravan caravan, FishingStatus status, CaravanHuntingState state)
        {
            Thing catchStack = ThingMaker.MakeThing(status.Fish);
            catchStack.stackCount = FishPerCatch;
            float mass = catchStack.stackCount * catchStack.GetStatValue(StatDefOf.Mass);
            if (mass > caravan.MassCapacity - caravan.MassUsage)
            {
                catchStack.Destroy();
                return false;
            }
            CaravanInventoryUtility.GiveThing(caravan, catchStack);
            state.pendingFishDefName = null;
            return true;
        }

        private static string BuildTooltip(FishingStatus status)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("CaravanSustenance.Fishing.Gizmo.Desc".Translate());
            sb.AppendLine();
            sb.AppendLine();
            if (status.RequirementsMet)
            {
                sb.Append("CaravanSustenance.Fishing.Tooltip.Fishers".Translate(status.FisherCount));
                sb.AppendLine();
                sb.Append("CaravanSustenance.Fishing.Tooltip.Catch".Translate(status.Fish != null ? status.Fish.LabelCap : "CaravanSustenance.Fishing.Tooltip.CatchPending".Translate()));
                sb.AppendLine();
                sb.Append("CaravanSustenance.Fishing.Tooltip.Rate".Translate(status.DailyNutrition.ToString("0.##")));
                sb.AppendLine();
                sb.Append("CaravanSustenance.Tooltip.YieldMultiplier".Translate(Mod.Settings.fishingYieldMultiplier.ToStringPercent()));
                sb.AppendLine();
                string water = (status.Saltwater ? "CaravanSustenance.Fishing.Water.Salt" : "CaravanSustenance.Fishing.Water.Fresh").Translate();
                sb.Append("CaravanSustenance.Fishing.Tooltip.Water".Translate(water));
            }
            else
            {
                sb.Append("CaravanSustenance.Tooltip.Paused".Translate(status.ReasonKey));
            }
            sb.AppendLine();
            sb.AppendLine();
            sb.Append("CaravanSustenance.Fishing.Tooltip.Rot".Translate());
            return sb.ToString();
        }
    }
}
