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
            if (PieceData.MustStayLive(zdo, zdo.GetPrefab())) return false;
            if (info.CanShowSnow && SnowReaches(zdo)) return false;
            if (VedrWinterSnowHere(zdo, info, out _, wantWhy: false)) return false;
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
            if (PieceData.MustStayLive(zdo, zdo.GetPrefab()))
            {
                detail = PieceData.DescribeLiveEdits(zdo, zdo.GetPrefab());
                return "a field edit or Structure Tweaks key on it changes what the piece shows or offers, which only a real piece carries";
            }

            if (info.CanShowSnow && SnowReaches(zdo))
            {
                detail = zdo.GetFloat(ZDOVars.s_snow, 0f) > 0f
                    ? $"snow buildup {zdo.GetFloat(ZDOVars.s_snow, 0f):F2} on its ZDO"
                    : "spawned with a location, so Deep North weather will snow it";
                return "snow can change what it shows, which only a real piece tracks";
            }

            if (VedrWinterSnowHere(zdo, info, out string winterWhy))
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

        // Null when the rule kept nothing this season.
        internal static string WinterKeepSummary()
        {
            if (s_winterKept.Count == 0) return null;
            int vedr = s_winterKeptVedrCap.Count;
            return $"winter keep: {s_winterKept.Count} piece(s) of {s_winterKinds.Count} kind(s) kept real ({s_winterKept.Count - vedr} with a snow cap on the prefab, {vedr} of {s_winterKindsVedrCap.Count} kind(s) for a Vedr cap, fitted or not decided yet{(VedrLink.HasCapFor ? "" : " (Vedr has no PieceSnowCapFor: all kept)")}:{string.Join(", ", s_winterKindsVedrCap)})";
        }

        // A new season: the once-per-kind lines may say their reason again, and the keep count starts over.
        internal static void ForgetSnowLines()
        {
            s_snowSaidKeep.Clear(); s_snowSaidSkip.Clear();
            s_winterKept.Clear(); s_winterKeptVedrCap.Clear(); s_winterKinds.Clear(); s_winterKindsVedrCap.Clear();
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
