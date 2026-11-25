using System;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.460 ([visual], lead 11-18; Fire: "ebm should draw the mountains like vanilla would if vedr ISNT installed, it just needs to be able to SHOW the vedr snow
    // properly" / "it wasnt to PROVIDE those changes on its own"). ONE global only: _EbmVedrOn = 1 while a Vedr.VedrApi type is loaded (EBM/Heightmap then reads
    // Vedr's _Vedr* globals, which Vedr alone writes), else 0 (EBM/Heightmap takes the vanilla path of every row). EBM writes NO _Vedr* global and no look value.
    // Set at start (first Update: every plugin has loaded) and again on every world start (a new ZNetScene).
    internal static class EbmVedrDefaults
    {
        private static int s_vedr = -1;   // -1 not looked yet, 0 absent, 1 present
        private static ZNetScene s_pushedFor;
        private static bool s_pushedOnce;
        private static readonly int P_On = Shader.PropertyToID("_EbmVedrOn");

        internal static bool VedrPresent
        {
            get
            {
                if (s_vedr < 0)
                {
                    s_vedr = 0;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type t = null;
                        try { t = asm.GetType("Vedr.VedrApi", false); } catch { }
                        if (t != null) { s_vedr = 1; break; }
                    }
                    EasyBakeLog.Info(s_vedr == 1
                        ? "[Terrain] Vedr is installed: EBM/Heightmap shows Vedr's terrain look from Vedr's own globals (_EbmVedrOn = 1)"
                        : "[Terrain] Vedr is not installed: EBM/Heightmap draws the ground exactly as the game's terrain shader (_EbmVedrOn = 0)");
                }
                return s_vedr == 1;
            }
        }

        // From EbmTerrainShader.Tick (Plugin.Update, once a second)
        internal static void Tick()
        {
            var scene = ZNetScene.instance;
            if (s_pushedOnce && scene == s_pushedFor) return;
            s_pushedOnce = true;
            s_pushedFor = scene;
            Shader.SetGlobalFloat(P_On, VedrPresent ? 1f : 0f);
        }
    }
}
