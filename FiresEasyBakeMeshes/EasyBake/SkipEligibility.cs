using System;
using System.Collections.Generic;

using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // Decides which baked pieces may stay uncreated on this client. Only pure structure qualifies: every component in
    // the prefab must be one the combined mesh and a stand-in collider fully reproduce, so a piece that crafts, stores,
    // lights, protects, comforts, animates or reacts in any way is always created for real.
    internal static class SkipEligibility
    {
        internal sealed class PrefabInfo
        {
            public bool Skippable;
            // What stops a prefab from being skipped, for ebm_census.
            public string Blocker;
            public GameObject Prefab;
            // Its WearNTear carries a snow cap, so Deep North weather can change what it shows.
            public bool CanShowSnow;
            // 1.2.180: it has a WearNTear at all, so Vedr 0.8.145+ may give it a snow cap at runtime (PieceSnowCap, WearNTear.Awake postfix) although the prefab has none.
            public bool HasWear;
            public float DefaultHealth;
            public bool AllImmune;
            public bool SyncsScale;
            public Vector3 PrefabScale;
            public bool? BakeUnsafe;   // 1.2.253: LateJoinFromZdo's answer from ZoneTracker.HasBakeUnsafeComponent, per prefab; goes with this cache on Clear
            // Components the bake cannot stand in for that sit under a wear model, so whether they run depends on the look.
            public readonly List<Component> LookDependentBlockers = new List<Component>();
            private readonly Dictionary<WearLook, string> _blockerByLook = new Dictionary<WearLook, string>();

            public string BlockerFor(WearLook look)
            {
                if (LookDependentBlockers.Count == 0) return null;
                if (_blockerByLook.TryGetValue(look, out string blocker)) return blocker;
                blocker = null;
                for (int i = 0; i < LookDependentBlockers.Count && blocker == null; i++)
                    if (WearLooks.IsShown(Prefab, look, LookDependentBlockers[i].transform)) blocker = LookDependentBlockers[i].GetType().Name;
                _blockerByLook[look] = blocker;
                return blocker;
            }
        }

        // RandomMaterialValues only tints renderers the bake already replaces. SimpleMeshCombine (its own assembly) is an
        // editor tool with no runtime callbacks. DropOnDestroyed has no behaviour of its own: its Awake subscribes to
        // WearNTear.m_onDestroyed and it runs only when that fires, and ZdoMatches skips nothing but pieces that event
        // cannot reach. A breakable Ashlands ruin still fails ZdoMatches on its health and is created for real, so it
        // can still be mined for its drops.
        private static readonly HashSet<Type> AllowedComponents = new HashSet<Type>
        {
            typeof(Transform), typeof(MeshFilter), typeof(MeshRenderer), typeof(LODGroup),
            typeof(BoxCollider), typeof(SphereCollider), typeof(CapsuleCollider), typeof(MeshCollider),
            typeof(Piece), typeof(WearNTear), typeof(ZNetView), typeof(RandomMaterialValues),
            typeof(DropOnDestroyed),
        };

        private static readonly HashSet<string> AllowedComponentNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "SimpleMeshCombine", "SimpleMeshCombineMaster",
        };

        // Allowed only while VegetationSkipAllowed, which is why they are not in the set above: a prefab's verdict is
        // cached, so the flag's SettingChanged clears that cache.
        //
        // TreeBase and Destructible carry no periodic work (TreeBase has none at all; Destructible's only
        // InvokeRepeating is DestroyNow, and world vegetation has no TTL), and their RPC_Damage is the same
        // owner-gated, ZDO-health shape WearNTear uses, so the existing damage replay covers them.
        //
        // LodFadeInOut is moot rather than merely harmless: its whole body runs once in Awake, parking the LODGroup's
        // reference point so an object spawning over 20 m away stays invisible for 0.1-0.3 s instead of popping in.
        // A skipped object never spawns, so there is no pop to hide.
        //
        // DELIBERATELY ABSENT: ZSyncTransform and RandomFlyingBird. Both mean the object MOVES, and an instanced
        // group holds one static matrix per instance. MineRock5 is absent too - UpdateSupport, per-area health and
        // UpdateMesh rebuilding per-cell geometry make it a different problem.
        private static readonly HashSet<Type> VegetationAllowedComponents = new HashSet<Type>
        {
            typeof(TreeBase), typeof(Destructible), typeof(LodFadeInOut),
        };

        private static bool VegetationAllows(Type type) => VegetationSkipAllowed && VegetationAllowedComponents.Contains(type);

        private static readonly PrefabInfo NoScene = new PrefabInfo { Blocker = "no scene" };
        private static readonly Dictionary<int, PrefabInfo> _byPrefab = new Dictionary<int, PrefabInfo>();

        public static void Clear() => _byPrefab.Clear();
        internal static bool Known(int prefabHash) => _byPrefab.ContainsKey(prefabHash);   // 1.2.157

        public static PrefabInfo Get(int prefabHash)
        {
            if (_byPrefab.TryGetValue(prefabHash, out var info)) return info;
            var scene = ZNetScene.instance;
            if (scene == null) return NoScene;
            info = Build(FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(prefabHash));
            _byPrefab[prefabHash] = info;
            return info;
        }

        public static bool IsPrefabSkippable(int prefabHash) => Get(prefabHash).Skippable;

        private static PrefabInfo NotSkippable(string blocker) => new PrefabInfo { Blocker = blocker };

        private static PrefabInfo Build(GameObject prefab)
        {
            if (prefab == null) return NotSkippable("an unknown prefab");
            var piece = prefab.GetComponent<Piece>();
            var wear = prefab.GetComponent<WearNTear>();
            var view = prefab.GetComponent<ZNetView>();

            // ZNetView is the only one of the three the skip needs: skipping means never creating the object a ZDO
            // asks for, which only applies to a ZDO-backed prefab. Piece and WearNTear are the opposite of blockers.
            // A prefab with no WearNTear cannot be damaged, cannot be destroyed and has no worn or broken look to
            // diverge into, which is exactly the invulnerability ZdoMatches spends its health check establishing for
            // the prefabs that do have one. Demanding it kept the most static pieces in the world out of the skip.
            if (view == null) return NotSkippable("no ZNetView on its root");
            if (piece != null && piece.m_comfort > 0) return NotSkippable("comfort");
            if (ZoneTracker.IsExcludedFromBaking(prefab)) return NotSkippable("excluded from baking by its mod or [Batching] ExcludedPrefabs");

            var info = new PrefabInfo
            {
                Skippable = true,
                Prefab = prefab,
                CanShowSnow = wear != null && wear.m_snow != null,
                HasWear = wear != null,
                DefaultHealth = wear != null ? wear.m_health : 0f,
                AllImmune = wear == null || InvulnerableClassifier.AllImmune(wear.m_damages),
                SyncsScale = view.m_syncInitialScale,
                PrefabScale = prefab.transform.localScale,
            };

            // Only what can ever run matters. A child inactive in the prefab stays inactive, since no allowed component
            // switches objects on - except WearNTear, whose models follow the piece's look, so what sits under them is
            // judged per look.
            var components = prefab.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component == null) return NotSkippable("a missing script");
                var type = component.GetType();
                if (AllowedComponents.Contains(type) || AllowedComponentNames.Contains(type.Name) || VegetationAllows(type)) continue;
                if (wear != null && UnderWearModel(component.transform, wear))
                {
                    info.LookDependentBlockers.Add(component);
                    continue;
                }
                if (!ActiveWithinPrefab(component.transform, prefab.transform)) continue;
                return NotSkippable(type.Name);
            }
            return info;
        }

        // ═══ PHASE 3: LETTING A DAMAGEABLE PIECE GO UNCREATED ═════════════════
        // The health check below is the ONLY thing keeping damageable pieces live, and they are the largest
        // remaining cost: measured in the BlueHills town, 27,809 of ~51,000 created objects (54%) are pieces
        // the bake ALREADY DRAWS, kept real only so they can take damage, hold structure and be raided.
        //
        // What makes it safe to relax rather than reckless:
        //
        //  - Damage still lands. It is owner-authoritative and routed, so a remote owner applies it with full
        //    support, collapse and drop handling whether or not this client has the object. When THIS client
        //    owns it, DamageReplay catches the hit vanilla would have dropped, materialises that one piece and
        //    replays the hit into it.
        //  - A damaged piece stops matching on its own. Applying damage writes health into the ZDO, which bumps
        //    DataRevision; SkippedPiece.Unchanged then fails, this method runs again, and WearLooks.Resolve
        //    returns a worn or broken look that no longer equals the bake's, so the piece is created for real.
        //    No new reaction code.
        //  - Raids still find it: BaseAI does Physics.OverlapSphere then GetComponentInParent<StaticTarget>(),
        //    and the overlap already hits stand-in colliders - StandInDamage adds the StaticTarget it looks for.
        //
        // WHAT IS NOT PROVEN, and why this defaults OFF: a piece THIS CLIENT owns and has not created runs no
        // WearNTear Update, so its own rain wear and support check do not tick until something materialises it.
        // On a dedi the server owns much of the world and is unaffected, but ZDOMan reassigns ownership by
        // active area, so a walking player picks up ownership of what they pass. Whether that shows up as a
        // wall that should have collapsed and did not is a question for the rig, not for this comment.
        internal static bool DamageableSkipAllowed;

        // Trees, shrubs and bushes. Unlike DamageableSkipAllowed there is no wear or support tick to lose:
        // TreeBase has no periodic work at all and an untimed Destructible has none either.
        internal static bool VegetationSkipAllowed;

        // The live ZDO must still describe the piece the cache drew: invulnerable, the same look, rotation and scale.
        // Position is part of the identity the caller matched on.
        public static bool ZdoMatches(ZDO zdo, PrefabInfo info, MeshBaker.PieceTransform cached)
        {
            if (!info.Skippable) return false;
            if (ZoneTracker.KeptReal(zdo)) return false;   // 1.2.482: made real on request (EbmStandIns.MakeReal); no path skips it again this session
            if (PieceData.MustStayLive(zdo, zdo.GetPrefab())) return false;
            // 1.2.502 [08 - Snow And Seasons] Baked Pieces Under Snow Layer: while the snow owner's unified layer draws the tops (cast against the stand-in
            // colliders, which copy the prefab's own), winter snow is no reason to keep a piece real; both reasons below are skipped for every kind
            if (SnowLayerActive) NoteLayerBaked(zdo);
            else if (CapsCoverWinter(zdo, info)) { NoteCapsBaked(); NoteDeclaredSkip(zdo, info); }   // 1.2.514: EBM draws (or will draw) this kind's snow cap itself, so winter snow is no reason to keep it real
            else
            {
                if (SnowKeepsReal(zdo, info, true)) { if (PieceCaps.Active) s_keptNoCap++; return false; }   // 1.2.481 (F2); 1.2.514: counted when caps are on but this kind has no EBM cap (yet)
                if (VedrWinterSnowHere(zdo, info, out _, wantWhy: false)) return false;
            }
            if (!info.AllImmune && !(StoredHealth(zdo, info) < 0f) && !DamageableSkipAllowed) return false;
            var look = WearLooks.Resolve(info.Prefab, zdo);
            if (look != cached.Look || info.BlockerFor(look) != null) return false;
            if (Quaternion.Angle(zdo.GetRotation(), cached.Rotation) > 0.5f) return false;
            return (ExpectedScale(zdo, info) - cached.Scale).sqrMagnitude < 0.0001f;
        }

        // Which of ZdoMatches' checks fails, for ebm_census; null when the piece matches.
        public static string DescribeMismatch(ZDO zdo, PrefabInfo info, MeshBaker.PieceTransform cached, out string detail)
        {
            detail = null;
            if (ZoneTracker.KeptReal(zdo)) return "made real on request (EbmStandIns.MakeReal, e.g. Vedr's shovel or full snow load) and kept real this session";   // 1.2.482, the same order as ZdoMatches
            if (PieceData.MustStayLive(zdo, zdo.GetPrefab()))
            {
                detail = PieceData.DescribeLiveEdits(zdo, zdo.GetPrefab());
                return "a field edit or Structure Tweaks key on it changes what the piece shows or offers, which only a real piece carries";
            }

            bool layer = SnowLayerActive || CapsCoverWinter(zdo, info);   // 1.2.502 / 1.2.514: the same bypasses as ZdoMatches
            if (!layer && SnowKeepsReal(zdo, info, false))   // 1.2.481: the same test as the skip decision (F2), not counted
            {
                detail = zdo.GetFloat(ZDOVars.s_snow, 0f) > 0f
                    ? $"snow buildup {zdo.GetFloat(ZDOVars.s_snow, 0f):F2} on its ZDO"
                    : "spawned with a location, so Deep North weather will snow it";
                // 1.2.333 ([lead], the snow-cap design gain question): is snow the ONLY reason, and could this piece take snow damage? Split rows in ebm_census.
                bool onlySnow = !VedrWinterSnowHere(zdo, info, out _, wantWhy: false)
                    && (info.AllImmune || StoredHealth(zdo, info) < 0f || DamageableSkipAllowed)
                    && WearLooks.Resolve(info.Prefab, zdo) == cached.Look && info.BlockerFor(cached.Look) == null
                    && Quaternion.Angle(zdo.GetRotation(), cached.Rotation) <= 0.5f && (ExpectedScale(zdo, info) - cached.Scale).sqrMagnitude < 0.0001f;
                bool canTakeSnowDamage = !info.AllImmune && !(StoredHealth(zdo, info) < 0f);
                return "snow can change what it shows, which only a real piece tracks" + (onlySnow ? (canTakeSnowDamage ? " [snow ONLY; damageable]" : " [snow ONLY; invulnerable]") : " [snow + another reason]");
            }

            if (!layer && VedrWinterSnowHere(zdo, info, out string winterWhy))
            {
                detail = winterWhy;
                return "Vedr winter snow builds on pieces here, which only a real piece grows and draws";
            }

            float health = StoredHealth(zdo, info);
            if (!info.AllImmune && !(health < 0f) && !DamageableSkipAllowed)
            {
                detail = $"server health {health:F0}, prefab damage modifiers not all Immune, so only this copy counts as invulnerable";
                return "its server health is not below zero (invulnerable on this copy only)";
            }
            var look = WearLooks.Resolve(info.Prefab, zdo);
            if (look != cached.Look)
            {
                detail = $"it shows {look}, the bake drew {cached.Look}";
                return "its look changed since the bake";
            }
            string blocker = info.BlockerFor(look);
            if (blocker != null)
            {
                detail = $"its {look} look switches on a {blocker}";
                return "its look shows a component the bake cannot stand in for";
            }
            float angle = Quaternion.Angle(zdo.GetRotation(), cached.Rotation);
            if (angle > 0.5f)
            {
                detail = $"rotated {angle:F1} degrees from the baked rotation";
                return "rotated since the bake";
            }
            Vector3 expected = ExpectedScale(zdo, info);
            if ((expected - cached.Scale).sqrMagnitude >= 0.0001f)
            {
                detail = $"scale from the ZDO and prefab {expected.ToString("F2")}, baked {cached.Scale.ToString("F2")}, prefab {info.PrefabScale.ToString("F2")}, syncs scale {info.SyncsScale}";
                return "its scale differs from the bake";
            }
            return null;
        }

        // Mirrors ZNetView.Awake: a prefab that syncs its scale takes the ZDO's vector, else its scalar on all three axes,
        // but only when that scalar differs from the prefab's own x; any other prefab keeps its own, possibly non-uniform,
        // scale.
        internal static Vector3 ExpectedScale(ZDO zdo, PrefabInfo info)
        {
            if (!info.SyncsScale) return info.PrefabScale;
            Vector3 scale = zdo.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
            if (scale != Vector3.zero) return scale;
            float scalar = zdo.GetFloat(ZDOVars.s_scaleScalarHash, info.PrefabScale.x);
            return info.PrefabScale.x.Equals(scalar) ? info.PrefabScale : new Vector3(scalar, scalar, scalar);
        }

        // Valheim 1.0 grows snow on unroofed, unshielded pieces in Deep North and lets the player brush it off by
        // walking past, switching m_snow / m_snowWorn / m_snowBroken on above a 0.25 buildup and driving _SnowLevel
        // per object through MaterialMan (WearNTear.UpdateSnowVisual, Character's snow-walk sweep). A skipped piece
        // never runs that update, so it would sit bare while its live neighbours whiten; a piece baked WITH snow on
        // it would wear that snow forever, since a combined mesh has neither the switchable renderers nor the
        // per-object property. Either way the snow cap is not static, so snow-capable prefabs stay live where snow
        // can reach them.
        //
        // The two ZDO keys need different treatment. s_snow is only ever written inside WearNTear's DeepNorth branch,
        // so it scopes itself. s_preSnow does NOT: ZoneSystem sets it on every location-spawned WearNTear in the
        // world and only Deep North consumes it, so without the biome test this would hold every location piece
        // everywhere out of the bake. IsDeepnorth is a world-angle and a magnitude, no noise sampling, and static -
        // it needs no WorldGenerator instance.
        // 1.2.180 ([seasons] 11-07, Vedr 0.8.145 PieceSnowCap): a skipped piece runs no UpdateWear, so in Vedr's winter its ZDO snow never grows from 0 when this
        // client owns it, and a piece whose prefab has no m_snow (Vedr adds one at runtime to flat tops) was never even asked. So while Vedr builds snow on pieces
        // in this piece's biome (VedrApi.SnowBuildingBiomeMask, the mask its BuildingSnow transpiler remaps to Deep North), every WearNTear piece there stays real.
        // Outside winter the mask is 0 and nothing changes. Vedr's season event re-checks skipped pieces (ZoneTracker.RecheckSkippedForSeason).
        // Debug once per piece kind and season: kept real for winter snow, or skipped and why snow cannot reach it.
        internal static bool VedrWinterSnowHere(ZDO zdo, PrefabInfo info, out string why, bool wantWhy = true)
        {
            why = null;
            if (!info.HasWear) return false;
            int mask = VedrLink.SnowBuildingBiomeMask;
            if (mask == 0 || WorldGenerator.instance == null) return false;
            Vector3 p = zdo.GetPosition();
            if (BeyondWinterKeep(p)) return false;   // 1.2.480 WinterKeepRadius: beyond it no cap can show (Vedr hides caps past 60 m), so the piece may skip / bake
            var biome = WorldGenerator.instance.GetBiome(p);
            bool inBiome = ((int)biome & mask) != 0;
            string kind = info.Prefab != null ? info.Prefab.name : "?";
            // 1.2.183 ([seasons] Vedr 0.8.170): a kind with no vanilla cap stays real only while Vedr says it gets one (1) or has not decided (-1);
            // Vedr's 0 (sloped, door, station, out of size, ...) lets it skip and bake like any other piece
            int vedrCap = inBiome && !info.CanShowSnow ? VedrCapFor(kind) : 1;
            bool here = inBiome && vedrCap != 0;
            if (here) NoteWinterKept(zdo, info);
            // 1.2.183: the reason text is built only when it is logged or asked for (ZdoMatches passes wantWhy false; this runs per watched piece)
            bool say = (here ? s_snowSaidKeep : s_snowSaidSkip).Add(kind);
            if (!say && !wantWhy) return here;
            why = here
                ? $"{biome} is a Vedr winter snow biome now (mask {mask}), snow cap {(info.CanShowSnow ? "on the prefab" : vedrCap > 0 ? "fitted by Vedr" : "maybe fitted by Vedr (not decided yet)")}"
                : inBiome
                    ? $"{biome} is a Vedr winter snow biome now (mask {mask}), but Vedr fits no snow cap on this kind (VedrApi.PieceSnowCapFor 0)"
                    : $"{biome} is not a Vedr winter snow biome (mask {mask})";
            if (say) EasyBakeLog.Debug($"[Skip] {kind}: " + (here ? "kept real for winter snow: " : "may skip, no winter snow: ") + why);
            return here;
        }

        private static readonly HashSet<string> s_snowSaidKeep = new HashSet<string>(), s_snowSaidSkip = new HashSet<string>();

        // â”€â”€ 1.2.480 [08 - Snow And Seasons] WinterKeepRadius (EbmChurn.WinterKeepRadius; 0 = off = as before) â”€â”€
        private static float KeepRadius => EbmChurn.WinterKeepRadius != null ? EbmChurn.WinterKeepRadius.Value : 0f;
        // XZ distance from the local player; no player (dedicated server, loading) = no radius, the old rule
        private static bool BeyondWinterKeep(Vector3 p)
        {
            float r = KeepRadius;
            if (r <= 0f) return false;
            var pl = Player.m_localPlayer;
            if (pl == null) return false;
            Vector3 q = pl.transform.position;
            float dx = p.x - q.x, dz = p.z - q.z;
            return dx * dx + dz * dz > r * r;
        }
        /// <summary>The winter keep wants this skipped piece real now (in reach, cap-capable, in a Vedr snow biome). The watch hands it back.</summary>
        internal static bool WinterKeepNow(ZDO zdo, int prefabHash)
        {
            if (KeepRadius <= 0f || VedrLink.SnowBuildingBiomeMask == 0 || zdo == null) return false;
            if (BeyondWinterKeep(zdo.GetPosition())) return false;   // the cheap test first
            return VedrWinterSnowHere(zdo, Get(prefabHash), out _, wantWhy: false);
        }
        /// <summary>True when this zone is within reach of the radius and the player moved 4 m since its last radius re-check (updates lastPos).</summary>
        internal static bool WinterRadiusDue(Vector2s zone, ref Vector3 lastPos)
        {
            float r = KeepRadius;
            if (r <= 0f || VedrLink.SnowBuildingBiomeMask == 0) return false;
            var pl = Player.m_localPlayer;
            if (pl == null) return false;
            Vector3 q = pl.transform.position;
            Vector3 c = ZoneSystem.GetZonePos(zone);
            float reach = r + ZoneSystem.c_ZoneSize * 0.71f;   // the zone's half diagonal
            float dx = c.x - q.x, dz = c.z - q.z;
            if (dx * dx + dz * dz > reach * reach) return false;
            float mx = q.x - lastPos.x, mz = q.z - lastPos.z;
            if (mx * mx + mz * mz < 16f) return false;
            lastPos = q;
            return true;
        }
        internal static int WinterKeptCount => s_winterKept.Count;   // 1.2.480 [Churn] / [Settle]

        // 1.2.183: Vedr's answer per kind, cached: 1 and 0 are final; -1 is asked again only after PieceSnowCapRevision moves (CapFor reads
        // ZNetScene.GetPrefab, which may realize a lazy bundle, so it never runs per piece)
        private static readonly Dictionary<string, int> s_vedrCap = new Dictionary<string, int>();
        private static int s_vedrCapRevision = int.MinValue;

        private static int VedrCapFor(string kind)
        {
            if (!VedrLink.HasCapFor) return -1;
            int rev = VedrLink.PieceSnowCapRevision;
            if (rev != s_vedrCapRevision)
            {
                s_vedrCapRevision = rev;
                s_undecided.Clear();
                foreach (var entry in s_vedrCap) if (entry.Value < 0) s_undecided.Add(entry.Key);
                foreach (var k in s_undecided) s_vedrCap.Remove(k);
            }
            if (s_vedrCap.TryGetValue(kind, out int cap)) return cap;
            cap = VedrLink.PieceSnowCapFor(kind);
            s_vedrCap[kind] = cap;
            return cap;
        }

        private static readonly List<string> s_undecided = new List<string>();

        // 1.2.183 (lead 11-07, WearNTearUpdater 114 ms/s): distinct pieces and kinds this season's winter rule kept real, split by where their snow cap
        // comes from (on the prefab = vanilla m_snow; Vedr = only if Vedr's PieceSnowCap fits one), for the [Skip] summary line.
        private static readonly HashSet<ZDOID> s_winterKept = new HashSet<ZDOID>();
        private static readonly HashSet<ZDOID> s_winterKeptVedrCap = new HashSet<ZDOID>();
        private static readonly HashSet<string> s_winterKinds = new HashSet<string>();
        private static readonly HashSet<string> s_winterKindsVedrCap = new HashSet<string>();

        private static void NoteWinterKept(ZDO zdo, PrefabInfo info)
        {
            if (!s_winterKept.Add(zdo.m_uid)) return;
            string kind = info.Prefab != null ? info.Prefab.name : "?";
            s_winterKinds.Add(kind);
            if (info.CanShowSnow) return;
            s_winterKeptVedrCap.Add(zdo.m_uid);
            s_winterKindsVedrCap.Add(kind);
        }

        // ── 1.2.502 ([visual], design DESIGN_ebm_snow_layer_gate_20261122.md v1-v1.2, [2ic] APPROVED; Vedr side [seasons]) ──
        // [08 - Snow And Seasons] Baked Pieces Under Snow Layer (default OFF) + the owner's EbmApi.SetPieceSnowLayerOwner(owner, drawsTops). Decided at CREATE:
        // turning it on changes zones loaded afterwards; the owner flag going false or the key going off re-checks the skipped pieces (real again under the rule above).
        internal static BepInEx.Configuration.ConfigEntry<bool> SnowLayerKey;
        internal static bool SnowLayerActive => SnowLayerKey != null && SnowLayerKey.Value && VedrLink.SnowLayerDrawsTops;
        private static int s_layerWindow, s_layerTotal;   // [2ic] SHOULD: plain counters (no per-piece set): decisions made under the layer this tally window / since the flip
        private static void NoteLayerBaked(ZDO zdo) { s_layerWindow++; s_layerTotal++; }

        // ── 1.2.514 ([lead] winter-town profile pair O_20261124_124332 / O_20261124_130735: [SkipCensus] "already skipped at creation 8667" -> "3791", static
        // structure 1682 -> 6558 live, WearNTearUpdater +0.746 ms). This skip code is byte-identical in 1.2.504 and 1.2.513; what changed is the snow owner:
        // Vedr .26 sent SetPieceSnowLayerOwner("Vedr", true) (its log "[UnifiedLayer] build tops owner: ... sent"; EBM ":5883 [Skip] snow layer: winter
        // keep-real OFF"), Vedr .62 draws the game's per-piece caps instead and never sends it, so the 1.2.502 gate stayed off and SnowKeepsReal /
        // VedrWinterSnowHere kept every snowed piece real again ("ZdoChanged 344" -> "5029"). Those caps are now EBM's own draw (PieceCaps, the season mod's
        // registered mesh per kind, drawn for skipped pieces too), so for a kind whose cap EBM draws, winter snow is no reason to keep the piece real either.
        // Decided when a piece loads, like the layer gate; caps going inactive (key off, the owner clearing / removing a kind) re-checks the skipped pieces.
        // v3 ([lead]: the FIRST arrival must be covered, no create-then-destroy): a kind also counts as covered when the season mod has DECLARED it gets a
        // cap (the existing EbmApi.SetPieceSnowCap(prefab, 1), VedrLink.PieceSnowCapFor) while caps are on, so a piece is skipped at creation before the
        // kind's mesh is registered; the mesh registration later only draws. A kind declared then withdrawn (1 -> 0 / -1) re-checks the skipped pieces
        // (VedrLink.PushCap -> CapsCoverChanged). Undeclared kinds keep the ConvertQueue retry when their cap registers (ZoneTracker.RetryNewCapKinds).
        private static int s_capsWindow, s_capsTotal, s_keptNoCap;
        private static void NoteCapsBaked() { s_capsWindow++; s_capsTotal++; }
        // v4 ([2ic] SHOULD a, "declared but never drawn"): a kind skipped on a declaration alone (no mesh registered yet) is remembered with the time of its
        // first such skip; the 60 s tally names kinds still without a mesh 60 s later, so a run proves the count is 0. Forgotten once the mesh registers,
        // or on CapsCoverChanged (the skipped pieces are re-checked then). Main thread (ZdoMatches' callers).
        private sealed class DeclaredKind { internal string Name; internal float First; internal int Skips; }
        private static readonly Dictionary<int, DeclaredKind> s_declared = new Dictionary<int, DeclaredKind>();
        private static void NoteDeclaredSkip(ZDO zdo, PrefabInfo info)
        {
            int h = zdo.GetPrefab();
            if (PieceCaps.Registered(h) || info == null || info.Prefab == null) return;
            if (!s_declared.TryGetValue(h, out var d)) s_declared[h] = d = new DeclaredKind { Name = info.Prefab.name, First = Time.realtimeSinceStartup };
            d.Skips++;
        }
        private static readonly List<int> s_declaredDone = new List<int>();
        private static string DeclaredNoMeshPart()
        {
            if (s_declared.Count == 0) return "";
            int kinds = 0, skips = 0; string first = null;
            float now = Time.realtimeSinceStartup;
            s_declaredDone.Clear();
            foreach (var kv in s_declared)
            {
                if (PieceCaps.Registered(kv.Key)) { s_declaredDone.Add(kv.Key); continue; }
                if (now - kv.Value.First < 60f) continue;
                kinds++; skips += kv.Value.Skips; if (first == null) first = kv.Value.Name;
            }
            foreach (int h in s_declaredDone) s_declared.Remove(h);
            return $"; declared 1 and skipped, no mesh registered after 60 s: {kinds} kind(s), {skips} skip decision(s){(first != null ? $" (first '{first}')" : "")}";
        }
        internal static bool CapsCoverWinter(ZDO zdo, PrefabInfo info)
        {
            if (zdo == null) return false;
            if (PieceCaps.CoversWinter(zdo.GetPrefab())) return true;   // the kind's cap is registered
            return PieceCaps.Active && info != null && info.Prefab != null && VedrLink.HasCapFor && VedrLink.PieceSnowCapFor(info.Prefab.name) == 1;   // declared up front
        }
        /// <summary>PieceCaps: a kind lost its EBM-drawn cap (key off, cleared, removed). Skipped pieces are asked again (real again under the winter rule). Main thread.</summary>
        internal static void CapsCoverChanged(string why)
        {
            if (s_capsTotal == 0) return;   // nothing was skipped on the caps' account
            EasyBakeLog.Info($"[Skip] EBM-drawn caps: winter keep-real back ON for the affected kinds ({why}); skipped pieces re-checked, {s_capsTotal} skip decision(s) were made on the caps' account (1.2.514)");
            s_capsWindow = 0; s_capsTotal = 0; s_declared.Clear();   // v4: re-checked now, so nothing stays "declared, no mesh"
            ZoneTracker.RecheckSkippedForSeason(-1, -1);
        }

        internal static void BindSnowLayer(BepInEx.Configuration.ConfigFile config)
        {
            SnowLayerKey = EbmConfigMove.Bind(config, "08 - Snow And Seasons", "Baked Pieces Under Snow Layer", false,
                "With a snow owner drawing one snow layer over builds (Vedr's unified layer), winter snow is no reason to keep a piece real: winter arrival creates no more pieces than summer. Decided when a piece loads: turning it on in game changes zones loaded afterwards (set it before arriving); turning it off makes those pieces real again at once.");
            SnowLayerKey.SettingChanged += (_, __) =>
            {
                // no owner layer drawing tops = the gate is off either way: nothing to switch or re-check
                if (!VedrLink.SnowLayerDrawsTops) { EasyBakeLog.Info($"[Skip] snow layer: key {(SnowLayerKey.Value ? "on" : "off")}, but no snow owner draws a layer, so nothing changes (1.2.502)"); return; }
                SnowLayerChanged("key " + (SnowLayerKey.Value ? "on" : "off"), SnowLayerKey.Value);
            };
        }

        /// <summary>The key moved or the owner's flag flipped (VedrLink.SetSnowLayerOwner). Main thread.</summary>
        internal static void SnowLayerChanged(string why, bool nowActive)
        {
            if (nowActive)
            {
                EasyBakeLog.Info($"[Skip] snow layer: winter keep-real OFF ({why}); zones loaded from now on bake winter pieces (1.2.502)");
                return;
            }
            EasyBakeLog.Info($"[Skip] snow layer: winter keep-real back ON ({why}); skipped pieces re-checked, {s_layerTotal} skip decision(s) were made under the layer since it went on (1.2.502)");
            s_layerWindow = 0; s_layerTotal = 0;
            ZoneTracker.RecheckSkippedForSeason(-1, -1);
        }

        // Null when the rule kept nothing this season.
        internal static string WinterKeepSummary()
        {
            if (SnowLayerActive) { int w = s_layerWindow; s_layerWindow = 0; return $"winter keep: OFF under the snow layer, {w} skip decision(s) this window, {s_layerTotal} since it went on (1.2.502)"; }
            string caps = s_capsTotal > 0 || s_keptNoCap > 0 ? $"winter keep: OFF for kinds whose snow cap EBM draws, {s_capsWindow} skip decision(s) this window, {s_capsTotal} in all; kept real, cap not registered yet {s_keptNoCap} this window{DeclaredNoMeshPart()} (1.2.514)" : null;
            s_capsWindow = 0; s_keptNoCap = 0;
            if (caps != null && s_winterKept.Count == 0) return caps;   // the [Skip] tally line (every 60 s)
            if (s_winterKept.Count == 0) return null;
            int vedr = s_winterKeptVedrCap.Count;
            return (caps != null ? caps + "; " : "") + $"winter keep: {s_winterKept.Count} piece(s) of {s_winterKinds.Count} kind(s) kept real ({s_winterKept.Count - vedr} with a snow cap on the prefab, {vedr} of {s_winterKindsVedrCap.Count} kind(s) for a Vedr cap, fitted or not decided yet{(VedrLink.HasCapFor ? "" : " (Vedr has no PieceSnowCapFor: all kept)")}:{string.Join(", ", s_winterKindsVedrCap)})";
        }

        // A new season: the once-per-kind lines may say their reason again, and the keep count starts over.
        internal static void ForgetSnowLines()
        {
            s_snowSaidKeep.Clear(); s_snowSaidSkip.Clear();
            s_winterKept.Clear(); s_winterKeptVedrCap.Clear(); s_winterKinds.Clear(); s_winterKindsVedrCap.Clear();
            s_layerWindow = 0; s_layerTotal = 0;   // 1.2.502
            s_capsWindow = 0;   // 1.2.514 (s_capsTotal stays: CapsCoverChanged needs to know skips were made on the caps' account)
        }

        // 1.2.481 F2 ([visual], lead GO 11-21; W1 storm onset "hand-backs 5739 (watch ...)"): a cap-capable piece with snow on its ZDO stays real so its vanilla
        // cap can show. With [08 - Snow And Seasons] WinterKeepRadiusStormCaps ON and WinterKeepRadius above 0, beyond that radius it may skip / bake (Vedr
        // hides caps past 60 m); within it, as before. OFF (default) = exactly as before.
        private static bool SnowKeepsReal(ZDO zdo, PrefabInfo info, bool count)
        {
            if (!info.CanShowSnow || !SnowReaches(zdo)) return false;
            if (EbmChurn.StormCapsOn && BeyondWinterKeep(zdo.GetPosition())) { if (count) EbmChurn.NoteStormCapSkipped(); return false; }   // [2ic] note 1: ZdoMatches decisions only
            return true;
        }

        private static bool SnowReaches(ZDO zdo)
        {
            if (zdo.GetFloat(ZDOVars.s_snow, 0f) > 0f) return true;
            if (!zdo.GetBool(ZDOVars.s_preSnow)) return false;
            Vector3 position = zdo.GetPosition();
            return WorldGenerator.IsDeepnorth(position.x, position.z);
        }

        // The health WearNTear's damage gate reads: the ZDO's, defaulting to max health, which a field edit can replace.
        internal static bool StoredHealthIsNegative(ZDO zdo, PrefabInfo info) => StoredHealth(zdo, info) < 0f;

        private static float StoredHealth(ZDO zdo, PrefabInfo info)
        {
            float maxHealth = info.DefaultHealth;
            if (zdo.GetBool(WearLooks.FieldsKey) && zdo.GetBool(WearLooks.WearNTearFieldsKey)
                && zdo.GetFloat(WearLooks.MaxHealthKey, out float editedMaxHealth))
                maxHealth = editedMaxHealth;
            return zdo.GetFloat(ZDOVars.s_health, maxHealth);
        }

        private static bool ActiveWithinPrefab(Transform node, Transform root)
        {
            while (node != null)
            {
                if (!node.gameObject.activeSelf) return false;
                if (node == root) return true;
                node = node.parent;
            }
            return true;
        }

        private static bool UnderWearModel(Transform node, WearNTear wear)
        {
            return Under(node, wear.m_new) || Under(node, wear.m_worn) || Under(node, wear.m_broken);
        }

        private static bool Under(Transform node, GameObject model)
        {
            return model != null && (node == model.transform || node.IsChildOf(model.transform));
        }
    }
}
