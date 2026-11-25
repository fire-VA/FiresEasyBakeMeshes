using System;
using UnityEngine;

namespace FiresEasyBakeMeshes
{
    // 1.2.204 ([visual] 11-08, Fire: FTS's and Vedr's piece shaders "stand down when [EBM's] is installed alongside"): EBM's live state for
    // other mods, read by NAME (no compile reference) and gated on the ACTIVE flag, never on a version.
    public static class EbmApi
    {
        // 1.2.263: true on a headless dedicated server, where EBM stops in Plugin.Awake (nothing initialised). Every entry that does work then
        // returns its neutral value, so another mod calling EbmApi on a server can never throw. Plain delegate stores and events stay as they are.
        internal static bool Inert;
        public const int ApiVersion = 1;

        // True while EBM's own EBM/Piece draws EVERY piece material (the global swap). FTS (TossinShade) and Vedr then skip their piece-shader
        // swaps. 1.2.204: always false: EBM/Piece only draws EBM's own indirect clones, so FTS / Vedr keep swapping live renderers as today.
        public static bool PieceShaderActive { get; private set; }

        // Raised on the main thread whenever PieceShaderActive changes.
        public static event Action PieceShaderActiveChanged;

        // ── 1.2.228 ([visual], Fire's SP split: EBM paces object creation) ──
        // True while EBM's create pacer runs this session's create pass (client / local host). Re-read on every use: false = EBM is not
        // pacing (off, a dedicated server, or another mod already replaces the pass), keep your own path.
        // 1.2.250: this, ZoneFilling and ZoneSettled are the new home of FGN's FgnStreaming (same meanings, same shape).
        public static bool StreamingSignalActive => EasyBake.StreamPacer.Active;

        // ── 1.2.250 (Fire's SP split): distant prefabs, client side: EBM keeps the in-range ZDOs of a registered prefab in vanilla's distant list, so vanilla
        // creates and cleans them up. Registering again updates the distance; 0 or less unregisters. After a batch of registrations call RefreshDistantPrefabs.
        // Marking the ZDOs Distant and sending them to clients is server work and stays FGN's. Main thread. False on an empty name.
        public static bool RegisterDistantPrefab(string prefabName, float maxDistanceMeters) => !Inert && EasyBake.EbmDistantPrefabs.Register(prefabName, maxDistanceMeters);
        public static bool UnregisterDistantPrefab(string prefabName) => !Inert && EasyBake.EbmDistantPrefabs.Unregister(prefabName);
        public static void RefreshDistantPrefabs() { if (!Inert) EasyBake.EbmDistantPrefabs.Refresh(); }
        public static int DistantPrefabCount => Inert ? 0 : EasyBake.EbmDistantPrefabs.Count;

        // 1.2.231 ([fgn] FGN 1.6.79 gate): true while EBM's create pacer is installed, its [Optimize] StreamPacer key is on and it has not faulted
        // this session, i.e. EBM WILL pace the client create pass as soon as no earlier prefix takes it. Re-read on every use (the key is live).
        // A client / local-host answer: on a dedicated server EBM never paces (keep your server path).
        public static bool StreamPacerReady => EasyBake.StreamPacer.Ready && !(ZNet.instance != null && ZNet.instance.IsServer() && ZNet.instance.IsDedicated());
        // A near zone has objects waiting to be created / it has none left and is loaded (a later Filling is followed by another Settled).
        // Raised on the main thread inside the create pass, each handler in its own try/catch: keep handlers short.
        public static event Action<Vector2s> ZoneFilling;
        public static event Action<Vector2s> ZoneSettled;
        private static bool s_streamWarned;
        internal static void RaiseZoneFilling(Vector2s z) => RaiseZone(ZoneFilling, z, "ZoneFilling");
        internal static void RaiseZoneSettled(Vector2s z) => RaiseZone(ZoneSettled, z, "ZoneSettled");
        private static void RaiseZone(Action<Vector2s> ev, Vector2s z, string name)
        {
            if (ev == null) return;
            foreach (Delegate d in ev.GetInvocationList())
            {
                try { ((Action<Vector2s>)d)(z); }
                catch (Exception e) { if (!s_streamWarned) { s_streamWarned = true; EasyBakeLog.Warn($"[Stream] a {name} handler ({d.Method.DeclaringType?.FullName}.{d.Method.Name}) threw (said once): {e.Message}"); } }
            }
        }

