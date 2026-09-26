using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // ── PROVE THE ZDO BAKE BEFORE TRUSTING IT WITH THE RENDER PATH ───────────────────────────────────────
    //
    // WHY THIS EXISTS AT ALL. Object creation is the largest single cost in the game on this rig:
    //
    //     ZNetScene.CreateObject(per-instance)   1,070 ms/s over 5,135 calls in a 2 s streaming window
    //     EBM census: 75,089 pieces "unloaded after creation"
    //
    // Those 75,089 are created ONLY so the baker can see live pieces on a fresh zone, then thrown away.
    // TrySkipCreation already decides entirely from a ZDO - PieceIdentity.From(zdo) and
    // SkipEligibility.ZdoMatches(zdo, ...) - and the single thing missing on a fresh zone is `state.Baked`.
    // So the fix is not a rewrite: it is producing the bake from ZDOs at zone load, BEFORE vanilla
    // instantiates anything. DeferCreation's own header already concluded this in writing: "the fix is to
    // make the BAKE land before vanilla populates the zone, not to stall vanilla until it does."
    //
    // WHY A VERIFIER RATHER THAN JUST DOING IT. A wrong bake is invisible until someone looks at the world
    // and sees missing or misplaced geometry, and this runs on Fire's live test rig. So this pass derives the
    // ZDO answer alongside the real bake and REPORTS whether they agree. It writes nothing into the render
    // path, cannot move a vertex, and is off by default.
    //
    // WHAT IT COMPARES. PieceTransforms is the dictionary TrySkipCreation reads to decide a piece is already
    // drawn, so agreement on that dictionary is exactly the property the skip depends on:
    //   - same set of PieceIdentity keys (a missing key = a piece that would be created unnecessarily;
    //     an extra key = a piece that would be WRONGLY skipped, which is the dangerous direction)
    //   - same Position/Rotation/Scale/Look for shared keys
    //
    // The known gap is MeshRenderer.HasPropertyBlock(): it is per-instance renderer state and an
    // un-instantiated piece has none, so a ZDO bake cannot reproduce the live bake's property-block
    // rejections. This pass counts how often that matters instead of assuming it is rare.
    internal static class ZdoBakeVerifier
    {
        private static int s_zones;
        private static int s_keysLive;
        private static int s_keysZdo;
        private static int s_missingFromZdo;
        private static int s_extraInZdo;
        private static int s_posMismatch;
        private static int s_lookMismatch;
        private static float s_worstPosDelta;
        private static int s_outsideZone;
        private static int s_sectorScanned;

        internal static void Reset()
        {
            s_zones = 0; s_keysLive = 0; s_keysZdo = 0;
            s_missingFromZdo = 0; s_extraInZdo = 0;
            s_posMismatch = 0; s_lookMismatch = 0; s_worstPosDelta = 0f;
            s_outsideZone = 0; s_sectorScanned = 0;
            s_nextVerify = 0f; s_nextReport = 0f;
        }

        /// <summary>
        /// Called after a normal bake completes for a zone. Derives what a ZDO-only bake would have produced
        /// for the same zone and reports the difference. Never mutates <paramref name="live"/>.
        /// </summary>
        // ── A DIAGNOSTIC WITH NO COST CEILING DROPPED THE NETWORK CONNECTION ──────────────────────────────
        // MEASURED 2026-09-26. The first version ran on EVERY fresh bake with no throttle. A fresh bake happens
        // dozens of times during a join, and each verify walks a radius query of ~70,000 ZDOs doing a
        // ZoneSystem.GetZone plus a SkipEligibility lookup each. The client's ZNet.RPC_PeerInfo went from
        // unmeasurable to 7,060 ms and the join collapsed into a connect/disconnect loop:
        //     07:50:48 Connected -> 07:51:00 Lost connection: ErrorDisconnected
        //     07:52:04 Connected -> 07:52:13 Lost connection: ErrorDisconnected
        // Nothing ever walked. Bisected against the same dedi process by flipping only this setting: ON gave
        // 4+ disconnects and zero movement, OFF gave one connect, zero disconnects and a walking route.
        //
        // The dedi was healthy throughout (RPC_PeerInfo 525 ms), so this was entirely self-inflicted on the
        // client. Being measurement-only and default-off is what hid it: correctness got reviewed and cost
        // never did. So the ceiling is structural now rather than advisory.
        //
        //   - NOT UNTIL THE WORLD IS UP. No verifying before the local player exists, which is what keeps it
        //     off the join entirely - the join is precisely when fresh bakes come in bulk.
        //   - ONE ZONE PER INTERVAL, whatever the bake rate. A traverse produces plenty of zones over minutes;
        //     it does not need every one, and sampling cannot stall a frame budget.
        private const float MinSecondsBetweenVerifies = 3f;
        private static float s_nextVerify;

        internal static void Verify(Vector2s coord, MeshBaker.BakeResult live)
        {
            if (!FiresEasyBakeMeshesPlugin.ZdoBakeVerifyEnabled.Value) return;
            if (live == null || live.PieceTransforms == null) return;

            // Join-time bakes are the expensive ones and the ones that broke the connection; skip them outright.
            if (Player.m_localPlayer == null) return;

            float now = Time.unscaledTime;
            if (now < s_nextVerify) return;
            s_nextVerify = now + MinSecondsBetweenVerifies;

            var derived = DeriveFromZdos(coord);
            if (derived == null) return;

            s_zones++;
            s_keysLive += live.PieceTransforms.Count;
            s_keysZdo += derived.Count;

            foreach (var kv in live.PieceTransforms)
            {
                if (!derived.TryGetValue(kv.Key, out var mine)) { s_missingFromZdo++; continue; }

                float d = (mine.Position - kv.Value.Position).magnitude;
                if (d > 0.01f)
                {
                    s_posMismatch++;
                    if (d > s_worstPosDelta) s_worstPosDelta = d;
                }
                if (!mine.Look.Equals(kv.Value.Look)) s_lookMismatch++;
            }

            foreach (var kv in derived)
                if (!live.PieceTransforms.ContainsKey(kv.Key)) s_extraInZdo++;

            Report();
        }

        /// <summary>
        /// The whole point: build PieceTransforms for a zone from ZDOs alone, with nothing instantiated.
        /// Mirrors the live bake's eligibility so a difference means a real difference, not a different filter.
        /// </summary>
        private static Dictionary<MeshBaker.PieceIdentity, MeshBaker.PieceTransform> DeriveFromZdos(Vector2s coord)
        {
            var zdoMan = ZDOMan.instance;
            var znet = ZNet.instance;
            var scene = ZNetScene.instance;
            if (zdoMan == null || znet == null || scene == null) return null;

            var sector = new List<ZDO>();
            var distant = new List<ZDO>();
            try
            {
                // Public on ZDOMan, and the reason a ZDO-driven bake is reachable at all: every ZDO in the
                // zone, with nothing instantiated.
                zdoMan.FindSectorObjects(coord, ZNet.instance.GetSyncedSimulationDistance(), sector, distant);
            }
            catch { return null; }

            // ── FindSectorObjects IS A RADIUS QUERY, NOT ONE ZONE ─────────────────────────────────────────
            // Read ZDOMan.FindSectorObjects (1.0.15) before changing this. It calls FindObjects on `coord`,
            // then loops rings 1..NearSimulationDistance calling FindObjects on EVERY sector in each ring, then
            // FindDistantObjects out to TotalSimulationDistance. So it returns everything the client simulates,
            // not the contents of `coord`.
            //
            // The first run of this verifier did not filter, and reported: 14 zone(s), live 44,303 piece(s),
            // ZDO-derived 973,291, EXTRA 929,975 - which reads as "the ZDO bake would wrongly skip a million
            // pieces" and would have killed the idea. It was this call's radius, nothing else: 973,291/44,303
            // is 22x, and position mismatches were 0 and wear-look mismatches 0 across every matched key, i.e.
            // the derived DATA was exact and only the SET was wrong.
            //
            // ZoneSystem.GetZone(Vector3) is public static and gives a ZDO's own zone, so the filter needs no
            // private sector-index machinery. Comparing with Equals rather than == because Vector2s is used
            // here as a dictionary key and value equality is what that relies on.
            var zoneSystem = ZoneSystem.instance;
            if (zoneSystem == null) return null;

            int outsideZone = 0;
            var map = new Dictionary<MeshBaker.PieceIdentity, MeshBaker.PieceTransform>(sector.Count);
            for (int i = 0; i < sector.Count; i++)
            {
                var zdo = sector[i];
                if (zdo == null) continue;

                if (!coord.Equals(ZoneSystem.GetZone(zdo.GetPosition()))) { outsideZone++; continue; }

                int prefabHash = zdo.GetPrefab();
                if (prefabHash == 0) continue;

                var info = SkipEligibility.Get(prefabHash);
                if (!info.Skippable) continue;

                // Same "cannot be drawn by a bake" test the live path applies, and it reads only the ZDO.
                if (PieceData.MustStayLive(zdo, prefabHash)) continue;

                var identity = MeshBaker.PieceIdentity.From(zdo);
                map[identity] = new MeshBaker.PieceTransform
                {
                    Position = zdo.GetPosition(),
                    Rotation = zdo.GetRotation(),
                    Scale = ScaleOf(zdo),
                    Look = MeshBaker.LookOf(zdo),
                };
            }
            s_outsideZone += outsideZone;
            s_sectorScanned += sector.Count;
            return map;
        }

        // ZNetView writes the instance scale into the ZDO (ZDOVars.s_scaleHash) and reads it back on create,
        // so a uniformly scaled piece is recoverable; anything that never wrote one is scale 1.
        private static Vector3 ScaleOf(ZDO zdo)
        {
            var s = zdo.GetVec3(ZDOVars.s_scaleHash, Vector3.one);
            return s == Vector3.zero ? Vector3.one : s;
        }

        private static float s_nextReport;

        private static void Report()
        {
            float now = Time.unscaledTime;
            if (now < s_nextReport) return;
            s_nextReport = now + 30f;

            EasyBakeLog.Info(
                $"ZDO-bake verify over {s_zones} zone(s): live {s_keysLive} piece(s), ZDO-derived {s_keysZdo}. "
                + $"MISSING from the ZDO answer {s_missingFromZdo} (those would still be created), "
                + $"EXTRA in it {s_extraInZdo} (those would be WRONGLY skipped - this is the dangerous number), "
                + $"position mismatches {s_posMismatch} (worst {s_worstPosDelta:0.000} m), "
                + $"wear-look mismatches {s_lookMismatch}. A ZDO bake is only safe to trust when EXTRA is 0. "
                + $"[radius filter: {s_outsideZone} of {s_sectorScanned} scanned ZDO(s) belonged to a NEIGHBOURING "
                + "zone and were dropped - FindSectorObjects returns everything within simulation distance, so a "
                + "large number here is normal and a ZERO means the filter is not running]");
        }
    }
}
