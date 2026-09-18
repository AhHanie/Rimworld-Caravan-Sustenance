using HarmonyLib;
using UnityEngine;
using Verse;

namespace Caravan_Sustenance
{
    public class Mod : Verse.Mod
    {
        public static ModSettings Settings { get; private set; }

        public Mod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<ModSettings>();
            LongEventHandler.QueueLongEvent(Init, "CaravanSustenance.LoadingLabel", doAsynchronously: true, null);
        }

        private void Init()
        {
            new Harmony("sk.caravansustenance").PatchAll();
        }

        public override string SettingsCategory()
        {
            return "CaravanSustenance.SettingsTitle".Translate();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            ModSettingsWindow.Draw(inRect, Settings);
            base.DoSettingsWindowContents(inRect);
        }
    }
}