        // 1.2.313 ([ghost], lead 11-11): true while EBM draws the given VANILLA shader's materials with its own shader, so another mod's swap of the same
        // materials stands down. Today: "Custom/Heightmap" while [Optimize] TerrainShader is on and EBM/Heightmap is loaded (EBM takes the non-snow chunks;
        // snow chunks stay Vedr's / FTS's snow swap / vanilla). Live (follows the key); false on a dedicated server, for any other name, or on an empty name.
        // 1.2.314 ([seasons] water STEP 1, merged as ONE method): also "Custom/Water" while [Optimize] WaterShader is on and EBM/Water is loaded (zone water only).
        // 1.2.318 (Fire 11-11 shader priority EBM > Vedr > FTS, Tools\DESIGN_shader_priority_ebm_vedr_fts_20261111.md): also "Custom/FlowOpaque" while
        // [Optimize] IndirectLavaVariant + IndirectRenderer are on (EBM's lava variant takes covered lava), and "Custom/Piece" while PieceShaderActive.
        // Fire: "the same toggles with ebm ... so we can toggle the shader off and use the other mods shader": EBM's own [Optimize] keys ARE those toggles
        // (TerrainShader, WaterShader, IndirectLavaVariant); a key off = false here, the revision bumps and the event fires, the lower mods pick it up.
        public static bool HandlesShader(string vanillaShaderName) =>
            !Inert && !string.IsNullOrEmpty(vanillaShaderName) && (HandledMask() & MaskOf(vanillaShaderName)) != 0;

        // 1.2.388 ([saddles]): EBM owns the ParticleMist tuner ([Optimize] MistParticleCount / MistOpacity) whenever it is installed, whatever its keys say, so
        // two mods never scale the same emitter values. A lower mod reads this by reflection (absent = an older EBM: it tunes itself).
        public static bool MistTunerOwned => !Inert;

        // EBM is the top of the priority list: it never stands down.
        public static bool IsStandingDown(string vanillaShaderName) => false;

        // Bumps on every change of what HandlesShader answers (a key flip, the bundle loading, the indirect renderer starting / stopping).
        // Lower mods subscribe to ShaderOwnershipChanged; the revision is the alloc-free fallback compare for a late-loading mod or a missed event.
        public static int ShaderOwnershipRevision { get; private set; }
        public static event Action ShaderOwnershipChanged;

        // The vanilla shader names EBM has its own version of (static; HandlesShader says which are live now).
        public static string[] HandledShaders() => (string[])s_handled.Clone();

        private static readonly string[] s_handled = { "Custom/Heightmap", "Custom/Water", "Custom/FlowOpaque", "Custom/Piece", EasyBake.EbmMist.Vanilla };   // 1.2.388: + the Lux mist shader ([Optimize] MistShader)
        private const int M_Terrain = 1, M_Water = 2, M_Lava = 4, M_Piece = 8, M_Mist = 16;
        private static int MaskOf(string n)
        {
            switch (n)
            {
                case "Custom/Heightmap": return M_Terrain;
                case "Custom/Water": return M_Water;
                case "Custom/FlowOpaque": return M_Lava;
                case "Custom/Piece": return M_Piece;
                case EasyBake.EbmMist.Vanilla: return M_Mist;   // 1.2.388
                default: return 0;
            }
        }
        private static int HandledMask()
        {
            int m = 0;
            if (EasyBake.EbmTerrainShader.Active) m |= M_Terrain;
            if (EasyBake.EbmWater.Active) m |= M_Water;
            if (FiresEasyBakeMeshesPlugin.IndirectLavaVariant != null && FiresEasyBakeMeshesPlugin.IndirectLavaVariant.Value && EasyBake.Indirect.EbmIndirect.Active) m |= M_Lava;
            if (PieceShaderActive) m |= M_Piece;
            if (EasyBake.EbmMist.Active) m |= M_Mist;   // 1.2.388: FTS stands down (its mist swap and its ParticleMist tuner)
            return m;
        }

