using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.Patches
{
    // Patches that keep EasyBake.SectorInstanceMirror in sync with
    // ZNetScene.m_instances, and short-circuit ZNetScene.HaveInstanceInSector
    // to a single dictionary lookup.
    //
    // Hook surface:
    //   - AddInstance (postfix): a new ZNetView entered m_instances. Increment.
    //   - OnZDODestroyed (prefix): server told us a ZDO is gone. Decrement.
    //   - Destroy (prefix): code explicitly destroyed a ZNetView. Decrement
    //     before vanilla removes it from m_instances.
    //   - Shutdown (postfix): the whole scene is going away. Reset mirror.
    //   - HaveInstanceInSector (prefix): replace vanilla's O(m_instances)
    //     scan with an O(1) lookup.
    //
    // DestroyTimeSlicer also calls SectorInstanceMirror.OnInstanceRemoved
    // directly from its drain loop since it goes around ZNetScene.Destroy
    // and removes from m_instances itself.

    [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.AddInstance))]
    public static class ZNetScene_AddInstance_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ZDO zdo, ZNetView nview)
        {
            if (!FiresEasyBakeMeshesPlugin.SectorMirrorEnabled.Value) return;
            EasyBake.SectorInstanceMirror.OnInstanceAdded(zdo, nview);
        }
    }

    [HarmonyPatch(typeof(ZNetScene), "OnZDODestroyed")]
    public static class ZNetScene_OnZDODestroyed_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(ZDO zdo)
        {
            if (!FiresEasyBakeMeshesPlugin.SectorMirrorEnabled.Value) return;
            EasyBake.SectorInstanceMirror.OnInstanceRemoved(zdo);
        }
    }

    [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Destroy))]
    public static class ZNetScene_Destroy_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(GameObject go)
        {
            if (!FiresEasyBakeMeshesPlugin.SectorMirrorEnabled.Value) return;
            if (go == null) return;
            var nv = go.GetComponent<ZNetView>();
            if (nv == null) return;
            var zdo = nv.GetZDO();
            if (zdo == null) return;
            EasyBake.SectorInstanceMirror.OnInstanceRemoved(zdo);
        }
    }

    [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Shutdown))]
    public static class ZNetScene_Shutdown_Patch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            EasyBake.SectorInstanceMirror.Reset();
            // Zone state outlives the scene otherwise: after a relog a zone still "baked" would hide returning pieces
            // behind combined meshes the old scene took with it, and skipped ZDOs would belong to a finished session.
            try { EasyBake.ZoneTracker.Reset(); }
            catch { }
        }
    }

    // HaveInstanceInSector replacement. Vanilla walks every entry in m_instances
    // and computes ZoneSystem.GetZone(transform.position) per item — ~80 ms with
    // 146k instances. The mirror lookup is a single dictionary access. With the
    // mirror enabled this prefix returns false (skip vanilla) and sets __result
    // from the cached count.
    [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.HaveInstanceInSector))]
    public static class ZNetScene_HaveInstanceInSector_Patch
    {
        private static bool s_standDownLogged;

        [HarmonyPrefix]
        public static bool Prefix(Vector2s sector, ref bool __result, bool __runOriginal)
        {
            if (!__runOriginal)
            {
                if (!s_standDownLogged)
                {
                    s_standDownLogged = true;
                    EasyBakeLog.Info("[SectorMirror] Another mod already answers ZNetScene.HaveInstanceInSector; the sector mirror stands down.");
                }
                return false;
            }
            if (!FiresEasyBakeMeshesPlugin.SectorMirrorEnabled.Value) return true;
            if (EasyBake.SectorInstanceMirror.TryHasInstance(sector, out bool hasInstance))
            {
                __result = hasInstance;
                return false; // skip vanilla
            }
            return true; // mirror unavailable — fall through
        }
    }
}
