using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.271 (lead 11-10 10 PM, Fire's layout rule "no stacked hot-method patches"): the two diagnostic owners on ZNetScene's create pass
    // (Patches.CreationCensus and EbmZoneLoadDiag) shared the same slots - a First prefix and a Last postfix on CreateObjects (every pass) and
    // a Last postfix on CreateObject (every object created) - as separate Harmony patches. Now EBM has one First prefix and one Last postfix
    // per method that runs both. Same priorities as before, so the order against other mods' patches is unchanged. The timing owner is the
    // outer one (its clock starts last in the prefix and stops first in the postfix), so the census's own work is not inside its numbers.
    // Each owner keeps its own key gate (CreationCensus.Active / EbmZoneLoadDiag.On); off = one bool read each.
    [HarmonyPatch]
    internal static class CreatePassHooks
    {
        [HarmonyPatch(typeof(ZNetScene), "CreateObjects"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void PassPre(ZNetScene __instance)
        {
            global::FiresEasyBakeMeshes.Patches.CreationCensus.BeforeCreateObjects();
            EbmZoneLoadDiag.CreatePre(__instance);
        }

        [HarmonyPatch(typeof(ZNetScene), "CreateObjects"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void PassPost(ZNetScene __instance)
        {
            EbmZoneLoadDiag.CreatePost(__instance);
            global::FiresEasyBakeMeshes.Patches.CreationCensus.AfterCreateObjects();
        }

        [HarmonyPatch(typeof(ZNetScene), "CreateObject"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void OnePre() => EbmZoneLoadDiag.OnePre();

        [HarmonyPatch(typeof(ZNetScene), "CreateObject"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void OnePost(ZDO zdo, GameObject __result)
        {
            EbmZoneLoadDiag.OnePost(zdo, __result);
            global::FiresEasyBakeMeshes.Patches.CreationCensus.AfterCreateObject(zdo, __result);
        }
    }
}