        private static int s_lastMask = -1;
        private static float s_nextOwnershipCheck;
        // Called from a key's SettingChanged (at once) and from Plugin.Update (one float compare a frame, the mask once a second: the bundle / indirect
        // state has no event of its own). Raises ShaderOwnershipChanged only when the answer really changed; one Info line per change (a state change).
        internal static void RecheckShaderOwnership(string why, bool now)
        {
            if (Inert) return;
            float t = Time.unscaledTime;
            if (!now && t < s_nextOwnershipCheck) return;
            s_nextOwnershipCheck = t + 1f;
            int m = HandledMask();
            if (m == s_lastMask) return;
            bool first = s_lastMask < 0;
            s_lastMask = m;
            if (first && m == 0) return;   // nothing handled at start: no line, no event (the lower mods' first read already says false)
            ShaderOwnershipRevision++;
            EasyBakeLog.Info("[EbmApi] shader ownership (" + why + "): EBM draws " + (m == 0 ? "none (other mods / the game draw them)" :
                ((m & M_Terrain) != 0 ? "Custom/Heightmap (non-snow) " : "") + ((m & M_Water) != 0 ? "Custom/Water " : "")
                + ((m & M_Lava) != 0 ? "Custom/FlowOpaque (covered lava) " : "") + ((m & M_Piece) != 0 ? "Custom/Piece " : "") + ((m & M_Mist) != 0 ? "Lux Lit Particles/ Tess Bumped (the mist)" : "")).TrimEnd() + "; revision " + ShaderOwnershipRevision);
            Action ev = ShaderOwnershipChanged;
            if (ev == null) return;
            foreach (Delegate d in ev.GetInvocationList())
            {
                try { ((Action)d)(); }
                catch (Exception e) { EasyBakeLog.Warn($"[EbmApi] a ShaderOwnershipChanged listener ({d.Method.DeclaringType?.FullName}.{d.Method.Name}) threw: {e.Message}"); }
            }
        }

        // [seasons] 11-11: the water shader's settings owner (FTS). While one is registered, EBM pushes none of its default _Fires* water globals (the owner
        // pushes its own, read by EBM/Water under the same names) and calls perMaterial(material) on every water material EBM/Water takes (after EBM's own
        // _VolumeSize), e.g. FTS's per-material foam values and HD foam texture. perMaterial may be null. Null / empty owner = withdraw (EBM's defaults go
        // out again). Re-registering replaces. Main thread. A perMaterial that throws is dropped for the session (one Warning).
        public static void RegisterWaterGlobalsOwner(string owner, System.Action<UnityEngine.Material> perMaterial) { if (!Inert) EasyBake.EbmWater.RegisterOwner(owner, perMaterial); }

        // True while [Optimize] IndirectRenderer draws baked groups GPU-driven (EbmIndirect.Active).
        public static bool IndirectActive => EasyBake.Indirect.EbmIndirect.Active;

        // 1.2.315 (read-only, add-only; FDT's overlay): true while EBM's create pacer owns object creation this session (vanilla's CreateObjectsSorted
        // does not run then, so its candidate list is stale); the pacer's own queue of objects still to create. Both false / 0 when EBM is inert.
        public static bool StreamPacerOwnsCreation => !Inert && EasyBake.StreamPacer.OwnsCreation;
        public static int StreamQueueCount => Inert ? 0 : EasyBake.StreamPacer.QueueCount;

        // 1.2.288 (lead / [fgn] 11-11: FiresFixes decided "EBM's GPU fires not active" while they ran): true from EBM's config bind on while [Optimize] GpuFires is on
        // (client with a graphics device), so a mod that asks at its own decision time reads the truth. Live (follows the key).
        public static bool GpuFiresActive => EasyBake.GpuFires.ActiveNow;

        // 1.2.301 ([fgn] / [lead] 11-11: ONE owner for LightFlicker.CustomUpdate): true while EBM's LightFlicker LOD prefix is attached and running with
        // [Optimize] LightFlickerLodEnabled on (it covers off-view lights at its slowest rate, plus distance tiers and dark lights). FiresFixes reads it by name
        // each call and skips its own off-screen flicker skip while it is true. Live (follows the key).
        public static bool LightFlickerLodActive => FiresEasyBakeMeshesPlugin.LightFlickerLodEnabled != null && FiresEasyBakeMeshesPlugin.LightFlickerLodEnabled.Value && Patches.LightFlicker_CustomUpdate_Patch.Ran;

        // ── 1.2.206 ([wishbone] for [visual]): snow values pushed IN by the snow owner (Vedr builds against these; EBM calls no other mod) ──
        // Raised on the main thread when a zone's bake becomes Ready (and again when the zone comes back in range), with the ZDOIDs of the
        // pieces its instance groups draw. Answer with SetPieceSnow for those ids. Coordinates are vanilla zone coords.
        public static event Action<Vector2i, ZDOID[]> ZoneBaked;

