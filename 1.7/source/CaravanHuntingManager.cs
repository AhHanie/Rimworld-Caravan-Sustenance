using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace Caravan_Sustenance
{
    public class CaravanHuntingState : IExposable
    {
        public bool enabledByPlayer;
        public float nutritionCredit;
        public string pendingPreyDefName;
        public int pendingTile = -1;

        // Not saved: only throttles debug logging, so a default of 0 after load is fine.
        public int lastDebugLogTick = -1;

        public void ExposeData()
        {
            Scribe_Values.Look(ref enabledByPlayer, "enabledByPlayer", false);
            Scribe_Values.Look(ref nutritionCredit, "nutritionCredit", 0f);
            Scribe_Values.Look(ref pendingPreyDefName, "pendingPreyDefName");
            Scribe_Values.Look(ref pendingTile, "pendingTile", -1);
        }
    }

    public class WorldComponent_CaravanHunting : WorldComponent
    {
        private const int CleanupIntervalTicks = 60000;

        private Dictionary<int, CaravanHuntingState> states = new Dictionary<int, CaravanHuntingState>();

        private List<int> tmpKeys;
        private List<CaravanHuntingState> tmpValues;

        public WorldComponent_CaravanHunting(World world)
            : base(world)
        {
        }

        public CaravanHuntingState GetState(Caravan caravan)
        {
            if (caravan == null)
            {
                return null;
            }
            if (!states.TryGetValue(caravan.ID, out CaravanHuntingState state))
            {
                state = new CaravanHuntingState();
                states[caravan.ID] = state;
            }
            return state;
        }

        public void RemoveState(int caravanId)
        {
            states.Remove(caravanId);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref states, "states", LookMode.Value, LookMode.Deep, ref tmpKeys, ref tmpValues);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && states == null)
            {
                states = new Dictionary<int, CaravanHuntingState>();
            }
        }

        public override void WorldComponentTick()
        {
            base.WorldComponentTick();
            if (states.Count == 0 || Find.TickManager.TicksGame % CleanupIntervalTicks != 0)
            {
                return;
            }
            CleanupRemovedCaravans();
        }

        private void CleanupRemovedCaravans()
        {
            List<Caravan> caravans = Find.WorldObjects.Caravans;
            List<int> toRemove = null;
            foreach (int id in states.Keys)
            {
                bool stillExists = false;
                for (int i = 0; i < caravans.Count; i++)
                {
                    if (caravans[i].ID == id)
                    {
                        stillExists = true;
                        break;
                    }
                }
                if (!stillExists)
                {
                    (toRemove ?? (toRemove = new List<int>())).Add(id);
                }
            }
            if (toRemove == null)
            {
                return;
            }
            for (int i = 0; i < toRemove.Count; i++)
            {
                states.Remove(toRemove[i]);
            }
        }
    }
}
