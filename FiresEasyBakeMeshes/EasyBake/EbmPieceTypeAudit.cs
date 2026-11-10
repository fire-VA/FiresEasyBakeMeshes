using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.250 (moved from FGN's PieceTypeAudit, Fire's SP split, plan row #7). Names build pieces whose ZNetView.m_type is not Solid: objects are created in type order,
    // so a modded piece left at the default lands in the same tier as the loose items resting on it and can lose the race. Reads ZNetScene.m_prefabs once the count
    // stops growing (Jotunn registers after Awake). Debug output only; runs once a session; a client / local host only. [Optimize] PieceTypeAudit (default off, as FGN's was).
    internal static class EbmPieceTypeAudit
    {
        private const int MaxSettleChecks = 60, StableChecksRequired = 3;
        private const float SettleCheckSeconds = 1f;
        private static bool s_started;

        internal static void Begin(ZNetScene scene)
        {
            if (s_started || scene == null || FiresEasyBakeMeshesPlugin.PieceTypeAudit == null || !FiresEasyBakeMeshesPlugin.PieceTypeAudit.Value) return;
            if (ZNet.instance != null && ZNet.instance.IsDedicated()) return;
            s_started = true;
            scene.StartCoroutine(RunWhenPrefabsSettle());
        }

        private static IEnumerator RunWhenPrefabsSettle()
        {
            int lastCount = -1, stable = 0;
            for (int check = 0; check < MaxSettleChecks; check++)
            {
                ZNetScene scene = ZNetScene.instance;
                int count = scene != null ? scene.m_prefabs.Count : 0;
                if (count > 0 && count == lastCount) { if (++stable >= StableChecksRequired) break; }
                else stable = 0;
                lastCount = count;
                yield return new WaitForSeconds(SettleCheckSeconds);
            }
            Audit();
        }

        private static void Audit()
        {
            ZNetScene scene = ZNetScene.instance;
            if (scene == null) { EasyBakeLog.Debug("[PieceAudit] ZNetScene null at audit time, aborting."); return; }
            int total = 0, nonSolid = 0;
            var suspects = new List<string>();
            foreach (GameObject prefab in scene.m_prefabs)
            {
                if (prefab == null || prefab.GetComponent<Piece>() == null) continue;
                ZNetView nview = prefab.GetComponent<ZNetView>();
                if (nview == null) continue;
                total++;
                if (nview.m_type == ZDO.ObjectType.Solid) continue;
                nonSolid++;
                suspects.Add($"  {prefab.name}  type={nview.m_type}  collider={prefab.GetComponentInChildren<Collider>() != null}  wearNTear={prefab.GetComponent<WearNTear>() != null}");
            }
            EasyBakeLog.Info($"[PieceAudit] piece type audit ran: {total} build-piece prefabs, {nonSolid} NOT Solid");
            if (nonSolid == 0) return;
            EasyBakeLog.Debug("[PieceAudit] non-solid pieces. collider=True + wearNTear=True = a structural piece you can stand on that loads in the item tier, a fall-through suspect:");
            foreach (string line in suspects) EasyBakeLog.Debug(line);
        }
    }
}