        // Raised when an announced zone leaves the loaded area or its bake is torn down. Its stored values are already gone.
        public static event Action<Vector2i> ZoneUnloaded;

        // True: SetPieceSnow is read by both draw paths (instanced and indirect).
        public static bool AcceptsSnowValues => true;

        // One piece's snow: snowCover 0..1 (the _VedrSnowCover / _FiresSnowBiome value), wetScale 0 = dry (roofed) .. 1 = open.
        // void: the shape Vedr 0.8.218 binds. A piece EBM draws no instance of (not in an announced zone, or in a combined mesh) is ignored.
        public static void SetPieceSnow(ZDOID id, float snowCover, float wetScale) { if (!Inert) EasyBake.EbmSnowStore.SetPiece(id, snowCover, wetScale); }

        // The same, answering whether EBM took it (false = EBM does not draw that piece: keep it live).
        public static bool TrySetPieceSnow(ZDOID id, float snowCover, float wetScale) => !Inert && EasyBake.EbmSnowStore.SetPiece(id, snowCover, wetScale);

        // ── 1.2.437 (Vedr snow redesign step 4, agreed with [seasons]): per-piece snow SHAPE; the amount comes from Vedr's _VedrSnowA / _VedrSnowAOn globals ──
        // encoded: 0 = no snow; 0..1 = winter ground, value = shelter; 2..3 = snow base, value - 2 = shelter (clamped to 0..3).
        // EBM/Piece uses it while _VedrSnowAOn > 0.5: cover = saturate(shelter) x fill (inside the piece field rect the field's per-pixel shelter
        // replaces the stored one), else the SetPieceSnow cover as before. A shape change rewrites only that piece's per-instance value (.z),
        // and only when it changed: it never re-splits, rebakes or counts as an amount push. Main thread. Same piece rules as SetPieceSnow.
        public const int PieceSnowShapeVersion = 1;
        public static void SetPieceSnowShape(ZDOID id, float encoded) { if (!Inert) EasyBake.EbmSnowStore.SetShape(id, encoded); }
        // Shape mode: a baked piece with no pushed shape asks shapeAt(piece pivot) once, at its first look write after its zone is announced
        // (negative / NaN = no answer: the piece keeps the SetPieceSnow path). Null withdraws. Re-registering replaces; every announced group re-asks.
        public static void RegisterPieceSnowShapeSource(Func<Vector3, float> shapeAt) { if (!Inert) EasyBake.EbmSnowStore.RegisterShapeSource(shapeAt); }

        // Snow-cap values for a piece: stored only, EBM does not draw caps yet. False for a piece EBM does not draw.
        public static bool SetCapSnow(ZDOID id, float level, Vector4 edges, Vector4 neighbours) => !Inert && EasyBake.EbmSnowStore.SetCap(id, level, edges, neighbours);

        internal static void RaiseZoneBaked(Vector2s zone, ZDOID[] ids)
        {
            var h = ZoneBaked;
            if (h == null) return;
            try { h(new Vector2i(zone.x, zone.y), ids); } catch (Exception e) { EasyBakeLog.Warn("[EbmApi] a ZoneBaked listener threw: " + e.Message); }
        }

        internal static void RaiseZoneUnloaded(Vector2s zone)
        {
            var h = ZoneUnloaded;
            if (h == null) return;
            try { h(new Vector2i(zone.x, zone.y)); } catch (Exception e) { EasyBakeLog.Warn("[EbmApi] a ZoneUnloaded listener threw: " + e.Message); }
        }

