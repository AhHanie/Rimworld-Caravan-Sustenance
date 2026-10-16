using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace Caravan_Sustenance
{
    // Soft Vehicle Framework support. Nothing is compiled against Vehicle Framework and nothing is
    // patched: the lookup runs once and is compiled into a plain delegate, so per-call cost is
    // about that of a normal method call.
    public static class CaravanPawnUtility
    {
        private const string VehicleFrameworkPackageId = "SmashPhil.VehicleFramework";

        private static bool resolved;
        private static Type vehicleCaravanType;
        private static Func<Caravan, List<Pawn>> getAllPawns;

        // On a Vehicle Framework vehicle caravan, PawnsListForReading holds only the vehicles and any
        // dismounted pawns; the people aboard are only in VehicleCaravan.AllPawnsAndVehiclePassengers.
        public static List<Pawn> GetPawns(Caravan caravan)
        {
            if (!resolved)
            {
                Resolve();
            }
            if (getAllPawns != null && vehicleCaravanType.IsInstanceOfType(caravan))
            {
                return getAllPawns(caravan) ?? caravan.PawnsListForReading;
            }
            return caravan.PawnsListForReading;
        }

        private static void Resolve()
        {
            resolved = true;
            bool loaded = ModsConfig.IsActive(VehicleFrameworkPackageId);
            Logger.Message($"Vehicle Framework loaded: {loaded}.");
            if (!loaded)
            {
                return;
            }

            try
            {
                Type type = AccessTools.TypeByName("Vehicles.World.VehicleCaravan") ?? AccessTools.TypeByName("Vehicles.VehicleCaravan");
                if (type != null && AccessTools.PropertyGetter(type, "AllPawnsAndVehiclePassengers") != null)
                {
                    ParameterExpression caravan = Expression.Parameter(typeof(Caravan), "caravan");
                    Expression body = Expression.Property(Expression.Convert(caravan, type), "AllPawnsAndVehiclePassengers");
                    getAllPawns = Expression.Lambda<Func<Caravan, List<Pawn>>>(body, caravan).Compile();
                    vehicleCaravanType = type;
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Exception(ex, "Vehicle Framework lookup");
            }
            Log.Warning("[Caravan Sustenance] Vehicle Framework is loaded but VehicleCaravan.AllPawnsAndVehiclePassengers was not found; pawns inside vehicles will not be counted for hunting or fishing.");
        }
    }
}
