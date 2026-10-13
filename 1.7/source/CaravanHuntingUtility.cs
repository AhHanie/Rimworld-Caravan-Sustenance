using System.Collections.Generic;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Caravan_Sustenance
{
    public class HuntingStatus
    {
        public bool RequirementsMet;
        public bool CanActivelyHunt;
        public string ReasonKey;
        public List<Pawn> Hunters = new List<Pawn>();
        public Pawn Cook;
        public PawnKindDef Prey;
        public float AvailableCargoMass;
        public float DailyNutrition;
        public float EstimatedHarvestNutrition;
        public float EstimatedHarvestMass;
    }

    [StaticConstructorOnStartup]
    public static class CaravanHuntingUtility
    {
        private const int UpdateIntervalTicks = 15;
        private const int DebugLogThrottleTicks = 300; // ~5 real seconds at normal (1x) game speed.
        private const float StationaryProgressFactor = 2f;
        private const float SkillContributionPerLevel = 0.09f;
        private const float ReferenceAnimalDensity = 3.7f;
        private const float HuntingYieldPenalty = 0.65f;
        // Separate from the player-facing yield multiplier (default 1x): playtesting found the
        // base rate too slow to feel worthwhile, so this is baked into the formula directly.
        private const float BasePacingBoost = 4f;

        // Body sizes at or below this are left completely alone by the large-game curve (this
        // covers the small critters -- Rat/Hare/Squirrel-tier -- that playtesting found already
        // well balanced). Only the portion of body size *above* the pivot gets compressed by
        // Mod.Settings.largeGameCurveExponent, so big game still needs more credit than small
        // game, just not linearly-with-body-size more.
        private const float LargeGameCurvePivotBodySize = 0.3f;

        private static readonly Texture2D HuntIcon = ContentFinder<Texture2D>.Get("CaravanSustenance/Hunt");

        private static StatDef butcheryFleshEfficiencyStat;
        private static WorkTypeDef cookingWorkType;
        private static ThingDef mealDefCache;
        private static bool mealDefCacheComputed;

        private static StatDef ButcheryFleshEfficiencyStat =>
            butcheryFleshEfficiencyStat ?? (butcheryFleshEfficiencyStat = DefDatabase<StatDef>.GetNamed("ButcheryFleshEfficiency"));

        private static WorkTypeDef CookingWorkType =>
            cookingWorkType ?? (cookingWorkType = DefDatabase<WorkTypeDef>.GetNamed("Cooking"));

        public static WorldComponent_CaravanHunting Manager => Find.World?.GetComponent<WorldComponent_CaravanHunting>();

        public static CaravanHuntingState GetState(Caravan caravan)
        {
            return Manager?.GetState(caravan);
        }

        public static HuntingStatus GetStatus(Caravan caravan)
        {
            return BuildStatus(caravan, allowPreySelection: false);
        }

        public static float CalculateDailyNutrition(float sumSkillContribution, float animalDensity)
        {
            // No "/6" here: vanilla's forage formula divides by 6 then later multiplies by
            // progressPerTick(0.0001) * 60000 ticks/day, which is exactly *6, canceling it out.
            // DailyNutrition is that already-cancelled, final nutrition-per-day figure.
            return sumSkillContribution * (animalDensity / ReferenceAnimalDensity) * HuntingYieldPenalty * BasePacingBoost * Mod.Settings.huntingYieldMultiplier;
        }

        // The real (undampened) expected meat yield: used for cargo/mass estimates, since the
        // curve only compresses how long a harvest takes, not how much meat it actually produces.
        public static float CalculateRawMeatUnits(PawnKindDef prey, Pawn cook)
        {
            return prey.race.GetStatValueAbstract(StatDefOf.MeatAmount) * cook.GetStatValue(ButcheryFleshEfficiencyStat);
        }

        public static float CalculateHarvestThreshold(PawnKindDef prey, Pawn cook)
        {
            float rawMeatUnits = CalculateRawMeatUnits(prey, cook);
            float sizeDampeningFactor = GetSizeDampeningFactor(prey.race?.race?.baseBodySize ?? 1f);
            ThingDef meatDef = prey.race?.race?.meatDef;
            float meatNutritionPerUnit = meatDef?.GetStatValueAbstract(StatDefOf.Nutrition) ?? 0f;
            return rawMeatUnits * sizeDampeningFactor * meatNutritionPerUnit;
        }

        private static float GetSizeDampeningFactor(float rawBodySize)
        {
            if (rawBodySize <= LargeGameCurvePivotBodySize)
            {
                return 1f;
            }
            float exponent = Mathf.Clamp(Mod.Settings.largeGameCurveExponent, 0.05f, 1f);
            float dampedBodySize = LargeGameCurvePivotBodySize + Mathf.Pow(rawBodySize - LargeGameCurvePivotBodySize, exponent);
            return dampedBodySize / rawBodySize;
        }

        public static Gizmo GetGizmo(Caravan caravan)
        {
            CaravanHuntingState state = GetState(caravan);
            if (state == null)
            {
                return null;
            }

            HuntingStatus status = GetStatus(caravan);

            Command_Toggle toggle = new Command_Toggle
            {
                defaultLabel = "CaravanSustenance.Gizmo.Label".Translate(),
                icon = HuntIcon,
                isActive = () => state.enabledByPlayer,
                toggleAction = delegate
                {
                    state.enabledByPlayer = !state.enabledByPlayer;
                }
            };

            // The toggle always stays clickable: it records player intent, not current
            // eligibility. Hunting itself pauses/resumes automatically as status changes
            // (including automatically once the caravan reaches a tile with wildlife).
            toggle.defaultDesc = BuildTooltip(status);
            return toggle;
        }

        public static void ProcessTickInterval(Caravan caravan, int delta)
        {
            if (caravan == null || !caravan.IsPlayerControlled)
            {
                return;
            }
            if (!caravan.IsHashIntervalTick(UpdateIntervalTicks, delta))
            {
                return;
            }

            CaravanHuntingState state = GetState(caravan);
            if (state == null || !state.enabledByPlayer)
            {
                return;
            }

            Rand.PushState(Gen.HashCombineInt(caravan.ID, Find.TickManager.TicksGame));
            try
            {
                int ticksNow = Find.TickManager.TicksGame;
                bool shouldLog = ticksNow - state.lastDebugLogTick >= DebugLogThrottleTicks;

                HuntingStatus status = BuildStatus(caravan, allowPreySelection: true);
                if (!status.RequirementsMet)
                {
                    if (shouldLog)
                    {
                        Logger.Message($"Caravan {caravan.ID} hunting paused: {status.ReasonKey}");
                        state.lastDebugLogTick = ticksNow;
                    }
                    return;
                }

                // Use the fixed interval size, not the raw `delta` batch count: IsHashIntervalTick
                // firing true only means this caravan's slot in the 15-tick cycle fell within the
                // last `delta` ticks, not that `delta` ticks have passed since it last fired. Vanilla's
                // Caravan_ForageTracker does the same (hardcoded 15f, not `delta`).
                float creditGain = status.DailyNutrition / 60000f * UpdateIntervalTicks;
                if (!caravan.pather.MovingNow && !caravan.NightResting)
                {
                    creditGain *= StationaryProgressFactor;
                }
                state.nutritionCredit += creditGain;
                if (shouldLog)
                {
                    Logger.Message($"Caravan {caravan.ID} hunting {status.Prey.defName}: credit {state.nutritionCredit:0.####}/{status.EstimatedHarvestNutrition:0.####} (rate {status.DailyNutrition:0.####}/day, +{creditGain:0.#####} this interval).");
                    state.lastDebugLogTick = ticksNow;
                }

                if (status.EstimatedHarvestNutrition > 0f && state.nutritionCredit >= status.EstimatedHarvestNutrition)
                {
                    if (TryHarvest(caravan, status, state))
                    {
                        state.nutritionCredit -= status.EstimatedHarvestNutrition;
                        Logger.Message($"Caravan {caravan.ID} harvested {status.Prey.defName}; credit now {state.nutritionCredit:0.###}.");
                    }
                    else
                    {
                        Logger.Message($"Caravan {caravan.ID} deferred harvesting {status.Prey.defName}: insufficient cargo space.");
                    }
                }
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static HuntingStatus BuildStatus(Caravan caravan, bool allowPreySelection)
        {
            HuntingStatus status = new HuntingStatus();

            if (caravan == null || !caravan.IsPlayerControlled)
            {
                status.ReasonKey = "CaravanSustenance.Reason.NotPlayerControlled".Translate();
                return status;
            }

            List<Pawn> pawns = caravan.PawnsListForReading;
            float sumSkillContribution = 0f;
            for (int i = 0; i < pawns.Count; i++)
            {
                if (IsEligibleHunter(pawns[i], out int shootingLevel))
                {
                    status.Hunters.Add(pawns[i]);
                    sumSkillContribution += SkillContributionPerLevel * shootingLevel;
                }
            }
            if (status.Hunters.Count == 0)
            {
                status.ReasonKey = "CaravanSustenance.Reason.NoHunter".Translate();
                return status;
            }

            Pawn bestCook = null;
            int bestCookLevel = -1;
            for (int i = 0; i < pawns.Count; i++)
            {
                if (IsEligibleCook(pawns[i], out int cookLevel) && cookLevel > bestCookLevel)
                {
                    bestCook = pawns[i];
                    bestCookLevel = cookLevel;
                }
            }
            status.Cook = bestCook;
            if (bestCook == null)
            {
                status.ReasonKey = "CaravanSustenance.Reason.NoCook".Translate();
                return status;
            }

            if (caravan.NightResting)
            {
                status.ReasonKey = "CaravanSustenance.Reason.NightResting".Translate();
                return status;
            }

            CaravanHuntingState state = GetState(caravan);
            if (state == null || !TryGetPrey(caravan, state, allowPreySelection, out PawnKindDef prey))
            {
                status.ReasonKey = "CaravanSustenance.Reason.NoWildlife".Translate();
                return status;
            }
            status.Prey = prey;

            Tile tile = Find.WorldGrid[caravan.Tile];
            float density = tile?.AnimalDensity ?? 0f;
            status.DailyNutrition = CalculateDailyNutrition(sumSkillContribution, density);

            ThingDef meatDef = prey.race?.race?.meatDef;
            float rawMeatUnits = CalculateRawMeatUnits(prey, bestCook);
            status.EstimatedHarvestNutrition = CalculateHarvestThreshold(prey, bestCook);

            float meatMass = rawMeatUnits * (meatDef?.GetStatValueAbstract(StatDefOf.Mass) ?? 0f);
            ThingDef leatherDef = prey.race?.race?.leatherDef;
            float leatherUnits = prey.race.GetStatValueAbstract(StatDefOf.LeatherAmount);
            float leatherMass = leatherDef != null ? leatherUnits * leatherDef.GetStatValueAbstract(StatDefOf.Mass) : 0f;
            status.EstimatedHarvestMass = meatMass + leatherMass;

            status.AvailableCargoMass = caravan.MassCapacity - caravan.MassUsage;
            if (status.AvailableCargoMass < status.EstimatedHarvestMass)
            {
                status.ReasonKey = "CaravanSustenance.Reason.NoCargoSpace".Translate();
                return status;
            }

            status.RequirementsMet = true;
            status.CanActivelyHunt = state.enabledByPlayer;
            return status;
        }

        private static bool IsActiveCaravanPawn(Pawn p)
        {
            return !p.Downed && !p.InMentalState && !p.CarriedByCaravan();
        }

        private static bool IsEligibleHunter(Pawn p, out int shootingLevel)
        {
            shootingLevel = 0;
            if (!IsActiveCaravanPawn(p))
            {
                return false;
            }
            if (p.WorkTagIsDisabled(WorkTags.Violent))
            {
                return false;
            }

            if (p.RaceProps.IsMechanoid)
            {
                if (!p.IsColonyMechPlayerControlled)
                {
                    return false;
                }
                Verb verb = p.TryGetAttackVerb(null, allowManualCastWeapons: true);
                if (verb == null || verb.verbProps.IsMeleeAttack)
                {
                    return false;
                }
                shootingLevel = Mathf.Max(0, p.RaceProps.mechFixedSkillLevel);
                return true;
            }

            if (!p.RaceProps.Humanlike || p.skills == null)
            {
                return false;
            }
            if (p.WorkTypeIsDisabled(WorkTypeDefOf.Hunting))
            {
                return false;
            }
            SkillRecord shooting = p.skills.GetSkill(SkillDefOf.Shooting);
            if (shooting == null || shooting.TotallyDisabled)
            {
                return false;
            }
            if (p.equipment?.Primary == null || !p.equipment.Primary.def.IsRangedWeapon)
            {
                return false;
            }
            Verb primaryVerb = p.equipment.PrimaryEq?.PrimaryVerb;
            if (primaryVerb == null || !primaryVerb.Available())
            {
                return false;
            }
            if (!p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) || !p.health.capacities.CapableOf(PawnCapacityDefOf.Sight))
            {
                return false;
            }
            shootingLevel = shooting.Level;
            return true;
        }

        private static bool IsEligibleCook(Pawn p, out int cookingLevel)
        {
            cookingLevel = 0;
            if (!IsActiveCaravanPawn(p))
            {
                return false;
            }
            if (!p.RaceProps.Humanlike || p.skills == null)
            {
                return false;
            }
            if (p.WorkTypeIsDisabled(CookingWorkType))
            {
                return false;
            }
            SkillRecord cooking = p.skills.GetSkill(SkillDefOf.Cooking);
            if (cooking == null || cooking.TotallyDisabled)
            {
                return false;
            }
            cookingLevel = cooking.Level;
            return true;
        }

        private static bool TryGetPrey(Caravan caravan, CaravanHuntingState state, bool allowSelection, out PawnKindDef prey)
        {
            prey = null;
            PlanetTile planetTile = caravan.Tile;
            if (!planetTile.Valid)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(state.pendingPreyDefName) && state.pendingTile == planetTile.tileId)
            {
                PawnKindDef pending = DefDatabase<PawnKindDef>.GetNamedSilentFail(state.pendingPreyDefName);
                if (pending != null && IsStillValidPrey(planetTile, pending))
                {
                    prey = pending;
                    return true;
                }
                state.pendingPreyDefName = null;
            }

            if (!allowSelection)
            {
                return false;
            }

            Tile tile = Find.WorldGrid[planetTile];
            if (tile == null || tile.WaterCovered || tile.AnimalDensity <= 0f)
            {
                Logger.Message($"Caravan {caravan.ID} tile {planetTile.tileId} has no huntable wildlife: " +
                    (tile == null ? "tile not found." : tile.WaterCovered ? "tile is water." : $"animal density is {tile.AnimalDensity:0.##}."));
                return false;
            }

            List<PawnKindDef> candidates = new List<PawnKindDef>();
            List<float> weights = new List<float>();
            foreach (BiomeDef biome in tile.Biomes)
            {
                if (biome == null)
                {
                    continue;
                }
                foreach (PawnKindDef kind in biome.AllWildAnimals)
                {
                    if (!kind.RaceProps.Animal)
                    {
                        continue;
                    }
                    if (!Find.World.tileTemperatures.SeasonAndOutdoorTemperatureAcceptableFor(planetTile, kind.race))
                    {
                        continue;
                    }
                    float weight = GetAnimalCommonality(biome, tile, planetTile, kind);
                    if (weight <= 0f)
                    {
                        continue;
                    }
                    candidates.Add(kind);
                    weights.Add(weight);
                }
            }

            if (candidates.Count == 0)
            {
                Logger.Message($"Caravan {caravan.ID} tile {planetTile.tileId} (density {tile.AnimalDensity:0.##}) has no wild animal kinds with positive weight this season.");
                return false;
            }

            if (!TryWeightedIndex(weights, out int index))
            {
                return false;
            }

            prey = candidates[index];
            state.pendingPreyDefName = prey.defName;
            state.pendingTile = planetTile.tileId;
            Logger.Message($"Caravan {caravan.ID} selected prey {prey.defName} from {candidates.Count} candidates on tile {planetTile.tileId}.");
            return true;
        }

        private static bool TryWeightedIndex(List<float> weights, out int index)
        {
            float total = 0f;
            for (int i = 0; i < weights.Count; i++)
            {
                total += weights[i];
            }
            if (total <= 0f)
            {
                index = -1;
                return false;
            }
            float roll = Rand.Value * total;
            float cumulative = 0f;
            for (int i = 0; i < weights.Count; i++)
            {
                cumulative += weights[i];
                if (roll <= cumulative)
                {
                    index = i;
                    return true;
                }
            }
            index = weights.Count - 1;
            return true;
        }

        private static bool IsStillValidPrey(PlanetTile planetTile, PawnKindDef kind)
        {
            if (!kind.RaceProps.Animal)
            {
                return false;
            }
            if (!Find.World.tileTemperatures.SeasonAndOutdoorTemperatureAcceptableFor(planetTile, kind.race))
            {
                return false;
            }
            Tile tile = Find.WorldGrid[planetTile];
            if (tile == null)
            {
                return false;
            }
            foreach (BiomeDef biome in tile.Biomes)
            {
                if (biome == null)
                {
                    continue;
                }
                if (biome.CommonalityOfAnimal(kind) > 0f || biome.CommonalityOfCoastalAnimal(kind) > 0f || biome.CommonalityOfPollutionAnimal(kind) > 0f)
                {
                    return true;
                }
            }
            return false;
        }

        private static float GetAnimalCommonality(BiomeDef biome, Tile tile, PlanetTile planetTile, PawnKindDef kind)
        {
            float commonality;
            if (ModsConfig.BiotechActive && Rand.Value < WildAnimalSpawner.PollutionAnimalSpawnChanceFromPollutionCurve.Evaluate(tile.pollution))
            {
                commonality = biome.CommonalityOfPollutionAnimal(kind);
            }
            else
            {
                commonality = biome.CommonalityOfAnimal(kind);
            }
            if (tile.IsCoastal)
            {
                commonality += biome.CommonalityOfCoastalAnimal(kind);
            }
            foreach (TileMutatorDef mutator in tile.Mutators)
            {
                if (mutator.Worker != null)
                {
                    commonality *= mutator.Worker.AnimalCommonalityFactorFor(kind, planetTile);
                }
            }
            return commonality;
        }

        private static bool TryHarvest(Caravan caravan, HuntingStatus status, CaravanHuntingState state)
        {
            Pawn animal = PawnGenerator.GeneratePawn(status.Prey, null, caravan.Tile);
            try
            {
                float efficiency = status.Cook.GetStatValue(ButcheryFleshEfficiencyStat);
                List<Thing> products = new List<Thing>(animal.ButcherProducts(status.Cook, efficiency));

                ThingDef meatDef = status.Prey.race?.race?.meatDef;
                List<Thing> meatStacks = new List<Thing>();
                List<Thing> otherProducts = new List<Thing>();
                foreach (Thing product in products)
                {
                    if (meatDef != null && product.def == meatDef)
                    {
                        meatStacks.Add(product);
                    }
                    else
                    {
                        otherProducts.Add(product);
                    }
                }

                int totalMeatUnits = 0;
                foreach (Thing meat in meatStacks)
                {
                    totalMeatUnits += meat.stackCount;
                }
                foreach (Thing meat in meatStacks)
                {
                    meat.Destroy();
                }

                ThingDef mealDef = GetMealDef();
                float meatNutritionPerUnit = meatDef?.GetStatValueAbstract(StatDefOf.Nutrition) ?? 0f;
                float meatUnitsPerMeal = meatNutritionPerUnit > 0f ? 0.5f / meatNutritionPerUnit : 0f;

                int mealCount = meatUnitsPerMeal > 0f ? Mathf.FloorToInt(totalMeatUnits / meatUnitsPerMeal) : 0;
                int meatUnitsConsumed = Mathf.Min(totalMeatUnits, Mathf.RoundToInt(mealCount * meatUnitsPerMeal));
                int meatUnitsRemaining = Mathf.Max(0, totalMeatUnits - meatUnitsConsumed);

                List<Thing> mealsToGive = new List<Thing>();
                List<Thing> rawMeatToGive = new List<Thing>();

                int mealsRemaining = mealCount;
                while (mealsRemaining > 0)
                {
                    Thing meal = ThingMaker.MakeThing(mealDef);
                    int stackCount = Mathf.Min(mealsRemaining, mealDef.stackLimit);
                    meal.stackCount = stackCount;
                    CompIngredients comp = meal.TryGetComp<CompIngredients>();
                    if (comp != null && meatDef != null)
                    {
                        comp.ingredients.Add(meatDef);
                    }
                    meal.Notify_RecipeProduced(status.Cook);
                    mealsToGive.Add(meal);
                    mealsRemaining -= stackCount;
                }

                if (meatUnitsRemaining > 0 && meatDef != null)
                {
                    int remaining = meatUnitsRemaining;
                    while (remaining > 0)
                    {
                        Thing raw = ThingMaker.MakeThing(meatDef);
                        int stackCount = Mathf.Min(remaining, meatDef.stackLimit);
                        raw.stackCount = stackCount;
                        rawMeatToGive.Add(raw);
                        remaining -= stackCount;
                    }
                }

                float totalMass = 0f;
                foreach (Thing t in mealsToGive)
                {
                    totalMass += t.stackCount * t.GetStatValue(StatDefOf.Mass);
                }
                foreach (Thing t in rawMeatToGive)
                {
                    totalMass += t.stackCount * t.GetStatValue(StatDefOf.Mass);
                }
                foreach (Thing t in otherProducts)
                {
                    totalMass += t.stackCount * t.GetStatValue(StatDefOf.Mass);
                }

                float availableMass = caravan.MassCapacity - caravan.MassUsage;
                if (totalMass > availableMass)
                {
                    foreach (Thing t in mealsToGive)
                    {
                        t.Destroy();
                    }
                    foreach (Thing t in rawMeatToGive)
                    {
                        t.Destroy();
                    }
                    foreach (Thing t in otherProducts)
                    {
                        t.Destroy();
                    }
                    return false;
                }

                foreach (Thing t in mealsToGive)
                {
                    CaravanInventoryUtility.GiveThing(caravan, t);
                }
                foreach (Thing t in rawMeatToGive)
                {
                    CaravanInventoryUtility.GiveThing(caravan, t);
                }
                foreach (Thing t in otherProducts)
                {
                    CaravanInventoryUtility.GiveThing(caravan, t);
                }

                state.pendingPreyDefName = null;
                state.pendingTile = -1;
                return true;
            }
            finally
            {
                Find.WorldPawns.PassToWorld(animal, PawnDiscardDecideMode.Discard);
            }
        }

        private static ThingDef GetMealDef()
        {
            if (!mealDefCacheComputed)
            {
                mealDefCacheComputed = true;
                mealDefCache = ThingDefOf.MealSimple;
                if (ModLister.GetActiveModWithIdentifier("badoaks.meatonastick", ignorePostfix: true) != null)
                {
                    ThingDef meatOnAStick = DefDatabase<ThingDef>.GetNamedSilentFail("MeatOnAStick");
                    if (meatOnAStick != null)
                    {
                        mealDefCache = meatOnAStick;
                    }
                }
            }
            return mealDefCache;
        }

        private static string BuildTooltip(HuntingStatus status)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("CaravanSustenance.Gizmo.Desc".Translate());
            if (status.RequirementsMet)
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.Append("CaravanSustenance.Tooltip.Hunters".Translate(status.Hunters.Count));
                sb.AppendLine();
                sb.Append("CaravanSustenance.Tooltip.Cook".Translate(status.Cook.LabelShortCap));
                sb.AppendLine();
                sb.Append("CaravanSustenance.Tooltip.Prey".Translate(status.Prey.LabelCap));
                sb.AppendLine();
                sb.Append("CaravanSustenance.Tooltip.Rate".Translate(status.DailyNutrition.ToString("0.##")));
                sb.AppendLine();
                sb.Append("CaravanSustenance.Tooltip.StationaryBonus".Translate(StationaryProgressFactor.ToStringPercent()));
                sb.AppendLine();
                sb.Append("CaravanSustenance.Tooltip.YieldMultiplier".Translate(Mod.Settings.huntingYieldMultiplier.ToStringPercent()));
                sb.AppendLine();
                sb.Append("CaravanSustenance.Tooltip.SpeedPenalty".Translate(Mod.Settings.huntingSpeedPenalty.ToStringPercent()));
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.Append("CaravanSustenance.Tooltip.Paused".Translate(status.ReasonKey));
            }
            return sb.ToString();
        }
    }
}