        // ── 1.2.208: what Core (or any mod) may plug INTO EBM. EBM never looks anyone up; nothing registered = EBM's own defaults. Main thread. ──
        public static void RegisterLineFit(Func<string, int, string> fit, Func<int> consoleColumns, Func<string, int> prefixColumnsFor)
        { CoreLink.LineFit = fit; CoreLink.ConsoleColumnsSource = consoleColumns; CoreLink.PrefixColumnsSource = prefixColumnsFor; }
        public static void RegisterBakeExclusion(Func<UnityEngine.GameObject, bool> isExcluded) => CoreLink.BakeExclusion = isExcluded;
        public static void RaiseBakeExclusionsChanged() => CoreLink.RaiseExclusionsChanged();
        // [seasons] 11-11 (WaterHideBuriedTiles, add-only): EBM may stop drawing a zone water tile that lies wholly under the terrain. A mod with its own
        // underground space or water there (FAT's voxel caves / cave rivers) registers a veto: given the tile's world bounds, return true to keep that tile
        // drawn. Asked on EVERY hide decision (never cached), so a cave dug later is honoured at the tile's next re-test (terrain change or backstop).
        // A veto that throws keeps the tile. Main thread. Registering the same delegate twice adds it once.
        public static void RegisterWaterHideVeto(Func<Bounds, bool> keepVisible) => EasyBake.WaterHideBuried.AddVeto(keepVisible);
        public static void RegisterGravityUp(Func<UnityEngine.Vector3, UnityEngine.Vector3> up) => CoreLink.GravityUp = up;
        public static void RegisterFrameTimer(Action<string, long> record, Func<bool> enabled) { CoreLink.FrameTimer = record; CoreLink.FrameTimerOn = enabled; }
        // ── 1.2.208 (lead / [wishbone]'s audit: Core, Vedr and FDT patched or read EBM internals): stand-in facts, read-only ──
        // Raised on the main thread when a zone's stand-in colliders have all been built (zone, how many stand-ins it now has).
        public static event Action<Vector2i, int> StandInsChanged;
        internal static void RaiseStandInsChanged(Vector2i zone, int built)
        {
            try { StandInsChanged?.Invoke(zone, built); } catch (Exception e) { EasyBakeLog.Warn("[EbmApi] a StandInsChanged listener threw: " + e.Message); }
        }
        // The prefab hash of the piece this collider stands in for, or 0 when it is not one of EBM's stand-ins.
        public static int StandInPrefab(UnityEngine.Collider c)
        {
            if (c == null) return 0;
            var mark = c.GetComponentInParent<EbmStandIn>();
            return mark != null ? mark.PrefabHash : 0;
        }
        // True when EBM chose not to create this piece (it is drawn baked; its ZDO stays as the server has it).
        public static bool IsSkipped(ZDOID id)
        {
            if (Inert) return false;
            var zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(id) : null;
            return zdo != null && EasyBake.ZoneTracker.IsSkipped(zdo);
        }

        public static void RegisterDebugSwitch(Func<bool> debugOn) => EasyBakeLog.SetDebugSwitch(debugOn);
        public static void RegisterTrace(Action<string, int, string> step) => EasyBake.EbmTrace.Registered = step;
        public static void RegisterSnowBase(Func<UnityEngine.Vector3, float> baseAt) => CoreLink.SnowBase = baseAt;

        // what EBM offers to pull
        public static string StatusLine() { try { return CoreLink.StatusLine?.Invoke() ?? ""; } catch { return ""; } }
        public static BepInEx.Configuration.ConfigFile Config => CoreLink.Config;
        public static bool IsAdvancedKey(BepInEx.Configuration.ConfigDefinition def) { try { return CoreLink.AdvancedKey != null && CoreLink.AdvancedKey(def); } catch { return false; } }
        // EBM's help pages as plain text sections (group, title, order, adminOnly, text)
        public static System.Collections.Generic.List<(string group, string title, int order, bool adminOnly, string text)> HelpSections()
        {
            var list = new System.Collections.Generic.List<(string, string, int, bool, string)>();
            foreach (var s in CoreLink.HelpSections)
            {
                var sb = new System.Text.StringBuilder();
                try { s.build(new CoreLink.HelpWriter(sb)); } catch (Exception e) { sb.AppendLine("(help section threw " + e.GetType().Name + ")"); }
                list.Add((s.group, s.title, s.order, s.adminOnly, sb.ToString()));
            }
            return list;
        }

        // ── 1.2.210 ([ghost], Core's Far systems moved into EBM): what other mods push INTO EBM's far view. EBM looks nobody up. Main thread. ──

        // Far snow (the far-building chunks and the impostor engine). The snow owner (Vedr) registers once, and again with nulls to leave:
        //  active()        true while its season snow is live (re-read every check, never cached);
        //  coverAt(pos)    piece snow cover 0..1 at a world point; exposureAt(top) 0..1 how open the top of a piece is;
        //  revision()      bumps when the snow changes (far chunks re-quantise their snow then; null = never);
        //  worldSnow01()   one 0..1 amount for the impostors' snow, negative while not ready (null = altitude snow only).
        // Nothing registered: far buildings draw without season snow, impostors use altitude snow on mountain heights.
        public static void RegisterFarSnow(Func<bool> active, Func<Vector3, float> coverAt, Func<Vector3, float> exposureAt, Func<int> revision, Func<float> worldSnow01)
        { if (!Inert) EasyBake.Far.FarSnow.Register(active, coverAt, exposureAt, revision, worldSnow01); }

        // VA distant skip (FAP): a prefab FAP's own distant method already shows out to maxDistance metres is not drawn by EBM's far buildings
        // inside that distance (no double draw). maxDistance <= 0 removes it. Re-register freely (last value wins).
        public static void RegisterFarSkip(string prefabName, float maxDistance)
        {
            if (Inert || string.IsNullOrEmpty(prefabName)) return;
            if (maxDistance <= 0f) EasyBake.Far.FarBuildingDraw.VaDistantReach.Remove(prefabName);
            else EasyBake.Far.FarBuildingDraw.VaDistantReach[prefabName] = maxDistance;
        }

        // FAP 1.1.411's names for the same table (built against [visual]'s earlier name): one VA-distant prefab drawn out to distance metres;
        // ClearDistantDrawn forgets them all (FAP clears, then re-sends its list).
        public static void SetDistantDrawn(string prefab, float distance) => RegisterFarSkip(prefab, distance);
        public static void ClearDistantDrawn() { if (!Inert) EasyBake.Far.FarBuildingDraw.VaDistantReach.Clear(); }

        // 1.2.341 (G2 of the far-drawing plan, FAP 1.1.426): FAP's runtime-baked blueprints (VA_BakeParent) by CACHE NAME, so EBM's far tier can draw them.
        // meshesByCacheName / materialsByCacheName: index-aligned, one entry per part, parent-local, scale 1, the coarsest LOD per zone key, opaque / cutout
        // used; null = not in memory (FAP queues one paced rebake and calls RaiseFarBakeReady when it lands). Main thread, synchronous, no instantiation.
        public const int FarBakeSourceApiVersion = 1;
        public static void RegisterFarBakeSource(Func<string, UnityEngine.Mesh[]> meshesByCacheName, Func<string, UnityEngine.Material[][]> materialsByCacheName)
        { if (!Inert) EasyBake.Far.FarBakeSources.Register(meshesByCacheName, materialsByCacheName); }
        // FAP: a bake landed, was restored, changed or was removed (main thread); EBM reads that name again.
        public static void RaiseFarBakeReady(string cacheName) { if (!Inert) EasyBake.Far.FarBakeSources.Ready(cacheName); }

        // True while EBM's impostor engine draws (this is a client and [Far Impostors] Enabled is on). Cells a feeder sends while it is false are kept
        // (as Core did) and draw once it turns on; a feeder may read this to skip building cells nobody can see.
        public static bool FarForestLive => EasyBake.Far.FarRunner.Client && EasyBake.Far.ImpostorEngine.Enabled;
        // A mod that draws far buildings itself asks EBM's far buildings to step aside (why = the reason logged); null / empty why withdraws it.
        public static void RequestFarBuildingsStandDown(string owner, string why) { if (!Inert) EasyBake.Far.FarBuildings.RequestStandDown(owner, why); }

        // Far forest (FAT): feed EBM's impostor engine with plain values, no engine types needed. ApiVersion of this group:
        public const int FarForestApiVersion = 1;
        // Register a tree prefab as an impostor kind for an owner; distant = the prefab's ZNetView.m_distant. Returns the kind id, or -1.
        public static int FarForestRegisterKind(string owner, GameObject prefab, bool distant)
        {
            if (Inert) return -1;
            var o = EasyBake.Far.KindOptions.Default;
            o.MidLodSource = EasyBake.Far.MidLodSource.AuthoredFarLod;
            o.Distant = distant;
            o.ReachScale = 1f;
            return EasyBake.Far.ImpostorEngine.RegisterKind(owner, prefab, o);
        }
        // One cell's trees, packed 6 floats each: x, y, z, scale, yaw (radians), kind. count = trees in the array (count x 6 floats used).
        public static void FarForestSetCell(string owner, long cellKey, Bounds bounds, float[] packed, int count)
        {
            if (Inert) return;
            if (packed == null || count <= 0) { EasyBake.Far.ImpostorEngine.ClearCell(owner, cellKey); return; }
            count = Math.Min(count, packed.Length / 6);
            var arr = new EasyBake.Far.ImpostorInstance[count];
            for (int i = 0, p = 0; i < count; i++, p += 6)
                arr[i] = new EasyBake.Far.ImpostorInstance(new Vector3(packed[p], packed[p + 1], packed[p + 2]), packed[p + 3], packed[p + 4], (int)packed[p + 5]);
            EasyBake.Far.ImpostorEngine.SetCell(owner, cellKey, bounds, arr);
        }
        public static void FarForestClearCell(string owner, long cellKey) { if (!Inert) EasyBake.Far.ImpostorEngine.ClearCell(owner, cellKey); }
        // A zone whose real trees are standing: the impostors there hand off (only ever makes a zone stand sooner; EBM stays the authority).
        public static void FarForestMarkZoneStanding(Vector2s zone) { if (!Inert) EasyBake.Far.ImpostorEngine.MarkZoneStanding(zone, true); }

        // ── 1.2.210 ([ghost], lead: "EBM relies on no other mod"): the remaining values EBM used to look up BY NAME, now PUSHED by their owners.
        // Each has the same "nothing pushed" answer the old lookup gave when that mod was absent. Main thread. ──

        // Season mod (Vedr): the biome mask in which buildings carry snow (0 outside winter / snow on buildings off). Push at bind and on change.
        public static void SetSnowBuildingBiomeMask(int mask) { if (!Inert) VedrLink.PushBiomeMask(mask); }
        // Season mod: whether a piece prefab gets a snow cap when upright: 1 yes, 0 never, -1 undecided. EBM bumps its own revision per change.
        public static void SetPieceSnowCap(string prefab, int capFor) { if (!Inert) VedrLink.PushCap(prefab, capFor); }
        // Season mod: its season / stage or snow masks changed (where its own VisualsChanged fires).
        public static void RaiseVisualsChanged(int season, int stage) { if (!Inert) VedrLink.RaiseVisualsChanged(season, stage); }
        // Season mod: true while it owns indoor wet on its own piece shader (EBM's indoor dry then stands down for those renderers).
        public static void SetIndoorWetOwner(bool seasonModOwnsIndoorWet) { if (!Inert) EasyBake.WetOverride.PushIndoorWetOwner(seasonModOwnsIndoorWet); }

        // EBM's indirect material clones of vegetation / rock materials, for whoever writes season looks onto the source materials (copy them on).
        // Subscribing replays every clone that exists now. Raised on the main thread.
        private static readonly System.Collections.Generic.Dictionary<Material, Material> s_vegClones = new System.Collections.Generic.Dictionary<Material, Material>();
        private static Action<Material, Material> s_vegCreated;
        public static event Action<Material, Material> VegetationCloneCreated
        {
            add
            {
                s_vegCreated += value;
                foreach (var kv in s_vegClones) { try { value(kv.Key, kv.Value); } catch (Exception e) { EasyBakeLog.Warn("[EbmApi] a VegetationCloneCreated listener threw: " + e.Message); } }
            }
            remove { s_vegCreated -= value; }
        }
        public static event Action<Material> VegetationCloneReleased;
        internal static bool AnnounceVegetationClone(Material clone, Material source)
        {
            if (clone == null || source == null) return false;
            s_vegClones[clone] = source;
            var h = s_vegCreated;
            if (h != null) foreach (Action<Material, Material> one in h.GetInvocationList()) { try { one(clone, source); } catch (Exception e) { EasyBakeLog.Warn("[EbmApi] a VegetationCloneCreated listener threw: " + e.Message); } }
            return true;
        }
        internal static void AnnounceVegetationCloneReleased(Material clone)
        {
            if (clone == null || !s_vegClones.Remove(clone)) return;
            try { VegetationCloneReleased?.Invoke(clone); } catch (Exception e) { EasyBakeLog.Warn("[EbmApi] a VegetationCloneReleased listener threw: " + e.Message); }
        }

        // Baked piece snow source. For each role the highest-priority registration that provides it answers (agreed numbers: Vedr 30,
        // TossinShade 20, Core 10). Any role may be null. All roles null = withdraw that owner. Re-registering replaces.
        //   active()            this source's snow is live (any live source turns EBM's baked snow on)
        //   coverAt(pos)        0..1 area snow cover; exposureAt(top) 0..1 how open a piece's top is (null = vanilla Cover.IsUnderRoof)
        //   fieldActive()       a per-pixel shelter field is drawn by the swap shader (baked instances then take area cover only)
        //   applyPieceShader(prefab, why) swaps a prefab's piece materials to the source's shader (returns materials swapped)
        public static void RegisterPieceSnowSource(string owner, int priority, Func<bool> active, Func<Vector3, float> coverAt, Func<Vector3, float> exposureAt,
                                                   Func<bool> fieldActive, Func<UnityEngine.GameObject, string, int> applyPieceShader)
        { if (!Inert) EasyBake.SnowFeed.Register(owner, priority, active, coverAt, exposureAt, fieldActive, applyPieceShader); }
        // The source's snow changed everywhere (its revision moved): every loaded group re-feeds its snow.
        public static void RaisePieceSnowChanged() { if (!Inert) { EasyBake.EbmSnowStore.NoteAmountPush(); EasyBake.SnowFeed.RaiseChanged(); } }   // 1.2.437: counted (shape mode line)
        // 1.2.259 (Fire 11-10, DN in Summer): a snow / season owner calls this on a season change (or any time its snow may have moved): every
        // baked group re-reads its snow and every far chunk rebuilds with the snow read now. One Info line with the reason. Name locked with [seasons].
        public static void InvalidateSnow(string reason) { if (Inert) return; EasyBake.EbmSnowStore.NoteAmountPush(); EasyBake.EbmSnowStore.DropAllAndReannounce(); EasyBake.SnowFeed.Refresh("InvalidateSnow: " + (string.IsNullOrEmpty(reason) ? "no reason given" : reason), true); }   // 1.2.260: pushed per-piece values are dropped and every zone re-announced first
        // 1.2.259: optional. The piece snow source's revision int; EBM reads it once a second ([Snow] SnowRefreshPoll) and re-reads all snow when it moves.
        public static void RegisterPieceSnowRevision(Func<int> revision) { if (!Inert) EasyBake.SnowFeed.RegisterPieceRevision(revision); }
        // The source's snow changed inside one area of a zone (wholeZone = the whole zone): only the groups it touches re-split.
        public static void RaisePieceSnowAreaChanged(Vector2s zone, Rect area, bool wholeZone) { if (!Inert) { EasyBake.EbmSnowStore.NoteAmountPush(); EasyBake.SnowFeed.RaiseAreaChanged(zone, area, wholeZone); } }   // 1.2.437: counted

        // A snow mod's piece shader that swaps the game's Custom/Piece (and solid Standard) in place: EBM learns the swap up front, while
        // active() is true (re-read every attempt). ensureLoaded may be null (then the shader is found by name). Null shaderName = withdraw.
        public static void RegisterPieceShaderSwap(string owner, string swapShaderName, Func<bool> active, Func<Shader> ensureLoaded)
        { if (!Inert) EasyBake.ModdedPrefabGate.RegisterSwap(owner, swapShaderName, active, ensureLoaded); }

        // Prefab manager (FAP): the asset bundle a modded prefab came from (null / empty = unknown), for EBM's refusal lines.
        public static void RegisterPrefabSourceLookup(Func<string, string> sourceBundleOf) { if (!Inert) EasyBake.ModdedPrefabGate.RegisterSourceLookup(sourceBundleOf); }

        // Terrain mod (FAT, halo ring): object-sector keys while a ring world is on. All four, or nulls to withdraw.
        public static void RegisterHaloZones(Func<bool> on, Func<Vector3, Vector2s> zoneOf, Func<Vector2s, Vector3> zonePos, Func<Vector2s, Vector2s, Vector2s> delta)
        { if (!Inert) EasyBake.EbmZones.Register(on, zoneOf, zonePos, delta); }

        internal static void SetPieceShaderActive(bool on)
        {
            if (PieceShaderActive == on) return;
            PieceShaderActive = on;
            EasyBakeLog.Info($"[EbmApi] PieceShaderActive = {on}");
            try { PieceShaderActiveChanged?.Invoke(); } catch (Exception e) { EasyBakeLog.Warn("[EbmApi] a PieceShaderActiveChanged listener threw: " + e.Message); }
            RecheckShaderOwnership("PieceShaderActive changed", true);
        }
        /// <summary>1.2.428: the drawn wave strength near the shore (0 at the shoreline .. 1 from 10 m of water) at a world xz, the CPU twin of EBM/Water's shore field (same values, rect, bilinear and edge fade). false = the field is off, not baked yet, written by FiresTossinShade instead, or the point is outside it (draw = full waves). Main thread.</summary>
        public static bool TrySampleShoreWave(float x, float z, out float wave) => EasyBake.EbmFtsGlobals.Shore.TrySampleWave(x, z, out wave);
    }
}
