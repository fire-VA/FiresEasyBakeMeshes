using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System.Collections;
using UnityEngine;

namespace FiresEasyBakeMeshes
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency("com.Fire.FiresUnifiedCore", BepInDependency.DependencyFlags.SoftDependency)]   // 1.2.111: was 0.2.35; links back to Core 0.2.219, run and tested on 0.2.586+ (the Core the release zip pins)
    // Runs after GameCamera, which moves the camera in its own LateUpdate, so the instanced
    // draw tests the view the frame is about to be rendered from.
    [DefaultExecutionOrder(1000)]
    public class FiresEasyBakeMeshesPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.Fire.FiresEasyBakeMeshes";
        public const string PluginName = "FiresEasyBakeMeshes";
        internal static ConfigEntry<bool> LightSettingsEvent, MinimapFogPartialUpload, CreateDestroyDirtyFlag, LightFlickerViewCull;   // 1.2.172
        public const string PluginVersion = "1.2.262";   // 1.2.262 (far snow caps back as their own parts, shown only for a copy over 0.25 build-up in Mountain/DeepNorth or where the season source has snow, Fire 11-10; impostor cards leave caps out). 1.2.261 ([lead] 11-10: far buildings no longer merge vanilla's snow cap meshes, which are active in every piece prefab, so far copies were white in Summer; FarBuildingSources.PickRenderers). 1.2.260 (baked snow: wait up to 3 s for the per-piece answer instead of drawing the area amount; InvalidateSnow drops pushed values and re-announces; [Snow] WaitForPushedSnow ON). 1.2.259 (snow re-read: revision poll 1/s, at world entry + 10 s, EbmApi.InvalidateSnow; [Snow] SnowRefreshPoll ON). 1.2.258 (render / bake-proof / shadow census, sky-depth pass, far cap hysteresis + pooled merge; all keys off). 1.2.257 (per-object create allocations removed); was 1.2.256:   // 1.2.256: far hand-off counts skipped pieces (FarHandoffCountsSkipped); a graphics-driver crash on the GPU path falls back for one session (IndirectCrashFallback); was 1.2.255:   //1.2.255 (zone cache checks: changed models, demolished zones, size caps, checksum; far cache writes); was 1.2.254: 1.2.254 (audit part 2: StreamPacerExtraPasses, TickGroupEventDriven, IndirectCellReusePerCell, draw-source split); was 1.2.253: 1.2.253 (fable circle audit): four keys, all default off: LateJoinFromZdo, RemoteZoneValidation, StreamPacerVanillaFloor, SaveOffThread; was 1.2.252:   // 1.2.252 (Fire 11-09, the circle test): the create pacer knows a player who moves without a rigidbody velocity (StreamPacerMotionByPosition); was 1.2.251:   // 1.2.251 ([seasons] pixel shave): IndirectPiecePixelShave, default off; was 1.2.250:   // 1.2.250 (Fire's SP split, plan rows #1-#10): FGN's client object streaming moved in (one pacer, idle / event object area, spread removal order, teleport gate, distant prefabs, diagnostics); far ring lap keeps its place; was 1.2.249:   // 1.2.249 (Fire 11-09, the VPO gap): Far buildings polls far less when nothing changed (FarBuildingsQuietPolling); [wishbone] + [worldgen] fixes; was 1.2.248:   // 1.2.248 ([generator] review): a source with a failed part is not cached and fails for this session only; was 1.2.247:   // 1.2.247 (Fire 11-08: stretched only with far buildings on): far reads leave shared meshes' buffer targets alone (Raw lost non-readable meshes' index buffers), [saddles]'s far fixes; was 1.2.246:   // 1.2.246 ([generator] review): readable meshes compared to their CPU copy, honest lap counts; was 1.2.245:   // 1.2.245 ([visual] 11-08): GPU mesh validator + FarBuildings Raw probe, off by default; was 1.2.244:   // 1.2.244 ([generator] review): every batch gets the debug radius, the sweep skips a moved snapshot, a bad cascade read cannot skip rows, cascade counts on their own key. 1.2.243 ([visual] 11-08, Fire: the stretch is in the camera draw): IndirectVertexDebug paints bad vertices; ebm_batchread reads a batch back from the GPU. 1.2.242 ([visual] 11-08): a [Cascades] line counts what each sun cascade really draws; IndirectCascadeEmptySkip leaves out rows that stayed empty. 1.2.241 ([visual] 11-08, Fire: everything in one round on keys): IndirectCellsJob and IndirectDrawParamsCache, off until measured; a big rock is no longer judged broken. 1.2.240 ([visual] 11-08): the phases line counts shadow draws; [SnowCap] lines and ebm_snowcaps show what turns snow caps on; the integrity and mesh content checks default off. 1.2.239 ([visual] 11-08, Fire: find what is wrong with the stretched batches): ebm_bisect next keeps found batches hidden; FOUND and ebm_batches say how a batch is built; an unusual scale is no longer red. 1.2.238 ([visual] 11-08, Fire: narrow down what mesh caused problems and why): ebm_bisect, ebm_batches, ebm_meshhash; a broken covered piece stays with Unity, a broken baked one touches no bounds or cells; saved caches stay readable (v15). 1.2.237 ([visual] 11-08, lead): a raw axis scale only logs and still draws (Balrond rigs are 100x); hidden = no volume, not finite, or a world box over 150 m, every distinct object named. 1.2.236 ([visual] 11-08, Fire: the stretch is EBM reading back saved meshes): cache v16, so every zone bakes fresh once; each saved mesh and instance is validated on read and a bad one rejects its zone, which rebakes. 1.2.235 ([visual] 11-08, Fire's live test: the stretch is persistent source data): an instance with an axis scale > 10 or < 0.05, a near-zero determinant or a > 150 m world box is NOT drawn and named (mesh + prefab / position); [Integrity] mesh CONTENT check (8 GPU windows per batch mesh, hashed; Diagnostics.IndirectMeshContentCheck).  // 1.2.234 ([visual] 11-08, lead: the near stretch is intermittent by load order): every mesh EBM builds is checked once (16-bit over 65,535 vertices, an index past the vertices, a non-finite vertex): a bad one is never drawn + Error; cache rebuilds are never 16-bit over 65,535 vertices.  // 1.2.233 ([visual] 11-08, lead: setpass ~= draws, the lever is fewer draws): cascade caster size skip (IndirectCasterSizeSkip / IndirectCasterMinScreen): batches too small from cascades 2-4's near split are not recorded there; merged [ghost] GC fixes (far digest / plan scratch reuse, pooled mirror sector sets).  // 1.2.232 ([visual] 11-08): StreamPacer arrival runs vanilla (1229: spawned at world spawn) and the key defaults OFF until proven; stretch evidence: [Integrity] also checks the world-to-object rows + the looks buffer, a non-finite instance matrix logs an Error, far-building chunks are validated before upload (a bad one is not drawn + Error).  // 1.2.231 ([visual] 11-08): EbmApi.StreamPacerReady (live: installed + key on + not faulted, not a dedicated server) for FGN 1.6.79's strip gate.  // 1.2.230 ([visual] 11-08): looks change-driven really skips: with no snow / wet / pushed looks the prep is a no-op whatever the never-cleared dirty flag says (1227: 0 skips, "snow dirty" on every take).  // 1.2.229 ([visual] 11-08, GC: Mono's full-heap collections): managed KB/s per EBM entry point + plugin-Update group on the own-cost line; [Heap] census of EBM's big holders every 60 s; pacer drops unloaded zones from its filling set.  // 1.2.228 ([visual] 11-08, Fire's SP split): EBM create pacer (StreamPacer: event queue, adaptive frame budget, distant window, extra pass only with work; EbmApi streaming events); [ghost] item-stand signature fix (stand3).  // 1.2.227 ([visual] 11-08): the [Draw] line names why looks were not skipped (first failing check); the 30 s coverage census (~30 ms walk) only with Diagnostics.CoverageCensus.  // 1.2.226 ([visual] 11-08): 1.2.225's copies threw (scratch buffers lacked CopySource / CopyDestination): fixed; a cascade-prep throw now latches same-frame off for the session (Error); [Integrity] GPU-vs-CPU check of the args + instance buffers (the stretch evidence).  // 1.2.225 ([visual] 11-08): THE STRETCH, found in RenderDoc: the shadow-pass cascade cull (G6) UAV-wrote the camera-read _EbmVisible buffer mid-frame; it now writes its own scratch buffer and copies it into a casters-only buffer. Camera indirect submits in shader/material/mesh order. Merged [ghost]: sliced sector-mirror rebuild (the 115 ms once-a-minute hitch) + physics census. LooksChangeDriven skip count on the [Draw] line.  // 1.2.224 ([visual] 11-08): THE STRETCH FIX: G5 cascade sphere capture removed (its shadow-pass pixel-shader UAV write corrupted piece geometry for the session; Fire's O bisect); the rest of plugin Update timed (heartbeat, preload kick, sector mirror) + an untimed-rest figure; the worst zone tracker frame keeps its phase split.  // 1.2.223 ([visual] 11-08): IndirectLooksChangeDriven (looks prep + write only on change); coverage reach pass capped at ~1 ms a frame; the plugin-Update fill/reach/census group timed apart; [Fill] says when far buildings drew before the spawn.  // 1.2.222 ([visual] 11-08): plugin Update split by tick group (worst frame named on the own-cost line); a 30 s average [Draw] split; cell reuse lets the sun drift 0.25 deg (~1.25 s outdoors); [ghost] item-stand churn fix (re-cover only when the stand shows another item: 6k re-covers per 30 s standing still) + EffectArea destroy census.  // 1.2.221 ([visual] 11-08): [seasons] EBM/RugIndirect variant for Custom/Rug behind IndirectRugVariant (default off).  // 1.2.220 ([visual] 11-08): IndirectCellReuse (the CPU cell walk keeps last frame's verdict while the camera barely moved and nothing changed; test widened 2 m so it stays a superset); [generator] fix: far Arrival budget client only and at most 90 s (a dedicated server ran it forever); IndirectLodPerInstance ack line (what a LOD merge can join on this world).  // 1.2.219 ([visual] 11-08, Fire: "we take 3 min to settle", VPO ~1 min): [Fill] line per arrival (time to 95 % / 99.5 % of the settled instance count, the curve, far buildings first drawn at T+); far buildings get an Arrival budget (6 ms, under the loading screen / teleport / until the fill settles) so distant builds draw from the first seconds; [EBM] own-cost line (ms a frame of each EBM Update / LateUpdate entry point); ebm_opt live key for IndirectCoverageLodLevels.  // 1.2.218 ([visual] 11-08): the once-per-30 s 77 ms frame found = the Debug renderer census's FindObjectsByType (IndirectStats + [ghost]'s CasterCensus), now opt-in ([Diagnostics] RendererCensus, default off); [ghost] P3 coverage LOD levels (IndirectCoverageLodLevels, default off, needs IndirectCoverageReach): each LOD level holds instance slots only near its own distance window, with hysteresis.  // 1.2.217 ([visual] 11-08): diagnostics only - [ghost] census2 (the coverage census total says it counts parked objects at drawn size; a "part on/off re-covers by prefab" top-12 line for the ~200 objects a second whose parts toggle while standing still).  // 1.2.216 ([visual] 11-08, Fire's O run on 1.2.215): the slow [Draw] frames are not GC, so each slow frame is now split by sub-timer (dead-group checks / Take incl. sync + looks prep + looks write / legacy draws / indirect EndFrame) and the worst one's split logged; DestroyLeanEarmark default ON (134,545 ZDOs stamped a call, ~0.94 ms a frame) with [generator]'s session self-check (another mod's keep-alive ZDOs in the near list = lean refused, named); [ghost] location-prop coverage (IndirectCoverageLocationProps, default off).  // 1.2.215 ([visual] 11-08): [ghost] coverage churn fix (vanilla MineRock's 10 s UpdateVisability timer re-covered every rock, changed or not: 10-14k adds + removes per 30 s standing still at 615 and their garbage; every vanilla hook now re-covers only on a real change, hand-backs counted by reason); [visual] DestroyLeanEarmark (default off: the zone-leave pass stamps only the keepalive tail and asks each leaving candidate its live sector instead of stamping ~134k ZDOs a call).  // 1.2.214 ([visual] 11-08, Fire's O run on 1.2.213 at 615): IndirectCellFastSkip (exact: an entry whose batch is already marked in every view it could add to is skipped before its distance tests; a cell wholly inside a camera view skips its entries' plane tests; the walk grew to 1.77 ms at 247k); [Draw] slow-frame line (frames > 50 ms, with / without a GC inside the draw timer: the 130-153 ms frames are outside every indirect phase); [DTS] and [CDS] 30 s census lines; fused cell bucket named; [ghost] P1 coverage census + P2 reach gate (IndirectCoverageReach, default off).  // 1.2.213 ([visual] 11-08, the O slide 98 -> 32 fps as coverage grew): [visual] IndirectGpuGrow (instance buffer growth copied on the GPU, no CPU re-upload: the 131 ms frames) + IndirectCompactSlots (lowest free slot first, s_high trimmed); [saddles] IndirectFusedCells (one CPU cell walk for camera+sun / cascades / lights / faces, Compact every 5 s), IndirectCachedAlive (one Unity-null check per batch per frame), IndirectLocalSteady (no light-buffer churn on a one-frame drop); [seasons] IndirectGrass (EBM GPU grass, default off).  // 1.2.212 ([visual] 11-08, lead): per-phase main-thread timers for the indirect path (Debug, every 30 s: group walk / layout / upload / cull + cells per view kind / draw + records / other cameras / group sync, worst frame and its phase, adds / removes); [Far Impostors] default back to OFF (Fire parked impostors; [Far buildings] stays ON); FarBuildings text fixed; ebm_opt says (live) / (next load) per key; G5 hysteresis flag set only once the capture is used; [ghost] safe patching (types read with AccessTools, a class that fails half-way is rolled back, the innermost reason logged).  // 1.2.211 ([visual] 11-08, Fire's O run on 1.2.210): G5 fixed as ONE implementation ([seasons] v2 main-camera-only capture with exact frame-stamped age + [visual] hysteresis; IndirectExactCascadeSpheres now default OFF for the A/B); far tier ON by default ([Far buildings] + [Far Impostors]); IndirectCoverageDisableRenderers default ON; ebm_opt <Key> on|off|status|list for every [Optimize] on/off key (live, no cfg write); a shader variant the GPU cannot run stays vanilla; cull count clamp on every append path; [wishbone] G3 grouped cull (IndirectCullGrouped), G4 one cull pass (IndirectOneCullPass), LOD window per instance (IndirectLodPerInstance), all default off; [saddles] G6 v2 same-frame receiver map + cascade cull in the shadow pass + shadow placeholders (IndirectShadowSameFrame, per his notes).  // 1.2.210 ([visual] 11-08, from the full VPO study, our own methods, each an [Optimize] A/B key read live): IndirectSparseUpload (changed instance slots only, no dirty-span uploads); IndirectStableLayout (a batch that outgrows its range moves alone, args rows by a power-of-two batch capacity, no full rebind / re-record); IndirectCasterCpuCull (per-cascade + per-light CPU cell lists, material order, re-record on a gained batch or a quarter stale; extra cameras CPU-culled + culling mask); IndirectPointFaces (6 face buffers per point light + caster depth bias); incremental group Sync + capacity headroom; LOD start in the CPU cells; batch key + receiveShadows / renderingLayerMask; [ghost] covered renderers out of Unity's culling (IndirectCoverageDisableRenderers, ebm_coverhide); [seasons] G1/G2 EBM/Piece alpha-test variant + texture branches + GPU min-alpha, G5 exact cascade spheres (IndirectExactCascadeSpheres); [wishbone] renderer counter split; [fgn] sector mirror confirms "empty" against live movers; Core Far systems + all lookups turned into EbmApi pushes ([generator] / [ghost]).  // 1.2.209 ([visual] 11-08, Fire: every VPO renderer method in ONE build, our own code): other cameras get their own culled view 6 (reflection probes, sky depth, planar water; main draws now camera = main); [wishbone] point / spot light casters (views 7..10, the 4 nearest LightLod-shadowed lights); [saddles] shadow receiver map (casters must shadow a visible receiver; previous-view keep, no pop on turns) + per-cascade size cut (IndirectShadowSizeCut 2 texels); [ghost] live interactive Piece + dropped ItemDrop coverage and EBM/StandardIndirect (+ Specular); IndirectMinScreenSize default 0.1; [generator] review: a batch going 0 -> 1 re-records the cascade / light casters.  // 1.2.208 ([visual] 11-08): STANDALONE part 1 (FGN link removed; Core link push-only through EbmApi Register*; no Core scheduler / FDT / FAT lookups); per-renderer LOD parts for the indirect path (every LODGroup level's renderer with its own window, [generator] item 3) + single-mesh chains cull (item 2) + "[low]" combined twins never drawn; [Optimize] IndirectCascadeShadows (per-cascade GPU cull, casters from the sun's command buffers, default off); 30 s GPU readback of post-cull drawn counts (Debug); per-prefab LOD Debug line; EbmApi StandInsChanged (built + removed) / StandInPrefab / IsSkipped; [wishbone] snow follow-up.  // 1.2.207 (lead 11-08, Fire: EBM's idle CPU counts in the head-to-head): ZoneTracker's checks and bake-decision walk run at 10 Hz while no zone has queued work (every frame while one does), cutting the idle zoneTrack cost; [wishbone]'s EbmApi snow entry points (ZoneBaked / ZoneUnloaded events with ZDOIDs, re-announced at most 1/s on a group change; SetPieceSnow / TrySetPieceSnow into EBM's own store, read by both draw paths; SetCapSnow stored; AcceptsSnowValues), so Vedr pushes in and EBM never calls Vedr.  // 1.2.206 ([visual] 11-08): [Optimize] IndirectCoverage (default off): [ghost]'s live trees / rocks / destructibles / pickables through the indirect batches (IndirectBatches API, per-instance LOD buffer, epoch-safe handles); EBM/Piece = [ghost]'s merge with all-own _Ebm* names + TossinShade's wet knob (knob values carried from _Fires* / _Vedr*); EBM's own copy of the mesh simplifier (rock LOD1 needs no Core); [Draw] times the pumps phase (zoneTrack proof).  // 1.2.205 ([classes-ui] / lead: halve A/B legs): ebm_indirect on|off|status flips [Optimize] IndirectRenderer for the session (in memory, logged "[Indirect] toggled ON/OFF by command"); [wishbone]: a group on the indirect path re-splits its snow without cell runs and skips an already-partitioned roofed-first reorder (no Drop + re-Sync per snow step).  // 1.2.204 ([visual] 11-08, Fire: "the new ebm shader needs to do everything tossin shades shader and vedrs shader does"): EBM/Piece, EBM's own piece shader (from the team's Vedr/Piece = FiresPiece + wet + winter snow) with the indirect variant, so baked PIECES go through [Optimize] IndirectRenderer too (Custom/Piece, Custom/FiresPiece and Vedr/Piece materials map to it; FiresPiece knobs copied); snow / roofed-dry ranges go indirect with per-instance looks ([wishbone] IndirectLooks, _EbmInstLooks); EbmApi (PieceShaderActive = false until EBM swaps every piece material, IndirectActive). Key still default off.  // 1.2.203 ([visual] 11-08, Fire "make our mod get us 100+", the VPO gap at 615: 99.5 vs ~36 fps): [Optimize] IndirectRenderer (default off) draws baked groups GPU-driven: one instance buffer for every loaded zone, a per-instance compute cull for the camera (frustum + the prefab's own LOD window + IndirectMinScreenPercent) and for the sun's shadow volume, one RenderMeshIndirect per batch (mesh + submesh + material + shadow mode + layer, across all zones) per view; [fgn] IndirectCells skips batches with nothing in view, [wishbone] IndirectStats gives the [Indirect] line. Needs the ebm_indirect bundle next to the DLL and a shader with the in-place variant (_EbmIndirect: FiresPiece / Vedr) or EBM's own (EBM/VegetationIndirect, EBM/StaticRockIndirect); anything else stays on the instanced path. Our own code; the cull method was learned from VPO by ontrigger (MIT, THIRD_PARTY_NOTICES.txt).  // 1.2.202 ([ghost], log-proven on the 1.2.201 ON leg: every rock keyed "___MineRock5" so ONE shared mesh (a rock4_coast) stood for all kinds and rock3 / rock1 stayed vanilla; LOD1 at 48 cells kept 77 % of the tris): the intact-rock kind = the ZDO prefab hash (name via EBM's guarded SafePrefab.Get) + the combined vertex count, never a GameObject name; key IntactRockLod1Detail (48) renamed IntactRockLod1CellsAcross, default 16 (~1.75 m cells on a 28 m rock), so the new default reaches cfgs.  // 1.2.201 ([ghost] EasyBakeIntactRockLod.cs, DESIGN_OcclusionLOD §5, lead approved 11-08; frozen by [visual]): A/B key [Optimize] BakeIntactRocks (default off = vanilla rocks): after vanilla MineRock5.UpdateMesh, an intact rock draws one SHARED copy of vanilla's own combined mesh per prefab (its private copy freed) and, with Core 0.2.1212+'s FiresCore.Far.MeshSimplify (by name), a shared simplified LOD1 below IntactRockLod1ScreenHeight (0.8) at IntactRockLod1Detail (48) cells; the first damage gives the rock vanilla's mesh and LODGroup back; colliders untouched; key off restores all. ebm_rocks prints the "[Rocks] bake" line.  // 1.2.200 (DESIGN_OcclusionLOD 2b, lead approved 11-08, TOWN50): A/B key [Optimize] InstancedInstanceCull (default off = as 1.2.199): one worker thread (EasyBakeInstanceCull.cs) tests every instance bounds sphere against the main camera frustum (fov +15 %, pushed out by camera travel) with a conservative sun sweep (kept when d(c) + 1.5 x shadowDistance x max(0, n.sunDir) >= -r on every plane, so no caster whose shadow can reach the view is dropped); the group keeps ONE compacted matrix list (+ part lists + size-classed snow arrays) and rebuilds it only when the visible set changes; a turn over 10 deg or a 20 m jump since the job draws everything that frame; other cameras keep the full lists. Debug [Draw]: part instances submitted of held, compacted rebuilds, worker ms.  // 1.2.199 (TOWN50, lead / [classes-ui] approved 11-08 after cells lost on calls): [Optimize] InstancedShadowProxies and InstancedCellLod no longer need cell runs: every draw goes through DrawRun (group bounds without cells). A shadow proxy (main draw casts nothing + one ShadowsOnly draw one LOD below what is drawn; the zone far tier counts as LOD1) is made ONLY for parts whose prefab has that lower LOD, so parts without one cost no extra call. Both keys default off = as 1.2.198. [Draw]: proxy shadow tris + shadow-only calls a frame.  // 1.2.198 ([saddles] FAP 1.1.407 / [generator] audit, 11-08): add-only event FiresEasyBakeMeshes.EbmBakedPieces.PieceLeftBake(GameObject), raised on the main thread from EasyBakeSuppressedVisuals.RestoreVisuals (the one point a live piece the bake drew gets its renderers back); Draws() now also answers false while the zone has a hand-back in flight (AwaitingRecreate), so a handed-back piece never defers. A throwing listener is removed after one Warning. Cell-mode fixes from the TOWN50 cell-16 leg (submit 4.7 -> 19.4 ms, GPU 29 -> 53 ms, GC 3 per 5 s): each snow range's instanced arrays are sized to its run (power of two 8..1023, block replaced only when the size class changes; was two 1023-float arrays per run, uploaded whole each draw); one shared cell-sort comparer (was a closure per sort); no tuple-array per re-split; the [Draw] tris split counts the zone far tier as far again (1.2.196 counted it as near: "far mesh 0k" was the proof line, not the draw).  // 1.2.197 (lead 11-08): (a) [ghost]'s read-only ebm_rocks [radius m] console report (EasyBakeRockReport.cs: every MineRock5 near the camera, intact or not, LOD now / count / transition heights, tris per LOD, shadow mode; per-prefab totals), for the DESIGN_OcclusionLOD rocks lever; no patch, no behaviour change. (b) [generator] audit: add-only FiresEasyBakeMeshes.EbmBakedPieces.Draws(GameObject piece) = the bake draws this exact piece in place of its renderers (zone baked, not held real, identity among the contributors), valid from ZNetView.Awake, so visual-only Awake hooks can return early.  // 1.2.196 (DESIGN_OcclusionLOD_20261108 2b + 2c, lead approved 11-08, TOWN50): two A/B keys on the 1.2.195 cell runs, both default off = as 1.2.195. [Optimize] InstancedCellLod: each instanced part carries its prefab's LOD chain (same rule as the old LOD1 far mesh: single renderer per level, same material + submesh, same placement) and vanilla's transition heights; each cell run draws the level vanilla's LODGroup would pick at the run's nearest point (size / (2 d tan(fov/2)) x lodBias), and nothing where vanilla culls. [Optimize] InstancedShadowProxies: the run's main draw casts no shadow and a ShadowsOnly draw one LOD lower casts it. Debug [Draw] adds instances per LOD level, LOD-culled, proxy shadow tris.  // 1.2.195 (TOWN50, [classes-ui]/lead 11-08: a baked group drew with ONE zone-sized bounds, so the camera and every sun-shadow cascade got all its instances): A/B key [Optimize] InstancedCellSizeMeters (default 0 = as before): above 0, a group orders its instances by x/z cell (inside its roofed / open snow sections, values carried along) and draws one call per cell run with that run's own bounds, so Unity culls per cell for the view and for each cascade. Same look; more calls, fewer tris. Rebuilt only on an instance or key change, buffers reused. Debug [Draw] split names the key value and instances per call next to the 1.2.194 tris split.  // 1.2.194 (Fire 0108k BlueHills city on 1.2.193 + town 50 fps target, lead 11-08): (a) a cached zone is judged against its ZDOs only once FGN settled it (never on the 60 s filling ceiling) and its ZDO count was quiet 30 s (10 s without FGN); pieces missing at both looks leave their instanced groups without a rebake (the 1.2.193 city rebaked 25 cached zones for pieces the server had not sent yet, 65k pieces created live then unloaded); only pieces merged into a combined mesh still rebake; (b) a cached zone is built at FGN ZoneFilling (before its first piece is created) so its pieces are skipped at creation; (c) the Debug [Draw] split adds tris submitted a frame (near / far mesh, shadow-casting). Proof: "[StandIn] ... gone piece(s) dropped ... rebake(s) ... cached zone(s) built at FGN ZoneFilling", "[Bake] ... instanced dropped without rebake", "[Draw] ... tris submitted".  // 1.2.193 (Fire's BlueHills arrival on 1.2.192, [visual] proof: "missing 25,423: 806 skipped, 407 would be skipped, 24,179 still to create"; city zones rebaked on "(-19,12) 5,240 contributor(s), 5,110 ZDO(s) held, 595 missing", the zone not yet all delivered, so its cache was thrown away and every piece became a live object; lead GO): (b) a cache check that finds contributors missing no longer rebakes at once: it looks again at the next settle or 30 s later and rebakes only if the SAME contributors are still missing. (a)/(c) FGN 1.6.54's zone signal (FiresGhettoNetworkMod.FgnStreaming, by reflection, used only while its live flag is on): a zone FGN says is still filling is neither judged nor baked until it settles (60 s ceiling), and its identity set is rebuilt once on settle. Without FGN: EBM's own quiet windows, as 1.2.192. Debug proof: "[Bake] Zone (x,y) cache check: N missing of C -> recheck at settle / in 30 s before any rebake", "... -> rebake (same contributors still missing: K)", "... 0 missing -> cache kept (the N missing last time were delivered)", and the 10 s [StandIn] line shows the FGN signal state. No keys.   // 1.2.192 (Fire 0108i, BlueHills teleport 30 s; lead GO): an arrival (spawn / teleport) waits in vanilla IsAreaReady for every ZDO in the 3x3 to have an instance; EBM decides "skip" only when the create loop reaches a piece, so baked pieces it WILL skip counted as missing. [Optimize] ArrivalCountsBakedPieces (default ON, a fix): such a piece counts as ready once its stand-in collider exists. Debug proof "[Arrival] area at (x, z): missing N: S skipped, W would be skipped (stand-in ready), C would be skipped (no stand-in yet), K still to create -> ready / waiting" at most every 5 s.   // 1.2.191 (Fire's BlueHills city, 174k ZDOs, minutes to load; [visual] proof from his 0108h log, lead GO): (a) the stand-in pass rebuilt a zone's whole ZDO identity set (FindSectorObjects + a new HashSet, ~3,000 in a city zone) and walked it again for "real" objects on EVERY frame its queue was open ("tracking zones 0.70-0.85 ms a frame, stand-ins 0.56-0.65"); now the set lives on the zone and is rebuilt only when that sector's ZDO count changes or after 2 s, and "real" is one lookup per identity. (b) a zone built from its cache was validated against LIVE objects that EBM's own create skip never created, so every check saw "0 present" and called the whole zone removed (89 checks, 207,560 pieces); it is now validated against the zone's ZDOs, and waits while none are held or they are still arriving. Debug proof lines: "[StandIn] zone identity sets, last 10 s: N rebuilt, M reused; K cached-zone check(s) waited" and "[Bake] Zone (x,y) cache check vs ZDOs: ... -> cache kept / rebake". No keys.   // 1.2.190 (lead 11-08, 0107w/x sweep): the "[Cache] Zone (x,y): instanced prefab ... no longer resolves; discarding cache so the zone rebakes" line is Debug (was a Warning once per prefab): it is EBM healing a cache made before a mod changed that prefab (Building_VAGaziboRavensRest after the vapieces repack); the zone rebakes once and the new cache leaves the prefab out.  1.2.189 (SanEggs 11-06: Jewelcrafting's Projectile.Awake postfix threw from EBM's prewarm; Fire 11-08 "make sure ebm is safe for all mods"): the prewarm clones prefabs under an INACTIVE holder (no Awake / OnEnable / OnDestroy runs on any component, vanilla or modded) and only warms what EBM can bake or instance (building pieces, its vegetation), never projectiles, creatures, items, ships, carts or spawners; every call into another mod (Vedr, FTS, FAT, FAP, Core) is guarded: a throw gives ONE Warning naming the mod and EBM uses its own behaviour from then on; prefab lookups (ZNetScene.GetPrefab, which other mods patch) are guarded; the load coroutine survives a throw in any step; optional-mod type lookups no longer log a HarmonyX warning when the mod is absent.  1.2.188 (Fire 11-08: "indoor wet from ebm steps aside to vedr when both installed"): while VedrApi.IndoorWetActive (Vedr 0.8.183+, ApiVersion 17, live flag re-read every second) is true, the wet override leaves every renderer on Vedr/Piece to Vedr (roofed-dry and roof undersides; anything EBM had dried is handed back to the global _Wet) and baked groups drop their roofed _Wet 0 split; other shaders keep EBM's dry; ShelterRain unchanged; without Vedr as before. Debug line on each stand-down / resume.  1.2.187 (lead 11-08, 0107u zone (26,4) cache thrown away: stone_wall_1x1_triangular refused on Vedr/Piece before EBM had seen a Vedr swap): the snow-swap mapping is learned up front from the snow mods, gated on their live flag (VedrApi.PieceShaderActive for Vedr/Piece; FTS FiresPieceShaderLoader.Enabled + EnsureLoaded for Custom/FiresPiece), at the ZNetScene.Awake index and again when the gate first meets that shader; learn-on-first-sight stays the fallback; Debug line when learned up front.  1.2.186 (lead 11-08, EBM draw ~30-40 ms/s): the Debug [Draw] line adds "Split:" (main-view prep vs RenderMeshInstanced submit, calls, snow-range calls, part instances a frame; per-camera pre-cull draws by camera name, which the draw figure never counted); toggles [Optimize] InstancedFollowChecksEveryFrame and InstancedOtherCameraRedraw, both default true = as before.  1.2.185 (lead 11-08, 0107r census "8 of 5059 silenced piece(s) are in no zone's list"): the zone unload gives silenced pieces ZNetScene still holds their renderers back before it drops the list (was a bare Clear) and the reload re-adopts them; the census (now Debug on or off) heals orphans already leaked: re-adopted where the active zone's bake draws them, otherwise given back their renderers; one Info line the first time it heals.  1.2.184 (lead 11-07, Fire stone_wall_2x1 body invisible at 1635,460): Debug "[Suppress] orphan census" at most every 30 s and only after something changed: silenced pieces in no zone's SuppressedPieces list (the unload branch clears a list without restoring), first 5 with position, whether ZNetScene still holds them, their EBM zone (active / marked unloaded) and whether its bake draws them.  1.2.183 (lead 11-07, WearNTearUpdater 114 ms/s): Vedr's winter keep holds a piece kind with no vanilla snow cap real only while Vedr 0.8.170+ (VedrApi ApiVersion 16, PieceSnowCapFor) says it gets one or has not decided; kinds Vedr caps never (vines, sloped, stations) skip and bake again; answers cached per kind, undecided ones re-asked when PieceSnowCapRevision moves; older Vedr keeps the 1.2.180 behaviour.the Debug "[Skip] reasons so far" line adds "winter keep: N piece(s) of K kind(s) kept real (M with a snow cap on the prefab, V of W kind(s) only if Vedr caps them: ...)", distinct per season; the winter check builds its reason text only when it is logged.  1.2.182 (lead 11-07, snow_proof3 cache warnings): the modded-prefab gate judges a shader that a snow swap put on a material (Vedr/Piece, FTS's) as the game shader it replaced, and a prefab's verdict is taken before EBM asks for the swap, so modded pieces on Vedr/Piece bake and instance again; the cached-zone "no longer resolves" line names the prefab and why, Warn once per prefab, Debug after.  1.2.181 (Fire 11-07: piece snow moves into Vedr): baked piece snow from VedrApi (PieceSnowCover, PieceExposureAt, PieceFieldActive, ApplyPieceShader, PieceSnowRevision) when Vedr has them, FTS + Core otherwise; _VedrSnowCover written beside _FiresSnowBiome; works with Vedr alone; one Info line names the source.  1.2.180 ([seasons] 11-07, Vedr 0.8.145 snow caps on flat pieces): while Vedr builds winter snow on pieces in a biome (VedrApi.SnowBuildingBiomeMask), WearNTear pieces there are not skip-spawned, so they grow and draw snow; Vedr season change re-checks skipped pieces; Debug reason per piece kind.  1.2.179 ([seasons], Fire 11-07 pier snow): baked pieces never switch off their snow cap renderers (m_snow / m_snowWorn / m_snowBroken and children); vanilla UpdateSnowVisual owns them, so build-up shows on baked pieces. Debug "snow caps left to vanilla: N" per piece kind.  1.2.178 (Fire 11-07, longhouse in a winter storm): in shelter every falling-weather system of the current env stops, re-checked every 0.25 s while inside; flakes living over 1.5 s are cleared as vanilla does on an instant switch; a falling word (snow, rain, ash...) beats the fog/cloud keep words; Debug line per system.  1.2.177 (Fire 11-07, Raven's Rest): invulnerable pieces run vanilla wear wherever snow builds on them now (Deep North, Vedr's snow-building biomes asked of Vedr directly so no Core is needed, Core ground snow when present), re-checked on Vedr's season change.  1.2.175: [Optimize] DeferCreatePacingToFgn: FGN paces object creation when installed, EBM's budget stands down.   was:   // 1.2.174: [Batching] LightFlickerViewCull: lights out of the camera view flicker at the slowest rate. 1.2.173: [Optimize] CreateDestroyDirtyFlag: quiet create / destroy passes are skipped by event instead of a world-wide ZDO count; a real pass still runs at least once a second.  // 1.2.172: [Batching] LightSettingsEvent (one settings hook for all lights) and MinimapFogPartialUpload (explored pixels upload as a rectangle), both default on, each with its off key.  // 1.2.171: without Core, BepInEx's own levels decide debug lines as 1.2.165 meant. 1.2.170: no more red "_Metallic" lines from the cache material key on shaders that declare it as something other than a float. 1.2.169 (found for the README): PluginEnabled off also stops the rain hider, wet override, shelter probe, light budget and item skip, and hands back what they changed. 1.2.168 (bake review): a settings change after a relog no longer serves old bakes; cached materials match on normal map, colour, gloss, metal. 1.2.167 ([generator] final read, checked from source): zones unload again, all-Immune pieces wear, Deep North snow, far one-shots, small fixes. 1.2.166 (Fire 11-05): IdleGates. 1.2.165 (Fire 11-05): no compile reference to Core; parts that need Core or FTS skip when absent. 1.2.164 (review of FTS 0.3.312, C1): prefabs EBM draws go through FTS's piece swap, since a skipped piece never wakes. 1.2.163 (Fire 11-04: baked pieces missing snow): baked cover comes from FTS's PieceCover, the live pieces' method. 1.2.162 (Fire 11-04: baked pieces missing snow): with FTS's world field on, baked instances carry the area cover only, same as live pieces. 1.2.161 (Fire 60 fps goal): Structure Tweaks keys keep a piece live only when Structure Tweaks is installed. 1.2.160 (Fire 11-03): recurring + test lines at Debug; Info = loaded + on/off. 1.2.159 ([visual], launch-1 log): the [SnowBake] split closes the previous frame before a new frame adds its Core coverage / exposure / finish ticks (the parts summed past the total).  1.2.158 ([visual], Fire 10-31 event rule + [release]): binds Core 0.2.1074 SnowField.ZoneAreaChanged; a local snow change (piece placed/removed) re-splits only the groups its rect touches, whole-zone changes as before, ZoneChanged fallback on older Core; [SnowBake] lines split the re-split into Core coverage / exposure test (FTS) / finish / EBM loop.  1.2.157 ([visual], Fire 10-31 picks): (a) the vanilla shader index is built on the loading screen (ZNetScene.Awake), not inside the first in-world CreateObject; (b) A/B toggle [Optimize] SpreadFirstSightChecks (default off): past 4 ms of first-sight checks in a frame, unchecked pieces are created live and baked a moment later. Debug "[EBM] shader index built on the loading screen", "[Skip] frame N: first-sight budget ... created live", worst skip-prefix frame.  1.2.156 ([visual], Fire 10-31 event-driven/cache rule, SAME BEHAVIOUR): the per-frame world-id reflection read stops once the cache preload is kicked for this ZNet; shelter rain reads EnvMan through cached FieldRefs instead of Traverse every 0.25 s. Debug "[Cache] world id ... preload kicked".  1.2.155 ([visual], public Seasonality report via Fire 10-30: autumn trees drawn in a frosted winter look until EBM was toggled): instancing draws follow their source material: a copy re-copies when the source's textures / colours / values change (signature, at most once a second per copy; was shader change only), and a part follows a NEW material put on the prefab renderer (checked once a second); Info one-shot + Debug per follow.  // 1.2.154 ([visual], Fire 10-30 choice (a), public chest report: pieces skipped while unsupported fell when EBM was turned off): [Batching] SkipOnHost and SkipDamageablePieces default FALSE and out of the 1.2.145 defaults-on migration (a saved value stays; no forced flip); both descriptions state the support gap plainly. No logic change.  // 1.2.153 ([visual], Fire 10-30 via lead, public white-trees report: "It's not default on or be forced on"): [Batching] SkipVegetation default FALSE and no longer in the 1.2.145 defaults-on migration (a user's own saved value stays; SkipOnHost / SkipDamageablePieces unchanged).  // 1.2.152 ([visual], Fire 10-30 shot 403: one hanging brazier showed only its bowl, "low on fuel"): pieces with a Fireplace whose fire / fuel children the game toggles (m_enabledObject / High / Low) are never baked or skipped, so a bake can not freeze a fuel state; Debug "[EBM] kept live: <prefab> (toggled visuals ...)" once per prefab.  // 1.2.151 ([visual], [fgn] / lead 10-30 town walk: "not created 12174/frame (sorted every frame) ... 937.2 ms over 69 frames"): [Optimize] PartialCreateSort (default ON): the create queue is no longer sorted in full every frame; only the nearest K = max(512, 4 x budget) go first in vanilla's order (bounded heap, O(n log K)); [CreateBudget] status counts "partial" sorts.  // 1.2.150 ([visual], Fire 10-30 "if that setting makes things worse then lets get rid of it all together"): the DeferCreationWhileBaking feature is removed (EasyBake/DeferCreation.cs, its ZoneTracker hold and cfg key); a saved line in an old cfg is an orphan BepInEx ignores.  // 1.2.149 ([visual], Fire 10-30 shadow option C for the A/B): [Optimize] FarSmallNoShadows (default OFF) + NoShadowBeyondMeters (40) + NoShadowSmallerThanMeters (1): baked instanced pieces whose group is beyond the distance, or whose mesh is smaller than the size, draw without casting a sun shadow; the [Draw] Debug line counts the part draws cut.  // 1.2.148 ([visual], public report via Fire): feasts (any piece with a Feast component) are never baked.   // 1.2.147 ([visual], lead dense65 worst 41.7 ms): the snow re-split budget holds across groups: a group waits a frame when the shared budget is spent (only the frame's first re-split is guaranteed progress), budget checked every 4 instances (was a forced 16 per group: 23 groups x 14 = 313 instances = 41.7 ms); Debug summary counts deferred draws.  // 1.2.146 ([visual], lead 10-28, dense58 161.5 ms re-split): snow re-split sliced to a per-frame budget shared by all groups, cut every 16 instances inside a group (Batching / SnowResplitBudgetMs = 2, 0 = off); Debug "[SnowBake] re-split sliced: N group(s) over M frame(s), worst X ms".  //   // 1.2.145 ([visual], Fire 10-28: proven improvements never off by default): SkipOnHost, SkipDamageablePieces, SkipVegetation default ON; an existing cfg's false is moved to true ONCE (marker BepInEx\cache\FiresEasyBakeMeshes\defaults_on_1_2_145.done); Debug "[EBM] defaults-on migration".   // 1.2.144 ([visual], lead 10-28 settled snow spikes): [SnowBake] re-split timer: Debug line per frame over 20 ms (groups, instances), worst frame in the 30 s summary. No behaviour change.  //   // 1.2.143 ([visual], Fire 10-28 "Build it"): FAP/VA source lookups resolved once; the source only worked out when its Debug line is written; [Skip] sampler splits prefab scan / modded gate; EasyBakeLog.Debug writes LogDebug.   // 1.2.142 ([visual], lead 10-28): Debug-only sampler of the CreateObject skip prefix by step, one line per frame over 20 ms. 1.2.141 ([visual], 10-28 ledger: the 1.2.140 prewarm coroutine was the top stall site, 37.7 s): prewarm removed (lazy again); slow-step Debug lines kept. 1.2.140 ([visual], round 45 login stall 1139 ms, EBM CreateObject prefix 659 ms): shader index + piece prefab checks prewarmed after ZNetScene.Awake at 3 ms a frame; Debug "[EBM] prewarm: ..." and "[Skip] slow step inside CreateObject: ..." per step over 10 ms. 1.2.139 ([visual], Fire 10-27 fire shadows flashing / smelter light pool flipping): light budget never touches shadows on a LightLod light (vanilla owns them), enter 18 m / leave 22 m + 3 s hold, Debug line per flip. 1.2.138 ([visual], Fire 10-27 standing rule via lead: why/proof/summary lines are Debug only): every EasyBakeLog.Info except the loaded/active/enable lines and console-command output now writes at Debug; no behaviour change. 1.2.137 ([visual] for [ghost]'s live map ROOFS toggle, Fire 10-27): add-only FiresEasyBakeMeshes.EbmRoofs.HideRoofsFor(Camera, bool) keeps baked ROOF groups (name has "roof", not "wall_roof") out of that camera only (per-camera pre-cull draw while any camera hides them; no change otherwise) + IsRoofPrefab / IsRoofName for live pieces. 1.2.136 ([visual], Fire 10-27 YES, homestead 8 fps = x10.9 render passes from 22 point lights): [Optimize] LightBudgetEnabled (default on): only the nearest 8 build-piece lights within 20 m keep pixel lighting + shadows, the rest vertex-lit with no shadows (EasyBake/LightBudget.cs; Debug "[EBM] light budget: ..."); the 1.2.125-1.2.135 executing lines moved to Debug. 1.2.135 ([visual], overnight 10-27, lead GO): [General] PerformanceProfile = Default | LowEnd (read once at load; Default unchanged): LowEnd halves every per-frame bake / convert / stand-in / destroy / material budget and the create budget, far tier + light / SFX LOD distances x0.75; executing line. 1.2.134 ([visual], overnight #6 RAM census): the wet override drops destroyed pieces / renderers from its caches once a minute (was: held until 20k or the rain stopped). 1.2.133 ([visual], overnight #9 log census): the per-group [SnowBake] shader line only with [Batching] VerboseLogging (was ~1000 lines / 20 min), one 30 s summary instead. 1.2.132 ([visual], Fire 10-27 shot 535 "the undersides of roofs still render wet"): while the local player is in shelter (1 s in / 2 s out) and it rains, every live or baked piece over their head within 20 m draws with _Wet 0 (vanilla's wet gloss is gated by the sky-depth camera only, so a roof's underside reads as open sky); why-line "roof undersides dried N". 1.2.131 ([visual], Fire 10-26 shot 525 "stop the inside of pieces from looking wet"): while it rains, live pieces with vanilla's roof flag (WearNTear.m_haveRoof) get _Wet 0 in their MaterialPropertyBlock and baked groups draw their roofed range with _Wet 0 (vanilla Custom/Piece and FiresPiece both read the global _Wet); why-line "[EBM] wet override". 1.2.130 ([visual]/[seasons] 10-26, Fire's hut 403 roofed vs 67 open): no stand-in collider while the piece is still a real object (it sat inside its own copy; every roof test from the piece hit it); stand-ins carry an EbmStandIn marker (EbmStandIns.StandsInFor); [StandIn] why-lines. 1.2.129 ([visual], Fire 10-26 "logging in place to tell WHY"): shelter probe adds per-collider ray hits (live / stand-in, layer) + the rain-FX hider state each 10 s; every probe / hider bail-out logs its reason once. 1.2.128 ([visual], Fire 10-26 "rain indoors needs to live in ebm"): while the local player is in shelter, the current env's falling-weather particle systems stop emitting (ShelterRain, standalone; FTS 0.3.222 stands down on FiresEasyBakeMeshes.ShelterRain.Active); shelter probe adds a wet-underfoot dump. 1.2.127 ([visual], lead/Fire 10-26 wet inside baked homes): a re-bake keeps the zone's current stand-in colliders instead of destroying all and rebuilding them over frames (no roof gap); stand-ins copy the vanilla "leaky" tag; plus the read-only Debug "[EBM] shelter probe" every 10 s while raining (vanilla Cover rays + roof sphere-cast, live vs EBM stand-in hits, InShelter, Wet SE; EasyBake/ShelterProbe.cs). 1.2.125 ([visual]/[seasons], Fire 10-26 shots 459/460 straw roof white far, bare near): EBM instancing copies follow their source material's shader when FTS swaps it in place (InstanceDefinition.FollowSource, Info line per change); Debug [SnowBake] line per group every 10 s (shaders + mean _FiresSnowBiome). 1.2.124 ([visual], [seasons] SNOW_PERF_DIAG 10-26): baked-instance snow skips the roof test where coverage is 0 and caches each spot's roof answer 30 s (EasyBake/ExposureCache.cs), instead of one raycast per instance on every snow change. 1.2.123 ([visual], Fire 10-26 #1: "item drops float away like they have no gravity"): the remote-item ZSync skip pins the body (kinematic, velocity 0) while it holds a settled remote drop, the way vanilla ClientSync did every frame, restores it on hand-back, and also hands back when our copy drifts from the spot. 1.2.122 ([visual], lead 10-24 halo ring perf): EBM zones follow FAT 0.2.1050 ring OBJECT sectors (EasyBake/EbmZones.cs, FatApi.HaloZone by reflection; x,z off the ring): zone keys, loaded test, LOD + sort distance, cached-zone loop wrap at the seam, build-hold neighbourhood, create/destroy gates; keep-alive and the destroy loaded-zone index stand down on the ring. Terrain-keyed sector mirror unchanged. 1.2.121 ([visual], lead 10-24 halo ring): per-instance roof/shelter test point = the top of the bounds along the LOCAL up (Core GravityUp, soft; world up off a ring). 1.2.120 ([visual]/[seasons] 10-24): roof/shelter from FTS FiresPieceSnowDriver.SnowExposureAt (one implementation, reflection; vanilla Cover fallback) at the instance bounds TOP. 1.2.119 ([visual], Fire 10-24 snow under his roof): baked instances under a roof (vanilla Cover.IsUnderRoof) get snow 0 and draw in their own batches. 1.2.118 ([visual], lead 10-24): the instancing self-check uses vanilla Custom/Piece (loaded and drawn instanced), not Standard (not loaded in Valheim). 1.2.117 ([visual], [ghost]/[lead] 10-24): baked instances take Core dynamic snow (_FiresSnowBiome per instance, EasyBake\SnowFeed.cs, reflection-gated). 1.2.116 ([visual], hand-over 57 invisible pieces): one warning per shader that has no INSTANCING_ON variant when EBM would draw it instanced (InstanceDefinition.WarnIfNoInstancingVariant; read-only). 1.2.115 ([visual], Fire 10-24 invisible forge): PatchesStationInteractProbe.cs logs ebm_why for a crafting station when it is used (read-only probe; ZoneTracker.WhyFor).
        private const string StatusSource = "EBM";

        public static ConfigEntry<bool> PluginEnabled;
        public static ConfigEntry<bool> VerboseZoneLogging;

        public static ConfigEntry<bool>  BatchingEnabled;
        public static ConfigEntry<bool>  BatchingRunOnServer;
        public static ConfigEntry<int>   BatchingMinPiecesPerZone;
        public static ConfigEntry<int>   BatchingMinPiecesPerBatch;
        public static ConfigEntry<float> BatchingSettleDelaySeconds;
        public static ConfigEntry<float> SuppressBudgetMs;
        public static ConfigEntry<float> SnowResplitBudgetMs;   // 1.2.146
        public static ConfigEntry<float> ClassifyBudgetMs;
        public static ConfigEntry<bool>  BatchingFarTierEnabled;
        public static ConfigEntry<float> BatchingFarTierDistance;
        public static ConfigEntry<bool>  BatchingInstancingEnabled;
        public static ConfigEntry<bool>  BatchingCullOffScreen;
        public static ConfigEntry<bool>  BatchingMultiPartPieces;
        public static ConfigEntry<float> BatchingBuildRadius;
        public static ConfigEntry<int>   BatchingMinInstancesPerPrefab;
        public static ConfigEntry<bool>  BatchingDamageablePieces;
        public static ConfigEntry<bool>  BatchingVerbose;
        public static ConfigEntry<bool>  BatchingExcludeTransparent;
        public static ConfigEntry<string> BatchingExcludedPrefabs;
        public static ConfigEntry<string> BatchingModdedPrefabs, BatchingModdedPrefabAllowlist;
        public static ConfigEntry<bool>  SkipBakedPieceObjects;
        public static ConfigEntry<bool>  SkipOnHost;
        public static ConfigEntry<bool>  SkipDamageablePieces;
        public static ConfigEntry<bool>  SkipVegetation;

        public static ConfigEntry<bool>   PrewarmEnabled;
        public static ConfigEntry<bool>   PrewarmRunOnServer;
        public static ConfigEntry<int>    PrewarmPrefabsPerFrame;
        public static ConfigEntry<bool>   PrewarmVerbose;
        public static ConfigEntry<string> PrewarmSkipNameContains;
        public static ConfigEntry<float>  PrewarmSlowInstantiateThresholdMs;
        public static ConfigEntry<int>    PrewarmMaxTransformCount;
        public static ConfigEntry<float>  PrewarmFrameBudgetMs;
        public static ConfigEntry<float>  MaterialRegistryFrameBudgetMs;

        public static ConfigEntry<bool> CachePersistEnabled;

        public static ConfigEntry<int>   CreateBudgetPerFrame;
        public static ConfigEntry<int>   CreateBudgetReleaseAbove;
        public static ConfigEntry<bool>  PartialCreateSort;   // 1.2.151
        public static ConfigEntry<float> CandidateCensusSeconds;
        public static ConfigEntry<int>   CandidateCensusSample;
        public static ConfigEntry<bool>  CreationCensusEnabled;
        public static ConfigEntry<bool>  CreateDestroySkipEnabled;
        public static ConfigEntry<float> CreateDestroySkipMaxSeconds;
        public static ConfigEntry<bool>  CreateDestroySkipVerbose;

        public static ConfigEntry<bool>  WearShortCircuitEnabled;

        public static ConfigEntry<bool>  ZoneKeepaliveEnabled;
        public static ConfigEntry<int>   ZoneKeepaliveRadius;
        public static ConfigEntry<float> ZoneKeepaliveSeconds;
        public static ConfigEntry<bool>  ZoneKeepaliveVerbose;

        public static ConfigEntry<bool>  DestroyTimeSliceEnabled;
        public static ConfigEntry<float> DestroyTimeSliceBudgetMs;
        public static ConfigEntry<bool>  DestroyTimeSliceVerbose;

        public static ConfigEntry<bool>  SectorMirrorEnabled;
        public static ConfigEntry<float> SectorMirrorRebuildSeconds;
        public static ConfigEntry<bool>  SectorMirrorVerbose;

        public static ConfigEntry<bool>  LightFlickerLodEnabled;
        public static ConfigEntry<bool>  LightBudgetEnabled;   // 1.2.136
        public static ConfigEntry<bool>  SpreadFirstSightChecks;   // 1.2.157 A/B
        private static ZNet s_preloadKickedFor;   // 1.2.156: the ZNet whose world preload is already kicked
        public static ConfigEntry<bool>  FarSmallNoShadows;   // 1.2.149
        public static ConfigEntry<bool>  InstancedFollowChecksEveryFrame, InstancedOtherCameraRedraw;   // 1.2.186
        public static ConfigEntry<float> InstancedCellSizeMeters;   // 1.2.195
        public static ConfigEntry<bool>  InstancedCellLod, InstancedShadowProxies;   // 1.2.196
        public static ConfigEntry<bool>  InstancedInstanceCull;   // 1.2.200
        public static ConfigEntry<bool>  IndirectRenderer;   // 1.2.203
        public static ConfigEntry<float> IndirectMinScreenPercent;   // 1.2.203
        public static ConfigEntry<bool>  IndirectCoverage;   // 1.2.206
        public static ConfigEntry<bool>  IndirectGrass;          // 1.2.213 ([seasons]: EBM GPU grass)
        public static ConfigEntry<float> IndirectGrassUploadMs;  // 1.2.213
        public static ConfigEntry<bool>  IndirectGrassFadeCarry; // [seasons] 11-09 E12/E13: a rebuilt patch keeps its row and fade
        public static ConfigEntry<float> GrassPatchSize;         // [seasons] 11-09 E15 (off)
        public static ConfigEntry<float> GrassFarSwayStop;       // [seasons] 11-09 E15 (off)
        public static ConfigEntry<bool>  IndirectGrassStepGate;       // [seasons] 11-09 round 2 (on, acts only with IndirectGrass)
        public static ConfigEntry<bool>  IndirectGrassStepGateVerify; // [seasons] 11-09 round 2 (Debugging, off)
        public static ConfigEntry<bool>  IndirectGrassArrayRecycle;   // [seasons] 11-09 round 2 (off until the A/B)
        public static ConfigEntry<bool>  IndirectCoverageDisableRenderers;
        public static ConfigEntry<bool> FarMeshReadNoRawFlags;
        public static ConfigEntry<bool> FarBuildingsQuietPolling;   // 1.2.249
        public static ConfigEntry<bool> FarSourceAsync, FarUploadSliced, IndirectPresize;
        public static ConfigEntry<bool> FarIndexRanges, FarZoneGrid, FarGroundProbe;   // [worldgen] 11-09, round 2 E16 / E19 / E20, A/B keys read live
        public static ConfigEntry<bool> FarHandoffProvenDrawn, FarKeepHandedOff, FarRetireSliced, FarUploadByteCap, FarBuildViewFirst;   // [worldgen] 11-09, round 2 E17 / E18, A/B keys read live
        public static ConfigEntry<bool> BakeCoverageCensus, BakeVerifyCoverage, BakeFollowChanges;   // [worldgen] 11-09, round 2 E26 / E27, keys read live
        public static ConfigEntry<bool> FarCapHysteresis, FarMergePooled;   // [worldgen] 11-09, the far-ON leg's eviction thrash + merge allocation, A/B keys read live
        public static ConfigEntry<float> ZoneTransitionBudgetMs;
        public static ConfigEntry<bool>  IndirectSparseUpload, IndirectStableLayout, IndirectCasterCpuCull, IndirectPointFaces, IndirectExactCascadeSpheres, IndirectCoverageDisablePrefabs, IndirectGpuGrow, IndirectCompactSlots, IndirectCellFastSkip, DestroyLeanEarmark, RendererCensus, IndirectCellReuse, IndirectRugVariant, IndirectCoverageReach, IndirectCoverageLocationProps, FarBuildingsChangedZonesOnly, IndirectCoverageLodLevels;   // + [ghost] step 2   // 1.2.210 VPO-study fixes, each an A/B key (read live)   // 1.2.210 [ghost]: covered renderers enabled = false (out of Unity's culling) instead of forceRenderingOff

        public static ConfigEntry<bool>  IndirectCascadeShadows;   // 1.2.208
        public static ConfigEntry<bool>  IndirectCullGrouped, IndirectOneCullPass, IndirectLodPerInstance;   // G3 / G4 / rank 6 ([wishbone] 11-08)
        public static ConfigEntry<bool>  IndirectShadowReceiverCull;   // 1.2.209 ([saddles])
        public static ConfigEntry<float> IndirectShadowSizeCut;        // 1.2.209 ([saddles])
        public static ConfigEntry<bool>  IndirectShadowSameFrame;      // 1.2.211 ([saddles] G6 v2)
        public static ConfigEntry<bool>  IndirectLooksChangeDriven;    // 1.2.223 ([visual])
        public static ConfigEntry<bool> SectorMirrorSlicedRebuild;   // 1.2.225 merge of [ghost] 1.2.222 spikes
        public static ConfigEntry<bool> PhysicsCensus;   // 1.2.222 [ghost]
        public static ConfigEntry<bool> IndirectIntegrityCheck;   // 1.2.226
        internal static FiresEasyBakeMeshesPlugin Instance;   // 1.2.245: host for the Raw probe coroutine
        public static ConfigEntry<bool> StreamPacerMotionByPosition;   // 1.2.252
        public static ConfigEntry<bool> StreamPacerMotionFromReference, StreamPacerRealCostCap, StreamPacerUnfocusedBoostOnlyWhenThrottled;   // 1.2.256
        public static ConfigEntry<bool> LateJoinFromZdo, RemoteZoneValidation, StreamPacerVanillaFloor, SaveOffThread;   // 1.2.253
        public static ConfigEntry<bool> StreamPacerMovingCap, StreamPacerViewOrder;   // 1.2.257 (moving keep-up)
        public static ConfigEntry<bool> CacheSeparateRoleFolders, CacheLowGarbage, CacheRejectChangedPrefabs, CacheEmptyZoneDropsCache, CacheKeepOtherSettings, CachePreloadNearOnly, IndirectCoverAfterBakeEnds, CoverageBatchedStart;   // 1.2.255
        public static ConfigEntry<int> CacheMaxMBPerWorld, CacheMaxMBAllWorlds;   // 1.2.255
        public static ConfigEntry<bool> FarHandoffCountsSkipped, IndirectCrashFallback, SaveSerializeOffThread;   // 1.2.256
        public static ConfigEntry<bool> ShareAwakePropertyBlock;   // 1.2.257
        public static ConfigEntry<bool> BakeFrameSplit; public static ConfigEntry<float> BakePassBudgetMs;   // [saddles] 11-09
        public static ConfigEntry<bool> StreamPacerExtraPasses, TickGroupEventDriven, IndirectCellReusePerCell;   // 1.2.254
        public static ConfigEntry<bool> IndirectPiecePixelShave;   // 1.2.251
        public static ConfigEntry<bool> IndirectGpuMeshValidator;   // 1.2.245
        public static ConfigEntry<bool> IndirectMeshContentCheck;   // 1.2.235
        public static ConfigEntry<bool> CoverageCensus;   // 1.2.227
        public static ConfigEntry<bool>  StreamPacer;                  // 1.2.228 ([visual], SP split); key ObjectStreamPacer since 1.2.250
        // 1.2.250 (Fire's SP split): the rest of what moved in from FGN
        public static ConfigEntry<int>   StreamPacerBaseMs, StreamPacerMinPerFrame;
        public static ConfigEntry<float> EventAreaFullPassSeconds;
        public static ConfigEntry<bool>  StreamPacerEveryFramePass, StreamPacerSelfCheck, IdleObjectAreaSkip, EventDrivenObjectArea, DestroyMoversFirst, RemovalIdleScanLimit, TeleportCreateGate, DistantPrefabStreaming, PieceTypeAudit, ZoneLoadDiagnostics;
        public static ConfigEntry<bool>  IndirectCasterSizeSkip;   // 1.2.233
        public static ConfigEntry<float> IndirectCasterMinScreen;  // 1.2.233
        public static ConfigEntry<bool>  IndirectShadowPlaceholders;   // 1.2.211 ([saddles] §E)
        public static ConfigEntry<bool>  IndirectCellsJob, IndirectDrawParamsCache, IndirectCascadeEmptySkip, IndirectVertexDebug, IndirectSettledWalk, IndirectCameraEmptySkip;
        public static ConfigEntry<bool>  IndirectCascadeCounts;   // 1.2.244   // 1.2.241
        public static ConfigEntry<bool>  IndirectLightsSwept, IndirectCascadeTightStride;   // [worldgen] 11-09, A/B keys read live
        public static ConfigEntry<bool>  IndirectSyncCleanup;   // [worldgen] 11-09, A/B key read live
        public static ConfigEntry<bool>  IndirectStagedSync;   // [worldgen] 11-09, A/B key read live
        public static ConfigEntry<bool>  IndirectFusedCells, IndirectCachedAlive, IndirectLocalSteady;   // 1.2.213 ([saddles], the [Draw] cost study #2 / #3 / #4), A/B keys read live
        public static ConfigEntry<bool>  BakeIntactRocks;               // 1.2.201 ([ghost], DESIGN_OcclusionLOD §5)
        public static ConfigEntry<float> IntactRockLod1ScreenHeight;    // 1.2.201
        public static ConfigEntry<int>   IntactRockLod1CellsAcross;     // 1.2.202 (was IntactRockLod1Detail 48 in 1.2.201)
        public static ConfigEntry<float> NoShadowBeyondMeters, NoShadowSmallerThanMeters;
        public static ConfigEntry<float> LightFlickerLodDistance;
        public static ConfigEntry<float> LightFlickerMidDistance;
        public static ConfigEntry<float> LightFlickerFarDistance;
        public static ConfigEntry<float> LightFlickerMidRate;
        public static ConfigEntry<float> LightFlickerFarRate;
        public static ConfigEntry<float> LightFlickerDistantRate;
        public static ConfigEntry<float> LightFlickerTierCheckRate;

        public static ConfigEntry<bool>  ZSFXLodEnabled;
        public static ConfigEntry<bool>  ArrivalCountsBakedPieces;   // 1.2.192
        public static ConfigEntry<float> ZSFXLodDistance;

        public static ConfigEntry<bool>  ZSyncStaticSkipEnabled;
        public static ConfigEntry<bool>  RemoteItemZSyncSkipEnabled;
        public static ConfigEntry<bool>  ZdoBakeVerifyEnabled;
        public static ConfigEntry<bool>  SkipUnchangedScans;
        public static ConfigEntry<bool>  ZSyncStaticSkipFixtures;
        public static ConfigEntry<bool>  ZSyncStaticSkipVerbose;
        public static ConfigEntry<bool>  EffectAreaIdleSkipEnabled;

        public static ConfigEntry<bool>  ClutterLodEnabled;
        public static ConfigEntry<float> ClutterLodMoveThreshold;
        public static ConfigEntry<float> ClutterLodMaxSeconds;
        public static ConfigEntry<bool>  ClutterLodVerbose;

        // Live-toggle bookkeeping. The Harmony instance is created in Awake
        // unconditionally so the PluginEnabled.SettingChanged handler has
        // somewhere to apply/unapply patches on demand without restart.
        private Harmony _harmony;
        private bool    _patchesApplied;

        // 1.2.92 (Fire 10-13, Tools\CONFIG_AUDIT_20261013.md): keys no EBM build binds any more stay in the .cfg as orphans and read
        // like working settings. Only the NAMED ones leave (a key bound later in the session is an orphan for a while too).
        private static readonly string[,] RetiredKeys = { { "Optimize", "CreateDestroySkipScoped" }, { "Optimize", "DeferCreatePacingToFgn" }, { "Optimize", "StreamPacer" } };   // 1.2.250: StreamPacer's saved "off" must not reach the new default (key ObjectStreamPacer)

        private void RetireOldKeys()
        {
            try
            {
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                var orphans = (typeof(ConfigFile).GetProperty("OrphanedEntries", flags)?.GetValue(Config, null)
                               ?? typeof(ConfigFile).GetField("<OrphanedEntries>k__BackingField", flags)?.GetValue(Config)) as IDictionary;
                if (orphans == null) return;
                int removed = 0;
                for (int i = 0; i < RetiredKeys.GetLength(0); i++)
                {
                    var def = new ConfigDefinition(RetiredKeys[i, 0], RetiredKeys[i, 1]);
                    if (!orphans.Contains(def)) continue;
                    orphans.Remove(def);
                    removed++;
                }
                if (removed == 0) return;
                Config.Save();
                EasyBakeLog.Debug($"[Config] removed {removed} retired key(s) no EBM build reads any more from {System.IO.Path.GetFileName(Config.ConfigFilePath)}");
            }
            catch (System.Exception ex) { EasyBakeLog.Warn($"[Config] could not remove retired keys: {ex.Message}"); }
        }

        // 1.2.145: BepInEx keeps a value already in the cfg, so an existing "= false" (the old default) would never pick up the new
        // default ON. Once per install (marker file), each listed key still at false is set to true; afterwards a player's false stays.
        private static readonly System.Collections.Generic.List<string> s_defaultOnMoved = new System.Collections.Generic.List<string>();
        private static bool? s_defaultOnDone;
        private static string DefaultOnMarker => System.IO.Path.Combine(Paths.CachePath, "FiresEasyBakeMeshes", "defaults_on_1_2_145.done");

        private static void MigrateDefaultOn(ConfigEntry<bool> entry)
        {
            if (s_defaultOnDone == null) { try { s_defaultOnDone = System.IO.File.Exists(DefaultOnMarker); } catch { s_defaultOnDone = true; } }
            if (s_defaultOnDone == true || entry.Value) return;
            entry.Value = true;
            s_defaultOnMoved.Add(entry.Definition.Key);
        }

        private static void FinishDefaultOnMigration()
        {
            if (s_defaultOnDone != false) return;
            s_defaultOnDone = true;
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DefaultOnMarker));
                System.IO.File.WriteAllText(DefaultOnMarker, "1.2.145 defaults-on migration ran " + System.DateTime.Now.ToString("s"));
            }
            catch (System.Exception ex) { EasyBakeLog.Warn("[EBM] defaults-on migration: could not write its marker (" + ex.Message + "); it will run again next start"); }
            EasyBakeLog.Debug(s_defaultOnMoved.Count > 0
                ? $"[EBM] defaults-on migration (once): moved {string.Join(", ", s_defaultOnMoved)} from the old default false to true"
                : "[EBM] defaults-on migration (once): nothing to move (already true)");
        }

        private void Awake()
        {
            Application.quitting += () => { s_quitting = true; EasyBake.Indirect.EbmIndirect.Shutdown(); EasyBake.MeshCacheStore.FlushPendingSaves(5000); EasyBake.Indirect.IndirectCrashGuard.CleanExit(); };   // 1.2.97: see OnDestroy; 1.2.203: GPU buffers released at quit
            Instance = this; EasyBakeLog.Bind(Logger);

            try { FiresEasyBakeBanner.PrintBig(); }
            catch (System.Exception ex) { Logger.LogWarning($"Big banner failed: {ex.Message}"); }

            // Detect FiresDebugginTools so EasyBake's own hot paths self-time into
            // the shared FrameTimer only when the profiler is there to drain it.
            EasyBake.Probe.Detect();

            PluginEnabled = Config.Bind("General", "PluginEnabled", true,
                "Master kill switch. When off no Harmony patches are applied at all.");

            // 1.2.135 ([visual], lower-end PCs): read once here; a change takes effect at the next game start.
            var perfProfile = Config.Bind("General", "PerformanceProfile", "Default",
                new ConfigDescription(
                    "Default: EBM's budgets as configured. LowEnd: for weaker PCs. Half the per-frame bake work (zones finish baking\n" +
                    "about twice as slowly, no frame gets more than about half the EBM work), half the objects created per frame,\n" +
                    "and the far-away look and light / sound simplifications start a quarter closer. Takes effect at the next game start.",
                    new AcceptableValueList<string>("Default", "LowEnd")));
            EasyBake.PerformanceProfile.Load(perfProfile.Value);

            VerboseZoneLogging = Config.Bind("General", "Verbose Zone Logging", false,
                "Log the per-zone cache-hit and fresh-bake lines (one line per zone,\n" +
                "~25-30 at login on a megabase). Off by default â€” the debounced\n" +
                "ZONES BAKED summary box carries the totals either way.");

            BatchingEnabled = Config.Bind("Batching", "Enabled", true,
                "Workstream E.2 v1: at zone load, combine all invulnerable-piece meshes\n" +
                "into one batch per material. Per-piece MeshRenderers are disabled but\n" +
                "GameObjects (and ZNetView, colliders, Piece) stay alive.");

            BatchingRunOnServer = Config.Bind("Batching", "RunOnServer", false,
                "Run batching on dedicated server too. Default off â€” a headless server\n" +
                "doesn't render so Mesh.CombineMeshes() output is pure wasted CPU + memory.\n" +
                "A player-hosted world (host + client in one process) still gets the benefit\n" +
                "regardless of this setting; this gate only suppresses true dedicated servers.");

            BatchingMinPiecesPerZone = Config.Bind("Batching", "MinPiecesPerZone", 20,
                new ConfigDescription(
                    "Below this many invulnerable pieces in a zone, skip batching entirely.\n" +
                    "Tiny invulnerable counts don't pay back the bake cost.",
                    new AcceptableValueRange<int>(1, 10000)));

            BatchingMinPiecesPerBatch = Config.Bind("Batching", "MinPiecesPerBatch", 10,
                new ConfigDescription(
                    "Below this many pieces sharing a material, that bucket passes through\n" +
                    "to vanilla rendering rather than getting its own combined mesh.",
                    new AcceptableValueRange<int>(1, 10000)));

            BatchingInstancingEnabled = Config.Bind("Batching", "InstancingEnabled", true,
                new ConfigDescription(
                    "Draw uniform single-mesh prefabs (palisades, floor tiles, stakewalls)\nas GPU instances instead of merging them into a combined mesh. Costs no\nvertex duplication, and a removed piece drops out in O(1) instead of\nforcing the zone to rebake."));

            BatchingCullOffScreen = Config.Bind("Batching", "SkipInstancesOutOfView", true,
                new ConfigDescription(
                    "Instanced pieces are handed to the graphics card again every frame, for every\nloaded zone, including the ones behind you. With this on, a group of instances\nwhose box is outside the camera is left out of the frame entirely. The box is\nstretched along the sun first, so shadows still land on what you can see."));

            BatchingMultiPartPieces = Config.Bind("Batching", "MultiPartInstancing", true,
                new ConfigDescription(
                    "Draw pieces built from more than one mesh - vines, log poles, tiled roofs - as GPU\ninstances as well, one draw per mesh and material. Without it only prefabs that come\ndown to a single renderer are instanced and the rest are merged or left to draw\nthemselves. Applies to zones baked after a change."));

            BatchingMinInstancesPerPrefab = Config.Bind("Batching", "MinInstancesPerPrefab", 10,
                new ConfigDescription(
                    "A prefab needs at least this many copies in one zone before it is\ndrawn as instances. Below it the pieces fall through to mesh combining,\nwhere they still batch by material with everything else.",
                    new AcceptableValueRange<int>(2, 10000)));

            BatchingDamageablePieces = Config.Bind("Batching", "DamageablePieces", true,
                new ConfigDescription(
                    "Also draw building pieces that can take damage as GPU instances. They stay\n" +
                    "in the world, so rain wear, support and raids work as normal, and a piece\n" +
                    "goes back to drawing itself the moment it is damaged, burns or is\n" +
                    "highlighted by a hammer. Never skipped. Applies to zones baked after a change."));

            BatchingFarTierEnabled = Config.Bind("Batching", "FarTierEnabled", true,
                new ConfigDescription(
                    "Bake a second combined mesh per zone from each piece's LOD1 geometry\n" +
                    "and swap to it once the viewer is far enough away. Without it a baked\n" +
                    "zone draws full LOD0 detail at every distance."));

            BatchingFarTierDistance = Config.Bind("Batching", "FarTierDistanceMeters", 64f,
                new ConfigDescription(
                    "Distance from the zone centre at which a baked zone swaps to its LOD1\n" +
                    "mesh. A zone is 64m across, so its centre is at most ~45m from any\n" +
                    "point inside it â€” keep this at or above 64 and the swap can never\n" +
                    "happen while you are standing in the zone watching it.",
                    new AcceptableValueRange<float>(48f, 512f)));

            ClassifyBudgetMs = Config.Bind("Batching", "ClassifyBudgetMs", 2f,
                new ConfigDescription(
                    "Milliseconds per frame spent sorting a zone's pieces for instancing while they arrive,\n" +
                    "before the zone bakes. A big base's sorting was most of its bake frame (60+ ms);\n" +
                    "done ahead, the bake only re-sorts pieces that changed since. 0 = all at bake time.",
                    new AcceptableValueRange<float>(0f, 50f)));

            SuppressBudgetMs = Config.Bind("Batching", "SuppressBudgetMs", 3f,
                new ConfigDescription(
                    "Milliseconds per frame spent hiding the original pieces a bake replaces.\n" +
                    "A big base hides tens of thousands of pieces; doing it all in the bake's\n" +
                    "frame was a 100-200 ms hitch. The combined mesh appears once all of its\n" +
                    "pieces are hidden, so nothing draws twice. 0 = all in one frame (old behaviour).",
                    new AcceptableValueRange<float>(0f, 50f)));

            BatchingSettleDelaySeconds = Config.Bind("Batching", "SettleDelaySeconds", 1.5f,
                new ConfigDescription(
                    "Wait this long after the last piece is added or removed in a zone\n" +
                    "before baking. Cooperates with VAGhetto's time-sliced instantiation:\n" +
                    "pieces arrive across many frames, settle-delay debounces until the\n" +
                    "zone is stable. Each new piece resets the timer.",
                    new AcceptableValueRange<float>(0.25f, 30f)));

            BatchingVerbose = Config.Bind("Batching", "VerboseLogging", false,
                "Log per-zone classification and bake details.");

            // 1.2.146 ([visual], dense58: one 161.5 ms snow re-split frame in the settled world): a fix, so ON by default
            SnowResplitBudgetMs = Config.Bind("Batching", "SnowResplitBudgetMs", 2f,
                new ConfigDescription(
                    "When snow or rain changes, every baked group re-works which of its pieces are under a roof. This spreads that work\n" +
                    "over frames: at most this many milliseconds a frame for all groups together (a group carries on next frame where it\n" +
                    "stopped, drawing with its previous snow meanwhile). 0 = do it all in one frame (the old behaviour, which can stall).",
                    new AcceptableValueRange<float>(0f, 50f)));

            BatchingExcludeTransparent = Config.Bind("Batching", "ExcludeTransparentPieces", true,
                "Skip pieces with transparent-queue materials (renderQueue >= 2500) and\n" +
                "FiresGlass windows. Combining transparent geometry into a static batch\n" +
                "breaks per-piece depth sorting â€” stained glass sorts against the world\n" +
                "per pane, a merged mesh sorts once for the whole batch. Excluded pieces\n" +
                "stay live and render normally.");

            BatchingModdedPrefabs = Config.Bind("Batching", "ModdedPrefabs", EasyBake.ModdedPrefabGate.Checked,
                new ConfigDescription(
                    "Other mods' pieces: Checked = bake one only when everything it draws uses the game's own shaders, with no\n" +
                    "keyword the game never sets, no texture array and no per-object material values (else it keeps its own\n" +
                    "renderers, logged once: \"[EBM] skipped <prefab> (<mod>): <reason>\"); Skip = never bake another mod's piece;\n" +
                    "Bake = bake them like vanilla pieces (before 1.2.85). Relog to rebake zones already baked.",
                    new AcceptableValueList<string>(EasyBake.ModdedPrefabGate.Checked, EasyBake.ModdedPrefabGate.Skip, EasyBake.ModdedPrefabGate.Bake)));
            RetireOldKeys();   // 1.2.92
            BatchingModdedPrefabAllowlist = Config.Bind("Batching", "ModdedPrefabAllowlist", "",
                "Comma-separated prefab names (exact) of other mods' pieces that bake whatever the ModdedPrefabs checks say.");
            BatchingExcludedPrefabs = Config.Bind("Batching", "ExcludedPrefabs", "",
                "Comma-separated prefab names (exact, case-sensitive) that are never baked, instanced or skipped:\n" +
                "they always keep their own renderers. Add a prefab here when the bake draws it wrong. Mods can\n" +
                "opt their own pieces out without this list, through FiresCore's MeshBakeBridge.\n" +
                "Applies to pieces as they load; relog to rebake zones that already hold one.");
            BatchingExcludedPrefabs.SettingChanged += (_, __) =>
            {
                EasyBake.ZoneTracker.ReloadExcludedPrefabs();
                EasyBake.SkipEligibility.Clear();
            };
            CoreLink.OnExclusionsChanged(EasyBake.SkipEligibility.Clear);

            SkipBakedPieceObjects = Config.Bind("Batching", "SkipBakedPieceObjects", true,
                "Multiplayer clients only. Pieces a bake already draws are not created as game objects:\n" +
                "a stand-in collider takes each one's place, so they cost nothing per frame. Only\n" +
                "invulnerable, purely structural pieces qualify; anything that crafts, stores, lights,\n" +
                "protects, comforts or animates is always created. Real pieces come back while a build\n" +
                "tool is out nearby, or when a skipped piece is removed, moved or loses invulnerability.\n" +
                "On a joined client this is always allowed; for a host or single-player world see SkipOnHost.\n" +
                "By design (Fire): pieces touching a skipped infinite-health piece treat it as solid ground.");

            // 1.2.154 (Fire 10-30 choice (a)): SkipOnHost and SkipDamageablePieces default OFF and no longer moved to true by the 1.2.145
            // defaults-on migration (MigrateDefaultOn now has no callers); a saved value stays as the player set it.
            SkipOnHost = Config.Bind("Batching", "SkipOnHost", false,
                "Host and single-player worlds get the same skip a joined client has: pieces the bake already\n" +
                "draws are not created as game objects. A host renders exactly like a joined client; the one\n" +
                "difference is that it also owns the ZDOs, and vanilla's 'CreateObject returned null = delete it'\n" +
                "path is blocked for skipped pieces (ZDOMan_DestroyZDO_SkipGuard_Patch).\n" +
                "WARNING - support gap: on a host or single-player world a skipped piece is not a real object, so the\n" +
                "game never checks whether it is still supported. A piece that loses its support while skipped keeps\n" +
                "standing, and falls the moment it is created again (for example when you turn EasyBakeMeshes off).\n" +
                "Only invulnerable pieces are skipped unless SkipDamageablePieces is also on. Default off.\n" +
                "By design (Fire): pieces touching a skipped infinite-health piece treat it as solid ground.");
            SkipBakedPieceObjects.SettingChanged += (_, __) =>
            {
                if (!SkipBakedPieceObjects.Value) EasyBake.ZoneTracker.HandBackAllSkipped();
            };

            SkipDamageablePieces = Config.Bind("Batching", "SkipDamageablePieces", false,
                "Also leave DAMAGEABLE pieces uncreated when the bake already draws them: the largest win left,\n" +
                "about half of everything still created in a dense town. Damage keeps working - it is\n" +
                "owner-authoritative and routed, so the owner applies it, and a hit on a piece this client owns\n" +
                "but has not created is caught, the piece brought back, and the hit replayed into it. A damaged\n" +
                "piece stops matching its bake and becomes real.\n" +
                "WARNING - support gap: a breakable piece (chests, walls, floors...) that this client owns and has not\n" +
                "created takes no rain wear and gets no support check until something brings it back. A piece that\n" +
                "loses its support while skipped keeps standing, and falls the moment it is created again (for example\n" +
                "when you turn EasyBakeMeshes off). Needs SkipBakedPieceObjects. Default off.");
            SkipDamageablePieces.SettingChanged += (_, __) =>
            {
                EasyBake.SkipEligibility.DamageableSkipAllowed = SkipDamageablePieces.Value;
                // Turning it off has to UNDO it, not just stop adding: every damageable piece already skipped
                // would otherwise stay uncreated with the feature nominally off, which would make the A/B a
                // lie. Handing everything back is the blunt version and the correct one - the skip re-applies
                // to whatever still qualifies on the next pass, and by then the new value is what decides.
                // The prefab cache is NOT cleared, deliberately: this flag is read per ZDO in ZdoMatches and
                // nothing prefab-derived depends on it, so clearing would only re-walk every prefab for show.
                EasyBake.ZoneTracker.HandBackAllSkipped();
            };
            EasyBake.SkipEligibility.DamageableSkipAllowed = SkipDamageablePieces.Value;

            SkipVegetation = Config.Bind("Batching", "SkipVegetation", false,
                "Draw trees, shrubs and bushes from the zone's instanced groups and leave them\n" +
                "uncreated, the way SkipDamageablePieces does for building pieces. Measured at 4,566 live objects\n" +
                "in a town, of which 91% the instancing path can already draw. Unlike building pieces these have\n" +
                "NO periodic tick at all - TreeBase has none and an untimed Destructible has none - so there is no\n" +
                "wear or support work to stall. Chopping still works: the stand-in answers as the prefab's own\n" +
                "DestructibleType and routes RPC_Damage to the owner exactly as TreeBase.Damage does, and a hit on\n" +
                "one this client owns but has not created is caught and replayed. Vanilla already GPU-instances\n" +
                "this vegetation, so this saves the GameObject, not draw calls. Needs SkipBakedPieceObjects.");
            SkipVegetation.SettingChanged += (_, __) =>
            {
                EasyBake.SkipEligibility.VegetationSkipAllowed = SkipVegetation.Value;
                // Unlike SkipDamageablePieces, this one DOES need the prefab cache cleared: it widens the component
                // allow-list, and a prefab's Skippable verdict is cached per hash. Without this the flag reads as
                // doing nothing until the next scene load.
                EasyBake.SkipEligibility.Clear();
                EasyBake.ZoneTracker.HandBackAllSkipped();
            };
            EasyBake.SkipEligibility.VegetationSkipAllowed = SkipVegetation.Value;

            FinishDefaultOnMigration();

            BatchingBuildRadius = Config.Bind("Batching", "BuildToolRadiusMeters", 24f,
                new ConfigDescription(
                    "While a build tool is out, the real pieces come back within this many metres of you, so the\nhammer can snap to them, highlight them and take them down. Everything further away stays\nbaked. In a dense base this is the most expensive thing skipping does, so keep it near what\nyou can actually reach.",
                    new AcceptableValueRange<float>(4f, 256f)));

            PrewarmEnabled = Config.Bind("Prewarm", "Enabled", true,
                "Workstream B: at ZNetScene.Awake, instantiate every prefab in m_prefabs\n" +
                "once at (0, -1000, 0), SetActive(false), then Destroy. Pays the hidden\n" +
                "first-Instantiate shader-compile / asset-bundle cost (5-35ms per heavy\n" +
                "prefab) once during loading rather than at zone borders during gameplay.");

            PrewarmRunOnServer = Config.Bind("Prewarm", "RunOnServer", false,
                "Run prewarm on dedicated server too. Default off â€” server doesn't render\n" +
                "so it gains nothing from shader-variant warm-up.");

            PrewarmPrefabsPerFrame = Config.Bind("Prewarm", "PrefabsPerFrame", 25,
                new ConfigDescription(
                    "Batch size of the prewarm coroutine. 25 keeps each loading-screen\n" +
                    "frame under ~50ms even when a batch hits some 5ms heavy prefabs.\n" +
                    "Higher = faster total warm-up but choppier loading screen.",
                    new AcceptableValueRange<int>(1, 500)));

            PrewarmVerbose = Config.Bind("Prewarm", "VerboseLogging", false,
                "Log every prefab + per-prefab ms during warm-up. Off by default; the\n" +
                "summary line carries totals + worst-single only.");

            PrewarmSkipNameContains = Config.Bind("Prewarm", "SkipNameContains", "",
                "Comma-separated list of case-insensitive substrings. Any prefab whose\n" +
                "name contains one of these tokens is skipped during prewarm. Empty by\n" +
                "default â€” main-thread responsiveness is preserved by the time-budgeted\n" +
                "yield pattern (see TimeBudgetMs below), not by skipping. Use this\n" +
                "config only when a specific prefab is misbehaving badly enough that\n" +
                "you'd rather pay the first-instantiate cost in-game than at load.");

            PrewarmFrameBudgetMs = Config.Bind("Prewarm", "FrameBudgetMs", 8f,
                new ConfigDescription(
                    "Time-budgeted yield: after each Instantiate, if the elapsed wall\n" +
                    "time on THIS frame exceeds this many ms, the coroutine yields and\n" +
                    "the next prefab runs on the next frame. Keeps the main thread\n" +
                    "responsive (loading screen + any open dialogs stay interactive)\n" +
                    "at the cost of longer total wall time. A single very-slow prefab\n" +
                    "(e.g. ConsMassiveTower's ~30s first-Instantiate) will still freeze\n" +
                    "the frame it lands on â€” Unity's shader-variant compile is\n" +
                    "synchronous and can't be yielded mid-Instantiate â€” but every\n" +
                    "other prefab around it stops piling on. Lower = smoother but\n" +
                    "longer. 8ms â‰ˆ half a 16ms frame budget.",
                    new AcceptableValueRange<float>(1f, 100f)));

            PrewarmSlowInstantiateThresholdMs = Config.Bind("Prewarm", "SlowInstantiateThresholdMs", 5000f,
                new ConfigDescription(
                    "If a single prefab's Instantiate takes longer than this many ms,\n" +
                    "log a warning naming the prefab AND persist it to\n" +
                    "BepInEx/cache/FiresEasyBakeMeshes/prewarm_slow_skips.txt, so every future\n" +
                    "session skips it before instantiating (delete a line from that file\n" +
                    "to give a prefab another chance, e.g. after removing the mod that\n" +
                    "made it slow). 0 to disable the runtime detector entirely.",
                    new AcceptableValueRange<float>(0f, 60000f)));

            PrewarmMaxTransformCount = Config.Bind("Prewarm", "MaxTransformCount", 800,
                new ConfigDescription(
                    "Skip any prefab whose transform hierarchy exceeds this many nodes,\n" +
                    "WITHOUT instantiating it. A single Instantiate is atomic on the main\n" +
                    "thread â€” the frame budget cannot split it â€” and combined-build\n" +
                    "mega-prefabs (e.g. ConsMassiveTower, ~29s) freeze the whole client\n" +
                    "on their first-ever warm-up, which the slow-instantiate detector\n" +
                    "above can only prevent from the SECOND session on. The node count is\n" +
                    "a cheap capped traversal; skipped prefabs warm on first real spawn\n" +
                    "instead. Normal pieces are tens of nodes; only combined builds reach\n" +
                    "the hundreds. 0 to disable the gate.",
                    new AcceptableValueRange<int>(0, 100000)));

            MaterialRegistryFrameBudgetMs = Config.Bind("Cache", "MaterialRegistryFrameBudgetMs", 4f,
                new ConfigDescription(
                    "Time-budgeted yield for the material-registry build pass that\n" +
                    "runs on ZNetScene.Awake (BEFORE prewarm starts). The build walks\n" +
                    "every prefab's MeshRenderers to index sharedMaterials by name â€”\n" +
                    "on a 5000+ prefab world this can take seconds synchronously,\n" +
                    "and the freeze lands right around the password-prompt /\n" +
                    "loading-screen-first-frame window. Yielding every Nms keeps the\n" +
                    "main thread interactive during that build. Lower = smoother but\n" +
                    "more total wall time. 4ms is roughly a quarter of a 16ms frame.",
                    new AcceptableValueRange<float>(1f, 50f)));

            CachePersistEnabled = Config.Bind("Cache", "PersistToDisk", true,
                "Save baked meshes to BepInEx/cache/FiresEasyBakeMeshes/cache/<worldUid>/\n" +
                "and reload them on subsequent sessions of the same world. Eliminates the\n" +
                "per-zone Mesh.CombineMeshes cost on re-entry â€” disk read + Mesh.SetVertices\n" +
                "is ~4x faster than rebuilding from source pieces. Disable to always rebake\n" +
                "(useful when debugging or after admin-removing pieces, which the cache\n" +
                "can't detect and would leave as ghost geometry).");

            CreationCensusEnabled = Config.Bind("Diagnostics", "CreationCensusEnabled", false,
                "Counts what ZNetScene actually creates: objects per CreateObjects call against\n" +
                "the budget handed out, and a breakdown by prefab. Diagnostic only â€” it changes\n" +
                "no behaviour, but it hooks every CreateObject and keeps a dictionary, so leave\n" +
                "it OFF unless you are chasing creation cost. It is what found that ~59,000 of\n" +
                "61,194 CreateObject calls per 30s were creating nothing at all.");

            // 1.2.150 (Fire 10-30): DeferCreationWhileBaking removed (measured worse: 51.7 -> 20.1 fps).
            CreateBudgetPerFrame = Config.Bind("Optimize", "CreateBudgetPerFrame", 40,
                new BepInEx.Configuration.ConfigDescription(
                    "Ceiling on how many objects ZNetScene may instantiate in ONE frame.\n" +
                    "Vanilla's budget is Max(backlog/100, 10), so the more objects are waiting\n" +
                    "the bigger each frame's burst gets. On a dense world that inverts: with\n" +
                    "~24,000 objects queued the budget becomes 240 in a single frame, and at\n" +
                    "0.18ms each that is ~44ms of instantiation in one frame â€” measured as a\n" +
                    "122ms stall about once a second. This caps the burst WITHOUT lowering\n" +
                    "vanilla's own floor, so an area takes a few more frames to fill but fills\n" +
                    "smoothly. It does not reduce total work, only how lumpy it is.\n" +
                    "0 = off (pure vanilla). Lower = smoother but slower to populate.",
                    new BepInEx.Configuration.AcceptableValueRange<int>(0, 500)));

            CreateBudgetReleaseAbove = Config.Bind("Optimize", "CreateBudgetReleaseAbove", 8000,
                new BepInEx.Configuration.ConfigDescription(
                    "Candidate count above which CreateBudgetPerFrame STANDS DOWN and vanilla's\n" +
                    "own catch-up budget runs again.\n" +
                    "\n" +
                    "A ceiling on creates-per-frame is paid against a backlog that is re-sorted\n" +
                    "IN FULL every frame it survives. Creating an object costs 0.183ms ONCE;\n" +
                    "leaving it queued costs its share of an O(n log n) sort EVERY frame. In a\n" +
                    "built-up settlement the queue reached 56,878 candidates: vanilla would have\n" +
                    "allowed 568 creates a frame, the cap allowed 40, and at the ~10fps that\n" +
                    "produced the drain rate was 400/s - slower than a walking player queues\n" +
                    "them. The queue then never clears and the sort bill is permanent, which is\n" +
                    "the opposite of what the cap is for. The same trap was already measured at\n" +
                    "spawn (142s stuck waiting on areaReady) and fixed there by lifting the cap\n" +
                    "until the player exists; this is the in-play half of it.\n" +
                    "0 = never release (the cap always binds). Higher = tolerate a longer queue.",
                    new BepInEx.Configuration.AcceptableValueRange<int>(0, 200000)));

            // 1.2.151 ([fgn] / lead 10-30: Fire's town walk sorted ~12k waiting objects every frame, ~13.6 ms)
            PartialCreateSort = Config.Bind("Optimize", "PartialCreateSort", true,
                "When many objects are waiting to be created, the game sorts ALL of them by distance every frame but only creates the nearest few. " +
                "With this on, only the nearest ones (512, or four times this frame's create budget) are sorted and put first; the rest wait unsorted and are ranked again next frame. " +
                "Same objects created in the same nearest-first order; the per-frame sort of a long queue is gone.");

            CandidateCensusSeconds = Config.Bind("Optimize", "CandidateCensusSeconds", 15f,
                new BepInEx.Configuration.ConfigDescription(
                    "How often to explain the create queue, in seconds, when it is over 500 deep.\n" +
                    "\n" +
                    "Sorts every queued ZDO into the reason it has no instance, and separates the\n" +
                    "ones vanilla is about to create anyway from the ones EBM is really\n" +
                    "withholding. Only the DeferCreationWhileBaking hold does the latter, so with\n" +
                    "that off (the default) the report should say the queue is budget, all of it.\n" +
                    "It then names why the skip passed each piece over - prefab can never be\n" +
                    "skipped, not in their zone's bake, no longer matches what was baked, zone\n" +
                    "holding its real pieces, and so on - which sizes how much of the queue the\n" +
                    "skip could ever take. Also reported: zones not ready for the type (vanilla's\n" +
                    "own skip), prefabs ABSENT from this client, and no prefab set.\n" +
                    "\n" +
                    "The reason comes from the same predicate the skip decides on, so the two\n" +
                    "cannot disagree. Before 1.2.73 this asked only whether the prefab CLASS was\n" +
                    "bake-eligible, which is the first of eight tests, so pieces the skip had\n" +
                    "already declined were reported as held back when they were waiting on\n" +
                    "budget - the one number the report exists to give.\n" +
                    "\n" +
                    "The queue sat at 24,187 in town and two separate readings of the code got\n" +
                    "the reason wrong, so it is worth measuring rather than inferring. Costs one\n" +
                    "walk of the queue per report and nothing in between; the report says how\n" +
                    "long that walk took.\n" +
                    "0 = off.",
                    new BepInEx.Configuration.AcceptableValueRange<float>(0f, 300f)));

            CandidateCensusSample = Config.Bind("Optimize", "CandidateCensusSample", 4000,
                new BepInEx.Configuration.ConfigDescription(
                    "Most queued ZDOs the census walks per report. Above this it takes every k-th\n" +
                    "and scales the counts back up.\n" +
                    "\n" +
                    "Measured uncapped in town on 1.2.73: 83.8 / 66.5 / 58 / 31.5 ms per report\n" +
                    "against queues up to 56,568 - a third of a 183 ms frame, spent by the tool\n" +
                    "whose only job is to explain that frame. The cost does NOT follow queue\n" +
                    "length: 56,568 entries cost 66.5 ms while a 2,364 queue cost 0.4 ms, because\n" +
                    "those all short-circuited early. It is the deepest two buckets that cost, so\n" +
                    "bounding the number walked is what bounds the report.\n" +
                    "\n" +
                    "The sample is unbiased: vanilla sorts the candidate list AFTER the budget\n" +
                    "call the census runs inside, so the list is still in near-walk order and\n" +
                    "every k-th entry samples the whole queue rather than its nearest slice.\n" +
                    "Counts become estimates and the report says so, naming the stride.\n" +
                    "0 = no cap (walk everything, exact counts, the numbers above).",
                    new BepInEx.Configuration.AcceptableValueRange<int>(0, 200000)));

            CreateDestroySkipEnabled = Config.Bind("Optimize", "CreateDestroySkipEnabled", true,
                "Skip ZNetScene.CreateDestroyObjects when the player hasn't crossed a sector\n" +
                "boundary AND no zone has loaded/unloaded since the last call. Vanilla does\n" +
                "a full O(loaded ZNetView) walk every 33ms; at a megabase that's ~6 ms/tick\n" +
                "of pure overhead finding zero things to remove. Skipping reclaims that for\n" +
                "the steady-state case (stationary at base). Set false to use pure vanilla.");

            CreateDestroySkipMaxSeconds = Config.Bind("Optimize", "CreateDestroySkipMaxSeconds", 1.0f,
                new ConfigDescription(
                    "Maximum time to skip CreateDestroyObjects even when nothing has changed.\n" +
                    "Forces a vanilla pass at this cadence so server-pushed ZDOs (creature\n" +
                    "spawns, remote player drops) eventually get instantiated on the client.\n" +
                    "Lower = more responsive to network events, less optimization gain.",
                    new AcceptableValueRange<float>(0.1f, 10f)));

            CreateDestroyDirtyFlag = Config.Bind("Optimize", "CreateDestroyDirtyFlag", true,
                "With CreateDestroySkipEnabled: skip the create / destroy pass until an event says the nearby objects changed, instead of comparing a world-wide object count. A real pass still runs at least every CreateDestroySkipMaxSeconds. Off = the old count.");

            CreateDestroySkipVerbose = Config.Bind("Optimize", "CreateDestroySkipVerbose", false,
                "Log a periodic summary of how many CreateDestroyObjects ticks we skipped.");

            WearShortCircuitEnabled = Config.Bind("Optimize", "WearShortCircuitInvulnerable", true,
                "Skip WearNTear.UpdateWear and UpdateCover for invulnerable pieces.\n" +
                "Invulnerable pieces (m_health < 0 or all-Immune damage modifiers)\n" +
                "can't take damage, so vanilla's per-piece wet/roof/support/biome/lava\n" +
                "computation is wasted work â€” CanBeRemoved() zeros the accumulated\n" +
                "damage at the bottom of the method. At a megabase WearNTearUpdater is\n" +
                "the single largest per-frame cost; this short-circuit reclaims roughly\n" +
                "30 ms/sec. Disable only if a mod conflict requires vanilla behavior.");

            // 1.2.86 (Fire, 10-05, from a player): off by default. An existing cfg keeps the value it already has.
            ZoneKeepaliveEnabled = Config.Bind("Optimize", "ZoneKeepaliveEnabled", false,
                "Keep baked zones loaded after you walk out of them, instead of letting\n" +
                "the game unload them and load them again when you come back.\n" +
                "COST: every pinned zone's objects stay in memory and stay live, up to\n" +
                "(2 x ZoneKeepaliveRadius + 1)^2 zones (961 at radius 15) for\n" +
                "ZoneKeepaliveSeconds after you leave. That is more RAM, and the game\n" +
                "keeps working on objects you are no longer near.\n" +
                "TURN ON for a big base or town you walk in and out of all the time,\n" +
                "where re-entering a zone hitches for hundreds of ms while it rebuilds,\n" +
                "and you have memory to spare. Leave off otherwise. Only works on a\n" +
                "client where baking skips object creation (never on a server).");

            ZoneKeepaliveRadius = Config.Bind("Optimize", "ZoneKeepaliveRadius", 15,
                new ConfigDescription(
                    "Max zone-distance from the player to keep baked zones pinned. Beyond\n" +
                    "this radius the zone is released and vanilla destroys its instances.\n" +
                    "Each zone is 64m. Default 15 = 960m radius â€” wide enough to cover\n" +
                    "Render Limits' extended view (typically +12 zones) so zones at the\n" +
                    "edge of the rendered area don't get destroyed when you walk around\n" +
                    "within town. With ~30 baked zones at a megabase the memory cost is\n" +
                    "bounded by the bake set, not the radius. Raise to 25-50 for very\n" +
                    "wide cities; drop to 5 for stock-vanilla active area.",
                    new AcceptableValueRange<int>(1, 50)));

            ZoneKeepaliveSeconds = Config.Bind("Optimize", "ZoneKeepaliveSeconds", 60f,
                new ConfigDescription(
                    "How long a baked zone stays pinned after the player last had it in\n" +
                    "the keepalive radius. After this, even though it's still tracked,\n" +
                    "the zone is released and vanilla destroys its instances on the next\n" +
                    "CreateDestroyObjects tick. Prevents stale pins from accumulating\n" +
                    "during long travel sessions.",
                    new AcceptableValueRange<float>(5f, 600f)));

            ZoneKeepaliveVerbose = Config.Bind("Optimize", "ZoneKeepaliveVerbose", false,
                "Log a periodic summary of how many zones are pinned and the refresh /\n" +
                "prune deltas. Useful for tuning the radius when paired with the\n" +
                "FiresDebugginTools profile output.");

            DestroyTimeSliceEnabled = Config.Bind("Optimize", "DestroyTimeSliceEnabled", true,
                "Replace ZNetScene.RemoveObjects' single-tick destroy loop with a queued,\n" +
                "time-budgeted version. Vanilla destroys every unearmarked instance in\n" +
                "one frame; at megabase scale with Render Limits expanding the loaded\n" +
                "ring, a single zone-cross can drop 10k+ pieces and produce a 100+ms\n" +
                "frame hitch. Time-slicing spreads the destroys across multiple frames\n" +
                "so the work is paced rather than spiked. Same total CPU cost, far\n" +
                "smoother frame distribution. Disable to use vanilla destroy semantics.");

            DestroyTimeSliceBudgetMs = Config.Bind("Optimize", "DestroyTimeSliceBudgetMs", 3f,
                new ConfigDescription(
                    "Per-frame ms budget for the destroy queue. Higher = drains faster\n" +
                    "but allows bigger spikes; lower = smoother frames but the queue\n" +
                    "takes more wall-clock time to drain. Default 3ms keeps a 10k-piece\n" +
                    "unload at ~5ms peak frame impact and drains in ~3 seconds.",
                    new AcceptableValueRange<float>(0.5f, 20f)));

            DestroyTimeSliceVerbose = Config.Bind("Optimize", "DestroyTimeSliceVerbose", false,
                "Log a periodic summary of destroyed / re-earmark-skipped counts and\n" +
                "current queue depth. Useful for confirming the slicer is draining at\n" +
                "the configured rate and not falling perpetually behind.");

            SectorMirrorEnabled = Config.Bind("Optimize", "SectorMirrorEnabled", true,
                "Maintain a per-sector instance-count mirror of ZNetScene.m_instances and\n" +
                "intercept ZNetScene.HaveInstanceInSector with an O(1) dictionary lookup.\n" +
                "Vanilla walks every entry in m_instances (146k+ at megabase) for each\n" +
                "ZoneSystem.UpdateTTL zone check â€” profile shows ZoneSystem.Update\n" +
                "spiking to 80+ ms/call during in-town movement. The mirror eliminates\n" +
                "that scan. Staleness from instances that move sectors after spawn\n" +
                "(creatures, ships) is corrected by a periodic full rebuild.");

            SectorMirrorRebuildSeconds = Config.Bind("Optimize", "SectorMirrorRebuildSeconds", 60f,
                new ConfigDescription(
                    "How often to rebuild the sector mirror from scratch. The rebuild\n" +
                    "walks m_instances once (cost similar to one vanilla HaveInstanceInSector\n" +
                    "call) and corrects any drift from instances that moved sectors since\n" +
                    "instantiation. Lower = tighter accuracy, slightly more cost; higher =\n" +
                    "longer-lived false positives (zones held alive after their creature\n" +
                    "wandered away). 60s is the default sweet spot. Set 0 to disable rebuilds\n" +
                    "entirely (only safe for fully-static worlds).",
                    new AcceptableValueRange<float>(0f, 600f)));

            SectorMirrorVerbose = Config.Bind("Optimize", "SectorMirrorVerbose", false,
                "Log a line each time the sector mirror rebuilds, showing tracked ZDO\n" +
                "and sector counts. Useful for verifying the mirror is staying populated.");

            // 1.2.136 ([visual], Fire 10-27 YES): far piece lights vertex-lit, no shadows (EasyBake/LightBudget.cs)
            // 1.2.157 ([visual], Fire 10-31 "Build (b), A/B"): OFF until the A/B says it helps (never pin an unproven setting on)
            ArrivalCountsBakedPieces = Config.Bind("Optimize", "ArrivalCountsBakedPieces", true,
                "When you spawn or teleport into a big build, building pieces that are already drawn by a bake and already solid count as loaded, so the loading screen does not wait for the game to walk through every one of them first. Off = wait as before.");
            SpreadFirstSightChecks = Config.Bind("Optimize", "SpreadFirstSightChecks", false,
                "When many new kinds of building piece appear in one frame (login, a big base coming into view), check at most about 4 ms of them per frame; the rest are created as normal pieces and baked a moment later. Lowers the worst login frame.");
            LightBudgetEnabled = Config.Bind("Optimize", "LightBudgetEnabled", true,
                "Only the nearest 8 lights on build pieces within 20 m of the camera keep full per-pixel lighting and shadows; every other piece light is vertex-lit with no shadows (a large FPS gain in lit bases; no visible change at a distance). Torches, fires and station lights only; never the sun, players or creatures.");

            // 1.2.149 ([visual], Fire 10-30 picked option C for the shadow A/B; OFF until the A/B and his look check)
            FarSmallNoShadows = Config.Bind("Optimize", "FarSmallNoShadows", false,
                "Baked (instanced) build pieces far from the camera, or small ones, draw without casting a sun shadow. Near pieces keep their shadows; the far ones are still lit and shaded, they only stop throwing a shadow. Cuts the shadow passes in big bases. Off until tested.");
            NoShadowBeyondMeters = Config.Bind("Optimize", "NoShadowBeyondMeters", 40f,
                new ConfigDescription("With FarSmallNoShadows: a group of baked pieces whose nearest point is farther than this casts no shadow.", new AcceptableValueRange<float>(10f, 200f)));
            NoShadowSmallerThanMeters = Config.Bind("Optimize", "NoShadowSmallerThanMeters", 1f,
                new ConfigDescription("With FarSmallNoShadows: a baked piece whose mesh is smaller than this (bounds diagonal, metres) casts no shadow at any distance. 0 = size never matters.", new AcceptableValueRange<float>(0f, 5f)));
            // 1.2.186 (lead 11-08): defaults keep today's behaviour; flip only after the Debug [Draw] "Split:" numbers prove it.
            InstancedFollowChecksEveryFrame = Config.Bind("Optimize", "InstancedFollowChecksEveryFrame", true,
                "Baked (instanced) groups ask every frame, for every part, whether their source renderer or material changed (each check acts at most once a second). Off: asked once a second per group instead. On = as before.");
            InstancedOtherCameraRedraw = Config.Bind("Optimize", "InstancedOtherCameraRedraw", true,
                "Baked (instanced) groups out of the main view are drawn again for other game / reflection cameras that see them (live map, water reflections). Off: only the main view and roof groups draw. On = as before.");
            // 1.2.195 (TOWN50, [classes-ui]/lead 11-08): A/B key, 0 = as before until the [Draw] numbers prove it
            InstancedCellSizeMeters = Config.Bind("Optimize", "InstancedCellSizeMeters", 0f,
                new ConfigDescription("Baked (instanced) groups are drawn in runs of nearby pieces, one draw per cell of this size (metres) with its own bounds, so the camera and each sun-shadow cascade skip the cells they cannot see. Same look; more draw calls, fewer triangles. 0 = one bounds per group (as before). Applies live (on each group's next re-split).",
                    new AcceptableValueRange<float>(0f, 128f)));
            // 1.2.196 (DESIGN_OcclusionLOD 2b / 2c, lead approved 11-08): A/B keys, off = as 1.2.195; both act on cell runs only
            InstancedCellLod = Config.Bind("Optimize", "InstancedCellLod", false,
                "Baked (instanced) pieces use the prefab's own LOD meshes by distance, per cell run, at the same switch points the game's LODGroup uses for a live piece (and are not drawn where the game would cull the piece). Off = the full mesh at every distance (EBM's zone-wide far mesh as before).");
            InstancedShadowProxies = Config.Bind("Optimize", "InstancedShadowProxies", false,
                "Baked (instanced) pieces cast their sun shadow from one LOD lower than they draw (the lowest when there is none lower), in a separate shadow-only draw. Off = every draw casts its own shadow (as before).");
            // 1.2.200 (DESIGN_OcclusionLOD, lead approved 11-08): A/B key, off = as 1.2.199
            // 1.2.201 ([ghost], DESIGN_OcclusionLOD §5, lead approved 11-08): A/B key, off = vanilla rocks
            BakeIntactRocks = Config.Bind("Optimize", "BakeIntactRocks", false,
                "Undamaged fractured rocks (rock formations you can mine) share one mesh per kind instead of a copy each, and far away draw a simplified version (made by Core's simplifier; needs FiresUnifiedCore 0.2.1212+). The first hit on a rock gives it back the game's own mesh. Colliders are never changed. Off = vanilla rocks.");
            BakeIntactRocks.SettingChanged += (_, __) => { if (!BakeIntactRocks.Value) EasyBake.IntactRockLod.RestoreAll(); };
            IntactRockLod1ScreenHeight = Config.Bind("Optimize", "IntactRockLod1ScreenHeight", 0.8f,
                new ConfigDescription("Screen height (0..1, before the game's LOD bias) below which an undamaged rock draws its simplified version. Higher = it switches sooner.", new AcceptableValueRange<float>(0.05f, 0.99f)));
            IntactRockLod1CellsAcross = Config.Bind("Optimize", "IntactRockLod1CellsAcross", 16,
                new ConfigDescription("Detail of the simplified far rock: grid cells across the rock's longest side (16 = about 1.7 m cells on a big mountain rock). Higher = closer to the full mesh, more triangles.", new AcceptableValueRange<int>(4, 256)));
            // 1.2.203 (Fire 11-08: "make our mod get us 100+"; the VPO gap at 615): EBM's GPU-driven path, A/B key, default off
            IndirectRenderer = Config.Bind("Optimize", "IndirectRenderer", false,
                "Baked pieces (and baked trees / rocks) are drawn GPU-driven: every loaded zone's instances in one buffer, culled per instance on the GPU for the camera and the sun's shadows (the prefab's own LOD levels as vanilla picks them), one draw per mesh and material for the whole area instead of one per zone. Needs the ebm_indirect bundle next to the DLL; anything it cannot draw (snow / dry-under-roof ranges, a shader with no variant) stays on the instanced path. Off = as before.");
            IndirectCullGrouped = Config.Bind("Optimize", "IndirectCullGrouped", false,
                "With IndirectRenderer on: the GPU cull counts each thread group's visible instances per batch together and writes the shared counter once per batch, instead of once per visible instance (busy batches no longer queue on one counter). Same instances drawn. Off = as before.");
            IndirectLodPerInstance = Config.Bind("Optimize", "IndirectLodPerInstance", false,
                "With IndirectRenderer on: the LOD level window travels with each instance instead of splitting batches by it, so one mesh + material is one batch whatever its LOD setup (fewer draws and command-buffer entries). Takes effect for batches made after it changes (a zone reload). Same pieces drawn at the same distances. Off = as before.");
            IndirectOneCullPass = Config.Bind("Optimize", "IndirectOneCullPass", false,
                "With IndirectRenderer on: one GPU cull pass a frame reads every instance once for the camera, the sun, the point / spot lights and up to 4 other cameras (reflections, sky depth, water), instead of a separate pass for the lights and for each other camera. A camera that moved after the main cull still gets its own pass. Same instances drawn. Off = as before.");
            IndirectCascadeShadows = Config.Bind("Optimize", "IndirectCascadeShadows", false,
                "With IndirectRenderer on: each sun shadow cascade draws only the baked casters that can reach it (a per-cascade GPU cull, drawn from the light's own command buffers), instead of one list drawn into every cascade. Off = as before.");
            IndirectShadowReceiverCull = Config.Bind("Optimize", "IndirectShadowReceiverCull", true,
                "With IndirectCascadeShadows on: a baked caster is drawn into a sun cascade only when its shadow can land on a surface the camera sees (a map of the visible ground per cascade, built from the camera's depth). Never drops on doubt: held off for a frame when the camera or sun moved too far. Off = as before.");
            IndirectShadowSameFrame = Config.Bind("Optimize", "IndirectShadowSameFrame", false /* 1.2.211: OFF until Fire A/Bs it (shadow stability; lead) */,
                "With IndirectCascadeShadows on: the sun cascade casters are culled inside this frame's shadow pass, with Unity's own cascade spheres and a receiver map from this frame's depth (no one-frame lag). Off = the 1.2.208 cascade test in the Update cull.");
            IndirectShadowPlaceholders = Config.Bind("Optimize", "IndirectShadowPlaceholders", true,
                "With IndirectCascadeShadows on: one empty shadow draw a frame (and one per shadowed point / spot light) so Unity never skips a shadow pass that only EBM's baked casters fill. Off = as 1.2.210.");
            IndirectShadowSizeCut = Config.Bind("Optimize", "IndirectShadowSizeCut", 2f, new ConfigDescription(   // 1.2.209: default ON (2 texels, FAP's value; Fire: perf default on)
                "With IndirectCascadeShadows on: a baked caster smaller than this many texels of a sun cascade's shadow map is not drawn into that cascade (it would be a speck). 0 = off.",
                new AcceptableValueRange<float>(0f, 8f)));
            // 1.2.213 ([seasons], Tools\DESIGN_EbmGrass_20261108.md): vanilla grass drawn by EBM on the GPU (A/B key; default off until the look check by Fire)
            IndirectGrass = Config.Bind("Optimize", "IndirectGrass", false,
                "Grass is drawn by EBM on the graphics card: the blades of each patch are kept in GPU memory, the fade and view test of the game run on the GPU, and each kind of grass is one draw (the same grass). Off = the game draws grass, one draw per patch.");
            IndirectGrassUploadMs = Config.Bind("Optimize", "IndirectGrassUploadMs", 0.5f, new ConfigDescription(
                "With IndirectGrass on: milliseconds a frame for sending new or changed grass patches to the GPU (the rest wait a frame).", new AcceptableValueRange<float>(0.1f, 4f)));
            // [seasons] 11-09 (DROP_NOTES_seasons_grass2.md): E12/E13 carry, E15 cost levers
            IndirectGrassFadeCarry = Config.Bind("Optimize", "IndirectGrassFadeCarry", true,
                "With IndirectGrass on: grass that is rebuilt where you can see it (snow trails, terrain edits) carries on from what was drawn instead of fading in again, and never goes blank for a frame. Off = every rebuilt patch fades in from nothing, as the game does.");
            IndirectGrassStepGate = Config.Bind("Optimize", "IndirectGrassStepGate", true,
                "With IndirectGrass on: a footstep in deep snow (or any paint-only ground change) that cannot change any grass does not rebuild the grass around it. Grass still disappears at once where a trail really clears it. Off = every step rebuilds, as the game does.");
            IndirectGrassStepGateVerify = Config.Bind("Debugging", "IndirectGrassStepGateVerify", false,
                "Testing aid: the step gate skips nothing; instead the grass it would have skipped is rebuilt and compared blade for blade (Debug census line: 'differed' must stay 0).");
            IndirectGrassArrayRecycle = Config.Bind("Optimize", "IndirectGrassArrayRecycle", false,
                "With IndirectGrass on: grass patches whose blades are already on the graphics card give their 64 KB lists to a small pool (8 MB at most, trims itself) that new patches reuse, so walking makes less garbage. Turning EBM grass off afterwards rebuilds all grass once.");
            GrassPatchSize = Config.Bind("Optimize", "GrassPatchSize", 0f, new ConfigDescription(
                "0 = the game's 8 m. Bigger grass patches mean fewer patches to build and draw; the blade count per patch is raised so the density stays. Visible: blades sit in different places, very dense kinds can thin out (1023 blades per patch at most), each snow step or terrain edit rebuilds a bigger patch, and the distance fade moves in coarser steps. Rebuilds all grass once when changed.",
                new AcceptableValueRange<float>(0f, 32f)));
            GrassFarSwayStop = Config.Bind("Optimize", "GrassFarSwayStop", 0f, new ConfigDescription(
                "0 = off. With IndirectGrass on: grass further than this many metres stops swaying in the wind (eases out over the last 4 m). Visible: distant grass stands still. Saves graphics-card work only.",
                new AcceptableValueRange<float>(0f, 200f)));
            IndirectCoverage = Config.Bind("Optimize", "IndirectCoverage", false,
                "With IndirectRenderer on: LIVE trees, rocks, bushes and pickables in loaded zones (the ones EBM does not bake) are drawn through the same GPU-driven batches; the objects stay real, only their renderers are hidden while covered ([ghost]). Off = they draw as vanilla.");
            IndirectCoverageDisableRenderers = Config.Bind("Optimize", "IndirectCoverageDisableRenderers", true /* 1.2.211: ON (O trend: covered-but-enabled 21k -> 79k as main 15 -> 26 ms) */, "1.2.210: with IndirectCoverage, the renderers EBM covers are switched off (enabled = false), which takes them out of Unity's per-frame culling walk, instead of only hidden (forceRenderingOff, still culled every frame). Anything that turns a covered renderer back on hands that object back to vanilla. Off until the A/B proves it.");
            IndirectSparseUpload = Config.Bind("Optimize", "IndirectSparseUpload", true, "1.2.210: the GPU instance buffers get only the slots that changed (runs split at gaps), never the whole span between the lowest and highest change (a piece built in a reused low slot could send tens of MB in one frame). Read live.");
            IndirectStableLayout = Config.Bind("Optimize", "IndirectStableLayout", true, "1.2.210: each batch keeps its own visible range; one that outgrows it moves alone (only its draws re-bind) and a new batch takes a free row, instead of a full relayout that re-binds every draw and re-records every shadow command buffer. Read live (a flip relayouts once).");
            IndirectCasterCpuCull = Config.Bind("Optimize", "IndirectCasterCpuCull", true, "1.2.210: with IndirectCascadeShadows, each sun cascade and each point / spot light records only the batches whose zone cells can reach it (CPU cell cull, same frame, so nothing pops), in material order, and re-records only when it gains a batch or a quarter of its list went stale. Off = every batch into every caster buffer (1.2.209). Read live.");
            IndirectVertexDebug = Config.Bind("Optimize", "IndirectVertexDebug", false, "1.2.243, a test tool: EBM's piece and rock shaders paint a bad vertex a solid colour. Orange index, red input, magenta matrix, yellow world position, white clip, cyan w. Leave off to play.");
            IndirectCascadeEmptySkip = Config.Bind("Optimize", "IndirectCascadeEmptySkip", false, "1.2.242: a sun cascade stops recording a batch that drew nothing for 2 s, standing still. It returns as soon as the GPU counts an instance for it, or the camera moves. Off until measured.");
            IndirectCellsJob = Config.Bind("Optimize", "IndirectCellsJob", false, "1.2.241: the CPU cell cull (with IndirectFusedCells) walks the cells on Unity's worker threads, the game thread waiting and helping. Same answer; it helps while moving and loading, when the cull cannot reuse last frame. Off until measured.");
            IndirectSettledWalk = Config.Bind("Optimize", "IndirectSettledWalk", false, "Baked groups are walked only when something changed (a zone loads or unloads, a piece changes, snow or rain state changes), instead of every group every frame. Same draws. Off until measured.");
            IndirectCameraEmptySkip = Config.Bind("Optimize", "IndirectCameraEmptySkip", false, "While standing still, the camera stops submitting a batch the GPU has drawn nothing of for 2 s. It returns when the GPU counts an instance for it, or when the camera moves or a piece changes. Off until measured.");
            IndirectDrawParamsCache = Config.Bind("Optimize", "IndirectDrawParamsCache", false, "1.2.241: each indirect batch keeps its draw settings between frames instead of filling them again. Same draws. Off until measured.");
            IndirectFusedCells = Config.Bind("Optimize", "IndirectFusedCells", true, "1.2.213: the CPU cell cull judges the camera, the sun volume, the sun cascades, the point / spot lights and their cube faces in ONE walk over the cells (each cell and entry tested once), instead of one walk per kind; empty cells are dropped every few seconds after a zone unloads. Same verdicts. Read live; off = as 1.2.212.");
            IndirectCachedAlive = Config.Bind("Optimize", "IndirectCachedAlive", true, "1.2.213: whether a batch's mesh / material still exists (another mod can destroy them) is asked once a frame per batch and per piece set, not again in every per-frame loop. Read live; off = as 1.2.212.");
            IndirectLocalSteady = Config.Bind("Optimize", "IndirectLocalSteady", true, "1.2.213: a point / spot light's caster buffers stay attached for a moment when the light drops out of EBM's list for a frame, and a buffer is re-recorded only when ITS list gained a batch, a quarter went stale, or one of its own batches moved (not every buffer whenever any batch got its first instance). Read live; off = as 1.2.212.");
            IndirectLightsSwept = Config.Bind("Optimize", "IndirectLightsSwept", false, "Point and spot lights are checked for shadows a slice of the list per frame, and only the ones that can cast are checked every frame. Off: every light every frame. A light gaining shadows may start up to 0.2 s later, inside its fade-in.");
            IndirectCascadeTightStride = Config.Bind("Optimize", "IndirectCascadeTightStride", false, "The sun cascade caster buffers are sized to the space in use instead of the next power of two, so less is copied on the GPU each frame. Off: the old size.");
            IndirectSyncCleanup = Config.Bind("Optimize", "IndirectSyncCleanup", false, "A group that changed compares its instances in place and only looks up the ones that moved, and its lists are sized once. Same result with less work and less garbage while zones load. Off: the old way.");
            IndirectStagedSync = Config.Bind("Optimize", "IndirectStagedSync", false, "A big group joining the GPU-driven path for the first time (256+ instance slots) is prepared on a worker thread, filled at most 2 ms a frame, and switched over in one frame. Until then it draws the old instanced way, so a zone loading no longer stalls one frame. Off: registered whole on the frame it appears.");
            IndirectPointFaces = Config.Bind("Optimize", "IndirectPointFaces", true,"1.2.210: a point light's indirect casters are recorded per cube face (six buffers, each only the batches that face can see) with the shadow caster depth bias, instead of every caster into all six faces. Read live.");
            IndirectExactCascadeSpheres = Config.Bind("Optimize", "IndirectExactCascadeSpheres", false /* 1.2.211: OFF until Fire A/Bs the main-camera-only capture */, "1.2.224: NO EFFECT (the GPU capture was removed: its shadow-pass write corrupted piece geometry). 1.2.210 ([seasons] G5): the per-cascade caster cull uses Unity's own shadow split spheres (captured on the GPU each frame, grown by the camera's move since) instead of EBM's estimate (camera slices x 1.25 + 2 m), so fewer casters go into each cascade. Read live; off = the estimate.");
            IndirectCoverageDisablePrefabs = Config.Bind("Optimize", "IndirectCoverageDisablePrefabs", false, "1.2.210 ([ghost] step 2): with IndirectCoverage, the prefab parts coverage draws anyway are switched off once on the PREFAB, so every copy is born out of Unity's culling (no per-copy switch). A copy coverage does not draw (placement ghost, refused, baked) gets its parts switched back on at once; coverage or this key off switches every prefab part back on. Off until the A/B (none / IndirectCoverageDisableRenderers / this) proves it.");
            IndirectCoverageReach = Config.Bind("Optimize", "IndirectCoverageReach", false, "1.2.214 ([ghost] P2): with IndirectCoverage, a covered object the camera is past the last LOD level of (where it is drawn no more) gives its GPU slots back and stays hidden, and takes them again before it can be seen (re-checked every 8 m of camera movement, by 64 m cell). Objects with no LOD end are never parked. Same picture, fewer instances to cull. Off until the A/B; live through ebm_opt.");
            IndirectCoverageLodLevels = Config.Bind("Optimize", "IndirectCoverageLodLevels", false, "1.2.218 ([ghost] P3): with IndirectCoverageReach, a covered object holds GPU slots only for the LOD level(s) the camera is near (each level is added back before the camera can reach its distance), instead of every level at once. Same picture, fewer instances to cull. Off until the A/B; live through ebm_opt.");
            IndirectCoverageLocationProps = Config.Bind("Optimize", "IndirectCoverageLocationProps", false, "1.2.216 ([ghost]): with IndirectCoverage, static LOCATION props (dvergr town crates, tables, beams, goblin roofs and fences, pillars, statues: network objects that are not player pieces) are drawn through the indirect batches too, when nothing can change their look (no creature, Animator, light or fire, door, container, portal, ship, cart, stand, spawner, terrain or moving body). Off until the census proves it; then default on. Live through ebm_opt.");
            FarBuildingsChangedZonesOnly = Config.Bind("Optimize", "FarBuildingsChangedZonesOnly", false, "1.2.216 ([ghost]): the far-buildings digest scans a zone again only when its object count changed since its last scan (a piece placed, removed, received or unloaded), plus a safety rescan every 60 s, instead of rescanning every loaded zone round-robin (~1,400 a second in a big town). Same far buildings. Off until the A/B; live through ebm_opt.");
            IndirectGpuGrow = Config.Bind("Optimize", "IndirectGpuGrow", true, "1.2.213: when the instance buffers grow, the old contents are copied on the GPU and only the changed slots are sent, instead of every slot again from the CPU (tens of MB in one frame at a town). Read live; needs the 1.2.213 bundle (an older bundle uses the CPU path).");
            IndirectCompactSlots = Config.Bind("Optimize", "IndirectCompactSlots", true, "1.2.213: freed instance slots are reused lowest first and the free top is trimmed, so the GPU cull runs over fewer slots after objects are handed back. Read live.");
            IndirectCellFastSkip = Config.Bind("Optimize", "IndirectCellFastSkip", true, "1.2.214: in the CPU cell walk, an entry whose batch is already marked in every view it could add to is skipped before its distance tests, and a cell wholly inside a camera view skips its entries' plane tests. Exact (the same draws). Read live.");
            SectorMirrorSlicedRebuild = Config.Bind("Optimize", "SectorMirrorSlicedRebuild", true, "1.2.225 ([ghost]): the once-a-minute sector mirror rebuild walks the instances 8,000 a frame into staging tables and swaps them in at the end, instead of all ~135k in one frame (a ~115 ms hitch every minute in a big town). Same mirror; live through ebm_opt.");
            PhysicsCensus = Config.Bind("Diagnostics", "PhysicsCensus", false, "1.2.222 ([ghost]): with Debug logging, ONE scene search of rigidbodies, ZSyncTransforms and colliders, 60 s after the local player spawns; logs the counts and the cost of that one frame. Never repeats. Off for clean perf runs.");
            IndirectIntegrityCheck = Config.Bind("Diagnostics", "IndirectIntegrityCheck", false, "1.2.226 ([visual], the stretch hunt): the GPU indirect args (every 1 s) and instance matrices (8,192 slots a second, rolling) are read back and compared with what EBM uploaded; a mismatch logs an Error with the frame. Default on until the stretch is closed.");
            IndirectCascadeCounts = Config.Bind("Diagnostics", "IndirectCascadeCounts", false, "1.2.244: with Debug logging, the [Cascades] line: what each sun cascade really draws, read back from the GPU twice a second.");
            FarBuildingsQuietPolling = Config.Bind("Optimize", "FarBuildingsQuietPolling", true, "1.2.249: Far buildings checks its zones for changes about once a second instead of about ten times, spreads its 60 s safety rescans out, and checks for a zone hand-off four times a second instead of every frame. A changed far zone shows up to about a second later. Off: as before.");
            FarMeshReadNoRawFlags = Config.Bind("Optimize", "FarMeshReadNoRawFlags", true, "Far buildings read non-readable prefab meshes from the GPU once. On: the read leaves those shared meshes' buffer targets as they are. Off: it sets the Raw flag on them first, as before (a test toggle).");
            LateJoinFromZdo = Config.Bind("Optimize", "LateJoinFromZdo", false, "1.2.253: a piece missing from its zone's bake joins the zone's instanced group straight from its ZDO, with no object made and destroyed. Anything the ZDO cannot answer for is created as before. Off = create, join, convert as before.");
            BakeCoverageCensus = Config.Bind("Optimize", "BakeCoverageCensus", false, "Debug census every 30 s, changes nothing: for each loaded baked zone, how many building pieces the bake draws, how many draw themselves, how many draw twice and how many draw at all nowhere (and how many of those are in view), with examples and the reason.");
            BakeVerifyCoverage = Config.Bind("Optimize", "BakeVerifyCoverage", false, "Needs BakeCoverageCensus. A piece the census finds drawn twice, or drawn by nobody, is checked again 2 s later and, if still so, fixed the way EBM already does it (its renderers hidden or given back, a skipped piece handed back to the game). Each fix is logged. Off: counted only.");
            BakeFollowChanges = Config.Bind("Optimize", "BakeFollowChanges", false, "A building piece the bake draws in its place is watched for changes to its saved data (paint or edits from other mods, a move), about once a second; a change the bake cannot show hands the piece back to draw itself. Off: only health and the existing hooks are followed.");
            RemoteZoneValidation = Config.Bind("Cache", "RemoteZoneValidation", false, "1.2.253: a cached zone is checked against the world only when no new object has arrived for its sector for 30 s and you are within one zone; a zone with at least 25 % (and 50) of its pieces gone rebakes fresh once. Off = the quiet-time rule.");
            StreamPacerVanillaFloor = Config.Bind("Optimize", "StreamPacerVanillaFloor", false, "1.2.253: while you move, the create pacer's floor is vanilla's rate (per second, not per frame) plus a share of the frame's measured headroom. Standing still is unchanged. Off = the old floor.");
            SaveOffThread = Config.Bind("Cache", "SaveOffThread", false, "1.2.253: bake cache files are written on a background thread, to a temporary file that then replaces the old one, at most once per 30 s per zone while it is active. Off = written on the main thread as before.");
            ShareAwakePropertyBlock = Config.Bind("Optimize", "ShareAwakePropertyBlock", false, "1.2.257: every building piece shares the one material property block the game makes for it when it loads and never uses, instead of a new one each. No visible effect. Off = one each, as the game does.");
            SaveSerializeOffThread = Config.Bind("Cache", "SaveSerializeOffThread", false, "1.2.256: with SaveOffThread on, a zone's cache file is also put together on the background thread, from a copy already held, so saving makes no long frame. No visible effect. Off = put together on the main thread.");
            FarHandoffCountsSkipped = Config.Bind("Optimize", "FarHandoffCountsSkipped", true, "1.2.256: when a zone loads, its far copy lets go as soon as every building there is drawn, counting pieces EBM draws without creating them. Off: on a server those zones keep the far copy on top for up to 10 s.");
            IndirectCrashFallback = Config.Bind("Optimize", "IndirectCrashFallback", true, "1.2.256: if the game crashed in the graphics driver while the GPU-driven renderer was starting, the next session alone uses the older drawing path and logs a warning; the one after tries again. Your settings are not changed. A killed game does not count.");
            CacheSeparateRoleFolders = Config.Bind("Cache", "SeparateRoleFolders", true, "1.2.255: a world's saved zones are kept apart when you host it or play it alone and when you join it as a client, because what each sees can differ. Off = one folder for both.");
            CacheLowGarbage = Config.Bind("Optimize", "CacheLowGarbage", false, "1.2.255: cache saves and far cache file work make far less temporary memory. No visible effect; the files are byte for byte the same. Off = as before.");
            CacheRejectChangedPrefabs = Config.Bind("Cache", "RejectChangedPrefabs", true, "1.2.255: a saved zone is rebuilt once if a building model it was baked from changed (a mod or game update, a moved pivot). Off = the old model keeps drawing until ebm_rebake.");
            CacheEmptyZoneDropsCache = Config.Bind("Cache", "EmptyZoneDropsCache", true, "1.2.255: the saved bake of a fully demolished zone is removed once the zone is known to be delivered and empty. Off = it stays and can reappear.");
            CacheKeepOtherSettings = Config.Bind("Cache", "KeepOtherSettings", true, "1.2.255: saved zones baked under other settings stay on disk, unused, until that zone bakes again. Off = they are deleted at load.");
            CachePreloadNearOnly = Config.Bind("Cache", "PreloadNearOnly", false, "1.2.255: only saved zones near you are held in memory, which saves RAM. A far zone reached quickly, as by teleport, may rebuild from its live pieces that visit. Off = every saved zone of the world is loaded.");
            CacheMaxMBPerWorld = Config.Bind("Cache", "MaxMBPerWorld", 512, new ConfigDescription("1.2.255: disk space one world's saved zones may use; the least recently used are removed first. It also bounds their memory. 0 = no limit.", new AcceptableValueRange<int>(0, 16384)));
            CacheMaxMBAllWorlds = Config.Bind("Cache", "MaxMBAllWorlds", 2048, new ConfigDescription("1.2.255: disk space all worlds' saved zones may use together; the least recently visited other world is removed first. 0 = no limit.", new AcceptableValueRange<int>(0, 65536)));
            IndirectCoverAfterBakeEnds = Config.Bind("Optimize", "CoverAfterBakeEnds", true, "1.2.255: a piece a bake gives back is drawn through the indirect batches again. Off = it stays on the slower path until reload.");
            CoverageBatchedStart = Config.Bind("Optimize", "CoverageBatchedStart", false, "New trees, rocks and pickables are handed to the faster draw path in one pass late in the frame instead of one at a time as each appears. Visible effect: none; when very many appear at once some wait a frame longer on the normal path. Off = as before.");
            BakePassBudgetMs = Config.Bind("Optimize", "BakePassBudgetMs", 0f, new ConfigDescription("Milliseconds the bake decision walk may spend in one frame before leaving the remaining zones for the next frame, so several zones' cache checks, bakes and saves stop landing in one frame. A single zone still finishes its step once started. Visible effect: none; a waiting zone keeps drawing its original pieces a frame or more longer. 0 = no limit, as before.", new AcceptableValueRange<float>(0f, 50f)));
            BakeFrameSplit = Config.Bind("Optimize", "BakeFrameSplit", true, "Only while Debug logging is on: every 30 s, one line naming where the zone tracker's worst frame went (cache check, trim, teardown, bake, save read / serialise / write, orphan census, pumps) and which zone took the largest step. Measurement only, no effect on the game.");
            StreamPacerMotionFromReference = Config.Bind("Optimize", "StreamPacerMotionFromReference", true, "1.2.256: the create pacer judges whether you are moving from the position the game loads the world around (your own position in normal play), measured every frame. A fix: the old test could miss motion entirely. Off = the old test from the player's position.");
            StreamPacerUnfocusedBoostOnlyWhenThrottled = Config.Bind("Optimize", "StreamPacerUnfocusedBoostOnlyWhenThrottled", true, "1.2.256: when the game is not the focused window, the create pacer uses its larger background budget (40 ms a pass) only if the game is really slowed in the background (the frame's other work takes over twice the target frame, or it is not set to run in the background). A game still running at full speed behind another window keeps its normal budget. No visible effect while you watch the game; alt-tabbed loading is unchanged. Off = always the background budget when unfocused.");
            StreamPacerMovingCap = Config.Bind("Optimize", "StreamPacerMovingCap", false, "1.2.257: while you move, objects near you appear at least as fast as in the unmodded game, and extra ones only within a small time cap each frame (at least 1 ms, unused time saved for up to 0.2 s), so the world keeps up without long frames. While on, StreamPacerVanillaFloor is not used. Standing still and loading are unchanged. Off = as before.");
            StreamPacerViewOrder = Config.Bind("Optimize", "StreamPacerViewOrder", false, "1.2.257: objects near you appear in front of you first and behind you last, closest first; anything waiting over 10 s still gets its turn. Off = closest first in every direction, as before.");
            StreamPacerRealCostCap = Config.Bind("Optimize", "StreamPacerRealCostCap", false, "1.2.256: while you stand still, the number of objects one create pass may make is worked out from what a real create costs, not an average that includes the near-free skipped ones. Same time limit; fewer objects in one frame. Off = the old count.");
            StreamPacerMotionByPosition = Config.Bind("Optimize", "StreamPacerMotionByPosition", true, "1.2.252: the create pacer also counts you as moving when your position changes (flying, carried, debug fly), not only from your physics speed, so it keeps to the moving budget instead of the long standing-still one. Off = physics speed only.");
            IndirectPiecePixelShave = Config.Bind("Optimize", "IndirectPiecePixelShave", false, "1.2.251: EBM piece shader skips texture work a piece cannot use (non-triplanar materials, no snow). Same look intended; off until checked.");
            FarSourceAsync = Config.Bind("Optimize", "FarSourceAsync", true, "Far buildings read and parse their cached meshes on a worker thread instead of the main thread, and start new reads only inside the frame's far budget. Off: on the main thread, as before.");
            FarUploadSliced = Config.Bind("Optimize", "FarUploadSliced", true, "A far buildings job's chunks are uploaded over several frames, inside the frame's far budget; the old look stays until the whole new one is ready. Off: all of a job's chunks in one frame.");
            FarIndexRanges = Config.Bind("Optimize", "FarIndexRanges", false, "Far buildings remember where each piece sits in its merged chunk. When pieces only leave a far zone, or a zone moves from a 512 m cell to its own target, those pieces are hidden in place instead of merging and uploading the whole zone or cell again. Anything else rebuilds as before. Checks itself on the GPU and switches off for the session if that check fails. Off: every change rebuilds.");
            FarZoneGrid = Config.Bind("Optimize", "FarZoneGrid", false, "Far buildings and far trees read which zones are loaded once a frame into a small grid, and answer 'is any zone here loaded' for a whole area in one step. Same answers as asking zone by zone. Off: zone by zone.");
            FarHandoffProvenDrawn = Config.Bind("Optimize", "FarHandoffProvenDrawn", false, "When a zone loads, its far copy waits until every building there is really drawn, counting a piece EBM draws without creating it only once its zone's baked group is drawing. Off: such a piece counts as soon as EBM has skipped it, which can be a frame or more before its group draws.");
            FarKeepHandedOff = Config.Bind("Optimize", "FarKeepHandedOff", false, "A far copy that hands over to the real buildings is hidden and kept for 2 minutes (at most a quarter of the far vertex cap) instead of destroyed, so walking back out of the area shows it again at once instead of after a rebuild. Off: destroyed and rebuilt when needed again.");
            FarRetireSliced = Config.Bind("Optimize", "FarRetireSliced", false, "Far chunks being replaced are hidden at once and destroyed a few a frame inside the far budget, instead of all in one frame. Looks the same. Off: all at once.");
            FarUploadByteCap = Config.Bind("Optimize", "FarUploadByteCap", false, "Spread far chunk uploads also stop at 8 MB a frame (at least one chunk), since the GPU copy happens on the render thread, outside the main-thread budget. Off: the time budget only.");
            FarBuildViewFirst = Config.Bind("Optimize", "FarBuildViewFirst", false, "Far buildings in front of the camera are built before the ones behind it. Order only: everything is still built. Off: nearest first.");
            FarCapHysteresis = Config.Bind("Optimize", "FarCapHysteresis", false, "When far buildings reach their vertex cap, a far area that was dropped comes back only once the total is under 90 % and 20 s have passed, so areas in the 90-100 % band can stay hidden that long. At most 16 areas are dropped per 10 s; past that, drawn areas are kept up to 110 % of the cap, and anything that would go past 110 % is still dropped (the 16 is not a hard limit there). Stops areas at the edge of the cap being dropped and rebuilt over and over while you move. Off: dropped and re-added the moment they fit or don't.");
            FarMergePooled = Config.Bind("Optimize", "FarMergePooled", false, "Far buildings reuse their merge buffers and chunk objects instead of making new ones for every rebuild, so far buildings make far less garbage (fewer full garbage-collection pauses). Looks the same. Off: new buffers and objects each time.");
            FarGroundProbe = Config.Bind("Optimize", "FarGroundProbe", false, "Counts only, changes nothing: measures how much far ground could be proven hidden by the terrain's own near-hide on these settings, and logs why none is skipped.");
            IndirectPresize = Config.Bind("Optimize", "IndirectPresize", true, "Remember each world's largest indirect buffer sizes and use them at the next load, so the buffers do not grow repeatedly while the world fills in. Off: they grow as needed.");
            ZoneTransitionBudgetMs = Config.Bind("Optimize", "ZoneTransitionBudgetMs", 1f, new ConfigDescription("Milliseconds a frame for handing a just-unloaded zone's pieces back (at least one piece a frame). 0 = all of a zone in the frame it unloads.", new AcceptableValueRange<float>(0f, 8f)));
            IndirectGpuMeshValidator = Config.Bind("Diagnostics", "IndirectGpuMeshValidator", false, "1.2.245, a test tool: reads each drawn mesh from the GPU and checks its vertices against its bounds and its indices against its vertex count, a lap every 20 s, and tests whether Far buildings changes a mesh when it reads it. Writes [GpuValidator] and [RawProbe] log lines.");
            IndirectMeshContentCheck = Config.Bind("Diagnostics", "IndirectMeshContentCheck", false, "1.2.235 ([visual], the stretch hunt): each batch mesh's GPU vertex + index data is sampled (4 KB each, one mesh every 0.25 s) and hashed; a change under EBM's draws (a mod rewriting a prefab mesh in place) logs an Error naming the mesh.");
            CoverageCensus = Config.Bind("Diagnostics", "CoverageCensus", false, "1.2.227: with Debug logging, the [CoverageReach] census line every 30 s (walks every covered object: ~30 ms in a big town). Off = no walk; ebm_coverage still prints it on demand.");
            // 1.2.250: FGN's client pacer moved here, so this is THE client pacer: default ON, under a new key so an old saved "StreamPacer = false" does not leave clients with none.
            StreamPacer = Config.Bind("Optimize", "ObjectStreamPacer", true, "Client / local host: objects are created from one event queue (your zone first, solid before loose), sized from this machine's measured frame, distant objects from what is left; ZoneFilling / ZoneSettled on EbmApi. Arrival stays vanilla. Off = vanilla's create path.");
            StreamPacerBaseMs = Config.Bind("Optimize", "StreamPacerBaseMs", 3, new ConfigDescription("The least time a create pass may use, when the frame has no headroom left.", new AcceptableValueRange<int>(1, 16)));
            StreamPacerMinPerFrame = Config.Bind("Optimize", "StreamPacerMinPerFrame", 100, new ConfigDescription("The create pass's object cap never drops below this.", new AcceptableValueRange<int>(10, 500)));
            StreamPacerEveryFramePass = Config.Bind("Optimize", "StreamPacerEveryFramePass", true, "While objects are waiting, a create pass also runs on frames vanilla's 30 per second tick skipped, so the fill follows the frame rate. Off = the 30 Hz tick only.");
            StreamPacerExtraPasses = Config.Bind("Optimize", "StreamPacerExtraPasses", true, "1.2.254: with StreamPacerEveryFramePass on, whether the extra create passes also run while you move. Off = while moving, objects are created on vanilla's 30 per second tick only; standing still and arriving are unchanged.");
            TickGroupEventDriven = Config.Bind("Optimize", "TickGroupEventDriven", false, "1.2.254: the shelter, wet and light checks re-run when what they read changes (rain, shelter, pieces added or removed, moving), with a slow timed check for the rest. Off = each on its fixed timer, as before.");
            IndirectCellReusePerCell = Config.Bind("Optimize", "IndirectCellReusePerCell", false, "1.2.254: with IndirectCellReuse, an added object re-judges only its own cell instead of the whole cell walk; a removed one costs nothing. Needs IndirectFusedCells. Off = any change re-walks every cell.");
            StreamPacerSelfCheck = Config.Bind("Optimize", "StreamPacerSelfCheck", false, "Test tool: every 10 s, a Debug line comparing the create queue with the game's own near list.");
            IdleObjectAreaSkip = Config.Bind("Optimize", "IdleObjectAreaSkip", true, "With nothing near you waiting to be created or removed and the same zone, the game's 30 per second pass over every nearby object runs 10 times a second instead. Same objects, less work in big bases. Needs ObjectStreamPacer.");
            EventDrivenObjectArea = Config.Bind("Optimize", "EventDrivenObjectArea", false, "While you stay in one zone, new nearby objects come straight from the event queue and the full gather of nearby objects runs about once a second. Objects that leave your area go within that second. Needs IdleObjectAreaSkip. Off until the A/B (ebm_eventarea flips it for a session).");
            EventAreaFullPassSeconds = Config.Bind("Optimize", "EventAreaFullPassSeconds", 1f, new ConfigDescription("With EventDrivenObjectArea: the longest time between two full gathers (a zone change always runs one at once).", new AcceptableValueRange<float>(0.1f, 5f)));
            DestroyMoversFirst = Config.Bind("Optimize", "DestroyMoversFirst", true, "When objects leave your area, anything that moves itself goes in that same frame and loose items go before the floors under them, so nothing is left simulating or falling. Off = one queue in scan order.");
            RemovalIdleScanLimit = Config.Bind("Optimize", "RemovalIdleScanLimit", true, "With nothing queued for removal and the same zone, the removal scan runs at most 10 times a second. Off = every pass.");
            TeleportCreateGate = Config.Bind("Optimize", "TeleportCreateGate", true, "While teleporting, create passes start as soon as zones arrive, and each object waits for its own zone's ground. Off = vanilla's whole-area wait.");
            DistantPrefabStreaming = Config.Bind("Optimize", "DistantPrefabStreaming", true, "Streams the prefabs other mods register with EbmApi.RegisterDistantPrefab out to their distance. Nothing runs until one registers. Off = none are streamed.");
            PieceTypeAudit = Config.Bind("Optimize", "PieceTypeAudit", false, "Diagnostic: once a session, names build pieces not marked Solid (they load in the item tier and can lose the race with what rests on them). Off = no audit.");
            ZoneLoadDiagnostics = Config.Bind("Optimize", "ZoneLoadDiagnostics", false, "Diagnostic: Debug lines for what each zone crossing cost and which objects keep failing to create. Adds a timer around every object created. Off = no timers.");
            IndirectCasterSizeSkip = Config.Bind("Optimize", "IndirectCasterSizeSkip", true, "1.2.233: in every shadow cascade past the first, a baked batch too small to matter from that cascade's near distance (under IndirectCasterMinScreen of the screen height) is not drawn into that cascade at all: fewer draw calls. A LOOK CHANGE at a low sun: a thin caster's far shadow is longer than the caster (check far posts / fences at sunset, on vs off). Off = every reachable batch draws in every cascade.");
            IndirectCasterMinScreen = Config.Bind("Optimize", "IndirectCasterMinScreen", 0.01f, new BepInEx.Configuration.ConfigDescription("1.2.233: with IndirectCasterSizeSkip, the smallest screen-height fraction a batch's mesh must cover from a far cascade's near distance to cast there.", new BepInEx.Configuration.AcceptableValueRange<float>(0f, 0.1f)));
            IndirectLooksChangeDriven = Config.Bind("Optimize", "IndirectLooksChangeDriven", true, "1.2.223: a GPU-drawn group whose snow / wet / pushed-look inputs did not change skips the per-frame looks prep and looks write (the exact no-op test of both, as field compares). Off = both run every frame, as before (A/B).");
            IndirectCellReuse = Config.Bind("Optimize", "IndirectCellReuse", true, "1.2.220: the CPU cell walk keeps last frame's answer while the camera moved under 1 m and turned under 0.05 degrees, no zone cell changed and the shadow cascades and lights stayed put (its test is widened by 2 m so the kept answer always covers what can be seen; the GPU still culls each instance exactly). Standing still the walk costs ~nothing. Read live.");
            IndirectRugVariant = Config.Bind("Optimize", "IndirectRugVariant", true, "1.2.221 ([seasons]): rugs (Custom/Rug: straw, asksvin, fur, jute) get EBM's own GPU-driven variant, so coverage can take them; same look, no shadows (vanilla rugs cast none), drawn after the floor. On since 1.2.256 (Fire checked the look). Takes effect for rugs covered after the switch (next load for all).");
            RendererCensus = Config.Bind("Diagnostics", "RendererCensus", false, "1.2.218: with Debug logging, count every vanilla renderer once per 30 s (the [Indirect] visible-of-enabled line and the [CasterCensus] line). Each count starts with a scene-wide object search that takes tens of ms at a town (the 77 ms frame every 30 s in the O runs), so it is off unless you are diagnosing.");
            DestroyLeanEarmark = Config.Bind("Optimize", "DestroyLeanEarmark", false /* 1.2.216: OFF until the pack is checked for other near-list writers ([generator]); a self-check on every zone change refuses it when one shows */, "1.2.215: the zone-leave destroy pass stamps only EBM's keepalive ZDOs and asks each leaving candidate its live sector, instead of stamping every near and distant ZDO (~134k in a town) on each pass. Never destroys more than vanilla would. Off until the [DTS] census shows the stamping is the cost. Read live.");

            IndirectMinScreenPercent = Config.Bind("Optimize", "IndirectMinScreenSize", 0.1f, new ConfigDescription(   // 1.2.209 (Fire: VPO parity): default 0.1 like VPO; key renamed so the new default reaches existing cfgs
                "With IndirectRenderer on: baked instances smaller than this percent of the screen height are not drawn by the camera (they still cast shadows). 0 = off (everything drawn). A look setting: 0.1 hides only specks.", new AcceptableValueRange<float>(0f, 2f)));
            InstancedInstanceCull = Config.Bind("Optimize", "InstancedInstanceCull", false,
                "Baked (instanced) pieces the camera cannot see are left out of the main view's draws, one compacted list per group (same draw calls, fewer pieces). A piece out of view whose shadow can still reach the view is kept. Worked out on a background thread a frame ahead; a sharp camera turn or a jump draws everything for that frame. Off = every baked piece is submitted (as before).");

            LightFlickerLodEnabled = Config.Bind("Optimize", "LightFlickerLodEnabled", true,
                "Distance-tiered throttling of LightFlicker.CustomUpdate. Vanilla runs\n" +
                "flicker math (6+ sin/cos calls + position jitter) every frame for every\n" +
                "lit prefab in the scene; at a megabase with hundreds of torches/braziers\n" +
                "this aggregates to 8-10 ms/sec. Distant lights update less often instead\n" +
                "of being skipped outright â€” vanilla zeroes a light's intensity when it is\n" +
                "enabled and only CustomUpdate writes it back, so a light that never ticks\n" +
                "renders black. Skipped time is banked and handed to the next real update,\n" +
                "so flicker and fade still run at the correct speed. Temporary FX lights\n" +
                "(m_ttl > 0) and lights still fading in always run every frame.");

            LightFlickerLodDistance = Config.Bind("Optimize", "LightFlickerLodDistance", 30f,
                new ConfigDescription(
                    "Radius in meters within which lights flicker at full vanilla rate.\n" +
                    "Beyond it they step down through the mid / far / distant tiers below.",
                    new AcceptableValueRange<float>(5f, 200f)));

            LightFlickerMidDistance = Config.Bind("Optimize", "LightFlickerMidDistance", 60f,
                new ConfigDescription(
                    "Outer radius of the mid tier. Lights between LightFlickerLodDistance\n" +
                    "and this distance update at LightFlickerMidRate.",
                    new AcceptableValueRange<float>(10f, 300f)));

            LightFlickerFarDistance = Config.Bind("Optimize", "LightFlickerFarDistance", 100f,
                new ConfigDescription(
                    "Outer radius of the far tier. Lights between LightFlickerMidDistance\n" +
                    "and this distance update at LightFlickerFarRate; anything beyond uses\n" +
                    "LightFlickerDistantRate.",
                    new AcceptableValueRange<float>(20f, 500f)));

            LightFlickerMidRate = Config.Bind("Optimize", "LightFlickerMidRate", 10f,
                new ConfigDescription(
                    "Updates per second for lights in the mid distance tier.",
                    new AcceptableValueRange<float>(1f, 60f)));

            LightFlickerFarRate = Config.Bind("Optimize", "LightFlickerFarRate", 5f,
                new ConfigDescription(
                    "Updates per second for lights in the far distance tier.",
                    new AcceptableValueRange<float>(1f, 30f)));

            LightFlickerDistantRate = Config.Bind("Optimize", "LightFlickerDistantRate", 2f,
                new ConfigDescription(
                    "Updates per second for lights beyond LightFlickerFarDistance. Keep\n" +
                    "above zero so distant lights still receive an intensity write.",
                    new AcceptableValueRange<float>(0.5f, 10f)));

            LightFlickerTierCheckRate = Config.Bind("Optimize", "LightFlickerTierCheckRate", 2f,
                new ConfigDescription(
                    "How often each light re-measures its distance tier. Caching the tier\n" +
                    "is what removes the per-frame distance math; raise it only if lights\n" +
                    "visibly lag a tier change while you sprint past them.",
                    new AcceptableValueRange<float>(0.5f, 10f)));

            ZSFXLodEnabled = Config.Bind("Optimize", "ZSFXLodEnabled", true,
                "Distance-LOD on ZSFX.CustomUpdate. ZSFX is Valheim's per-sound wrapper\n" +
                "around Unity's AudioSource â€” manages fade in/out, pitch/volume modifiers,\n" +
                "reverb routing, and concurrency suppression. It ticks every frame on every\n" +
                "active sfx in the scene; at a megabase with hundreds of torch / brazier /\n" +
                "fountain loops this aggregates to ~3 ms/sec. Unity's audio engine handles\n" +
                "spatial attenuation independently of CustomUpdate, so beyond the audible\n" +
                "falloff radius the per-frame housekeeping has no perceptible effect on what\n" +
                "the player hears. We skip CustomUpdate entirely beyond the configured\n" +
                "distance; the AudioSource keeps its last set volume / pitch which already\n" +
                "attenuates to ~zero at that range.");

            ZSFXLodDistance = Config.Bind("Optimize", "ZSFXLodDistance", 60f,
                new ConfigDescription(
                    "Distance in meters beyond which ZSFX.CustomUpdate is skipped. 60 m\n" +
                    "is conservative â€” most ambient sfx have AudioSource.maxDistance under\n" +
                    "50 m so they're inaudible past that even when CustomUpdate is still\n" +
                    "running. Music and ambient zones have longer reach; raise this if you\n" +
                    "hear audio pop in/out at the edge of your hearing range. Lower for\n" +
                    "more aggressive savings.",
                    new AcceptableValueRange<float>(10f, 200f)));

            ZdoBakeVerifyEnabled = Config.Bind("Optimize", "ZdoBakeVerifyEnabled", false,
                "MEASUREMENT ONLY - changes no rendering, moves no vertex, and is off by default.\n" +
                "\n" +
                "Object creation is the largest single cost on a streaming client: CreateObject measured\n" +
                "1,070 ms/s over 5,135 calls in one 2-second window, and the census says 75,089 pieces were\n" +
                "'unloaded after creation' - created ONLY so the baker could see live pieces on a fresh zone,\n" +
                "then thrown away.\n" +
                "\n" +
                "TrySkipCreation already decides entirely from a ZDO; the one thing missing on a fresh zone is\n" +
                "that the bake has not landed. So the fix is to produce the bake from ZDOs BEFORE vanilla\n" +
                "instantiates anything - which is what DeferCreation's own notes concluded after stalling\n" +
                "vanilla made things worse.\n" +
                "\n" +
                "Turning this on derives what a ZDO-only bake WOULD have produced for each zone and reports how\n" +
                "it differs from the real one. The number that decides whether the idea is safe is EXTRA: a\n" +
                "piece present in the ZDO answer but absent from the live bake would be WRONGLY SKIPPED, i.e.\n" +
                "invisible in the world. It must be 0 before any of this touches the render path.");

            RemoteItemZSyncSkipEnabled = Config.Bind("Optimize", "RemoteItemZSyncSkipEnabled", true,
                "Stop lerping dropped items that are already where they belong. A REMOTE ZSyncTransform runs a\n" +
                "full distance check plus lerp every FixedUpdate for the whole session, even on an object that\n" +
                "has not moved since it landed.\n" +
                "\n" +
                "Measured on the Iso rig: 412 ZSyncTransform instances, 308 owned (which exit immediately) and\n" +
                "104 REMOTE carrying 103-109 ms/s between them. The census named them: 52 MeadStrength,\n" +
                "26 QueensJam, 18 Fish1, 18 Pukeberries, 17 Blueberries. Dropped items and fish - not building\n" +
                "pieces, which is why ZSyncStaticSkip never sees them (it requires a Piece component).\n" +
                "\n" +
                "Unlike the piece skip this cannot be cached per prefab: the same item is ineligible while it is\n" +
                "still tumbling and eligible once it settles. A slow sweep re-checks instead, and REVERSES the\n" +
                "moment the owner moves it - so the worst case is one sweep interval of staleness on an object\n" +
                "that was not moving. Pieces, parented objects and owned objects are never touched.");

            ZSyncStaticSkipEnabled = Config.Bind("Optimize", "ZSyncStaticSkipEnabled", true,
                "Disable ZSyncTransform on player-built Pieces that never move. ZSyncTransform\n" +
                "is an IMonoUpdater that ticks every frame on every active instance, even when\n" +
                "the piece is a static wall whose position has been settled since placement.\n" +
                "Setting enabled=false on the component removes it from ZSyncTransform.Instances\n" +
                "via OnDisable so the per-frame iteration stops visiting it.\n" +
                "\n" +
                "Eligibility is narrow and conservative: must be a Piece (player-built), must\n" +
                "have no parent transform (rules out ship/cart attachments), no body-velocity\n" +
                "or character-parent sync flag, no non-kinematic Rigidbody, and no component\n" +
                "from the animated/interactable safelist (Door, Container, Sign, Smelter,\n" +
                "CookingStation, Fireplace, Pickable, TeleportWorld, Bed, Ship, Vagon,\n" +
                "MineRock, ShieldGenerator, â€¦) â€” the same surface as the mesh-bake unsafe\n" +
                "filter plus all interactables.\n" +
                "\n" +
                "Risk surface: in solo this is essentially free. In multiplayer an admin\n" +
                "teleport on a static piece won't propagate to other clients until reload.\n" +
                "Disable this toggle if you hit a sync issue in MP and we'll look at it.");

            EffectAreaIdleSkipEnabled = Config.Bind("Optimize", "EffectAreaIdleSkipEnabled", true,
                "Take EffectAreas that can never tick out of the MonoUpdaters list. EffectArea is the\n" +
                "LARGEST IMonoUpdater list in a built-up world - 848 instances in the 2026-09-26 census,\n" +
                "ahead of LightFlicker 562 and ZSyncTransform 354 - and MonoUpdaters copies the whole\n" +
                "list every call, so each instance costs a copy slot plus an interface dispatch fifty\n" +
                "times a second before its body runs.\n" +
                "\n" +
                "Most of them do no work at all. EffectArea.CustomFixedUpdate returns unless a character\n" +
                "is inside, and the character list is only ever filled for a Heat area or one carrying a\n" +
                "status effect. Every other area - PlayerBase, NoMonsters, PrivateProperty, which is the\n" +
                "workbench and base-marker population of a settlement - has a permanently empty list, so\n" +
                "its tick is dead code for the object's whole lifetime.\n" +
                "\n" +
                "Only updater-list membership is dropped; the component stays ENABLED, because\n" +
                "OnTriggerEnter/Exit live on it and Unity does not deliver trigger callbacks to a\n" +
                "disabled MonoBehaviour - disabling would break the NoMonsters and Burning bookkeeping.\n" +
                "The static area queries are fed in Awake and by collider overlap, never from the updater\n" +
                "list, so no game behaviour changes. Heat and status areas are left registered.");

            SkipUnchangedScans = Config.Bind("Optimize", "SkipUnchangedScans", true,
                "Stop re-searching the whole active area for work that is not there. Every pass ZNetScene walks\n" +
                "every object near you looking for ones with no model yet; standing in a settlement that is over\n" +
                "113,000 objects visited to find zero, thirty times a second. This skips that search while the\n" +
                "near list, the distant list, the live object count and your zone are all unchanged.\n" +
                "It skips only the SEARCH, never the removal pass, so nothing can be deleted or stranded by it -\n" +
                "the worst case is a new object appearing up to a second late. It also rechecks itself every few\n" +
                "seconds by running a search it would have skipped and reporting whether that search found\n" +
                "anything; the status box prints 'confirmed the skip, 0 wrong'. Turn it off if it ever says WRONG.");

            ZSyncStaticSkipFixtures = Config.Bind("Optimize", "ZSyncStaticSkipFixtures", true,
                "Also stop position updates on stations and fixtures that never move - signs, smelters,\n" +
                "cooking stations, fermenters, beehives, portals, fireplaces, workbenches, wards and beds.\n" +
                "They were left running out of caution rather than need, and in a settlement they are a\n" +
                "large share of what is left. Item stands, armour stands, pickables and switches are never\n" +
                "included. Turn this off if another player ever sees one of these at an old position after\n" +
                "it was moved with an admin tool.");
            ZSyncStaticSkipFixtures.SettingChanged += (_, __) => EasyBake.StaticPieceZSyncSkip.Reset();

            ZSyncStaticSkipVerbose = Config.Bind("Optimize", "ZSyncStaticSkipVerbose", false,
                "Log a periodic summary of how many pieces have had their ZSync disabled and\n" +
                "how many prefabs are in the eligibility cache. Useful for verifying the\n" +
                "filter is hitting your prefab set as expected after a deploy.");

            ClutterLodEnabled = Config.Bind("Optimize", "ClutterLodEnabled", true,
                "Skip ClutterSystem.LateUpdate while the player is standing still. Vanilla\n" +
                "re-walks the full grass-patch ring around the camera TWICE every frame\n" +
                "(generate + timeout, ~120 patches at default range) even when parked â€”\n" +
                "FiresDebugginTools measures this at ~3 ms EVERY frame, the largest steady\n" +
                "cost on a natural (non-megabase) world. The grass set is identical frame to\n" +
                "frame while stationary, so we skip the pass until the player moves, with a\n" +
                "short TTL (below) keeping it ticking a few times a second and an immediate\n" +
                "escape when a rebuild is pending (terrain edit, quality change, ResetGrass).\n" +
                "Moving = full vanilla every frame, so grass-in and the player push effect\n" +
                "are unchanged. Disable if you see grass not refreshing while standing still.");

            ClutterLodMoveThreshold = Config.Bind("Optimize", "ClutterLodMoveThreshold", 0.25f,
                new ConfigDescription(
                    "How far (meters) the player must move from the last clutter pass before\n" +
                    "ClutterSystem.LateUpdate runs again. Below this the player counts as\n" +
                    "parked and the pass is skipped. 0.25 m skips only when genuinely still;\n" +
                    "camera rotation / mouse-look doesn't move the player so it still skips.",
                    new AcceptableValueRange<float>(0.05f, 8f)));

            ClutterLodMaxSeconds = Config.Bind("Optimize", "ClutterLodMaxSeconds", 0.33f,
                new ConfigDescription(
                    "Maximum time ClutterSystem.LateUpdate is skipped while parked before a\n" +
                    "forced vanilla pass. Keeps the grass-push trail decaying and picks up\n" +
                    "async heightmap-ready transitions. Lower = more responsive, less savings;\n" +
                    "higher = more savings, longer-lived frozen push trail. 0.33s â‰ˆ 3 Hz.",
                    new AcceptableValueRange<float>(0.05f, 5f)));

            ClutterLodVerbose = Config.Bind("Optimize", "ClutterLodVerbose", false,
                "Log a one-line summary every ~5 seconds tallying ClutterLod calls: how many were\n" +
                "skipped vs passed through, and the reason for each passthrough (moved past threshold,\n" +
                "ttl expired, m_forceRebuild pending, no local player, freefly, disabled). Useful when\n" +
                "ClutterSystem.LateUpdate cost in the FiresDebugginTools overlay doesn't drop while\n" +
                "parked â€” the line will tell you whether the prefix is even being called, and if so,\n" +
                "what's forcing it to pass through to vanilla.");

            // ── AN EDIT TO THE .cfg ON DISK HAS TO REACH THE RUNNING GAME ─────────────────────────────────
            // BepInEx never re-reads a config file on its own, and a SettingChanged handler only fires when the
            // in-memory entry changes - through the config UI or code - never from an external edit. So every
            // SettingChanged wiring above was dead to a text edit, and only Core's watcher closes that gap.
            //
            // MEASURED, 2026-09-26: ZdoBakeVerifyEnabled was set to true in the live cfg mid-run. Eleven minutes
            // later the file still read true, nothing had re-saved over it, and 89 fresh bakes had run with the
            // feature still gated off in-process. The setting never arrived. Bound last, after every Config.Bind
            // above, so the first reload cannot race an entry that does not exist yet.
            PlayerFacingConfig.Apply(Config);   // 1.2.110: short lines, basics only without Show Advanced
            CoreLink.WatchConfig(Config);

            EasyBake.MeshCacheStore.Initialize();

            // Create the Harmony instance up front so the SettingChanged
            // handler below can patch/unpatch on the fly. The instance is
            // also reused by ApplyPatches / RemovePatches so toggling
            // PluginEnabled live never requires a restart.
            _harmony = new Harmony(PluginGUID);

            // Live master-toggle. When the user flips PluginEnabled in the
            // config manager, apply or remove all our patches immediately,
            // and on the OFF path also call Reset() on every stateful
            // subsystem so any baked zones, pinned ZDOs, destroy queues,
            // cached prefab classifications, etc. are unwound cleanly.
            PluginEnabled.SettingChanged += (_, __) =>
            {
                if (PluginEnabled.Value && !_patchesApplied)
                {
                    ApplyPatches();
                    EasyBakeLog.Info($"{PluginName} re-enabled via config â€” patches restored.");
                }
                else if (!PluginEnabled.Value && _patchesApplied)
                {
                    RemovePatches();
                    EasyBakeLog.Info($"{PluginName} disabled via config â€” patches removed, subsystem state reset.");
                }
            };

            EasyBakeLog.Debug($"[Keepalive] ZoneKeepaliveEnabled = {ZoneKeepaliveEnabled.Value} (default off since 1.2.86), radius "
                + $"{ZoneKeepaliveRadius.Value} zone(s), {ZoneKeepaliveSeconds.Value:0} s after leaving");

            if (PluginEnabled.Value)
            {
                ApplyPatches();
                // 1.2.210 ([generator] port of Core's Far\, integrated by [ghost]): EBM owns the far view now. Order as Core's owners called it;
                // FarCache creates FarRunner (and so the far job drain) here; ImpostorEngine hooks FarRunner lazily on its first kind / cell.
                try
                {
                    EasyBake.Far.ImpostorEngine.Initialize(Config);
                    EasyBake.Far.FarCache.Initialize(Config);
                    EasyBake.Far.FarLights.Initialize(Config);
                    EasyBake.Far.FarBuildings.Initialize(Config);
                }
                catch (System.Exception ex) { EasyBakeLog.Error($"[Far] far view init failed: {ex.Message}"); }
            }
            else
                EasyBakeLog.Info(
                    $"{PluginName} v{PluginVersion} loaded but PluginEnabled=false â€” no patches applied. " +
                    "Flip PluginEnabled to true in the config manager to enable live (no restart needed).");

            EasyBake.IdleGates.Bind(Config);
            EasyBake.EbmParticleIdle.Bind(Config);   // [seasons] 11-09 E34 (off)
            EasyBake.EbmBakeMemRelease.Bind(Config);   // [seasons] 11-09 E25 (off)
            EasyBake.GpuReleaseFence.Bind(Config);     // [seasons] 11-09 E23 (off)
            EasyBake.EbmHandoffCensus.Bind(Config);   // [seasons] 11-09 E28 census (off)
            EasyBake.EbmShaderWarm.Bind(Config);      // [seasons] 11-09 E30 (off)
            EasyBake.SnowFeed.Bind(Config);           // 1.2.259 [Snow] SnowRefreshPoll (ON, a fix)
            EasyBake.EbmSnowStore.Bind(Config);       // 1.2.260 [Snow] WaitForPushedSnow (ON, a fix)
            EasyBake.Indirect.EbmRenderCensus.Bind(Config);   // [seasons] 11-09 E39 (off; or -ebmrendercensus)
            EasyBake.Indirect.EbmIndirect.BindSkyDepth(Config);   // [seasons] 11-09 E31 (pass + throttle keys, off; census always at Debug)
            EasyBake.InvulnerableClassifier.FollowSeasons();   // 1.2.176
            LightFlickerViewCull = Config.Bind("Batching", "LightFlickerViewCull", true, "A light whose whole range is outside the camera flickers at LightFlickerDistantRate. It needs LightFlickerLodEnabled. Off = distance tiers only.");
            LightSettingsEvent = Config.Bind("Batching", "LightSettingsEvent", true, "Lights share one graphics-settings hook instead of one subscription each, so unloading many lights costs less. Off = vanilla.");
            MinimapFogPartialUpload = Config.Bind("Batching", "MinimapFogPartialUpload", true, "Newly explored map pixels upload as a small rectangle instead of the whole fog texture. Off = vanilla.");
            CoreLink.SayIfAbsent();
            try { Utilities.EbmHelpContent.Register(); }
            catch (System.Exception ex) { EasyBakeLog.Warn($"Help registration failed: {ex.Message}"); }

            CoreLink.RegisterStatus(StatusSource, DescribeStatus);

            // Compact "loaded" banner â€” oven with heat squiggles. Deferred to
            // world-load time (when ZNetScene is up) so it bookends the load;
            // the BIG "loading" banner already fired at the top of Awake.
            StartCoroutine(EmitCompactBannerWhenZNetReady());
        }

        // EBM's line in Core's status box: idle CreateDestroyObjects ticks skipped, and create-budget passes held back.
        private static string DescribeStatus()
        {
            int skipped = Patches.ZNetScene_CreateDestroyObjects_Patch.TakeSkippedSinceStatus();
            string budget = Patches.ZNetScene_CreateObjectsSorted_Budget.TakeStatus();
            string gate = EasyBake.ScanGate.TakeStatus();
            budget = gate == null ? budget : (budget == null ? gate : gate + "; " + budget);
            string skips = skipped > 0 ? $"skipped {skipped:N0} idle CreateDestroyObjects ticks" : null;
            if (skips == null) return budget;
            return budget == null ? skips : $"{skips}; {budget}";
        }

        // Waits for ZNetScene + its prefab table to be live (same readiness
        // signal the other Fires mods use), then emits the compact loaded
        // banner. Failure is non-fatal â€” falls back to a plain "loaded" log
        // line so the load event is still recorded in the file log.
        private IEnumerator EmitCompactBannerWhenZNetReady()
        {
            while (ZNetScene.instance == null
                   || ZNetScene.instance.m_prefabs == null
                   || ZNetScene.instance.m_prefabs.Count == 0)
            {
                yield return null;
            }
            yield return new WaitForEndOfFrame();

            try { FiresEasyBakeBanner.Print(); }
            catch (System.Exception ex)
            {
                EasyBakeLog.Info($"{PluginName} v{PluginVersion} loaded. (banner failed: {ex.Message})");
            }
        }

        // Idempotent. Walks every [HarmonyPatch] class in this assembly and
        // patches it, logging per-patch failures so a single bad target
        // doesn't take down the whole set. Sets _patchesApplied = true on
        // first run; subsequent calls are no-ops.
        private void ApplyPatches()
        {
            if (_patchesApplied) return;

            // 1.2.212 ([ghost], F2): a type list that cannot throw, the innermost reason, and a roll-back of a class that failed half-way (EbmPatchSafety)
            int succeeded = 0, failed = 0, methods = 0;
            var failedNames = new System.Collections.Generic.List<string>();
            foreach (var type in EasyBake.EbmPatchSafety.PatchTypes(typeof(FiresEasyBakeMeshesPlugin).Assembly))
            {
                if (type == null || type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
                try
                {
                    var patched = _harmony.CreateClassProcessor(type).Patch();
                    if (patched != null) methods += patched.Count;
                    succeeded++;
                }
                catch (System.Exception ex)
                {
                    failed++;
                    failedNames.Add(type.Name);
                    int undone = EasyBake.EbmPatchSafety.RollBack(_harmony, type);
                    var cause = EasyBake.EbmPatchSafety.Innermost(ex);
                    EasyBakeLog.Error($"Patch {type.Name} failed ({cause.GetType().Name}: {cause.Message}); that part of EBM is off this session, the rest works"
                        + (undone > 0 ? $"; {undone} part(s) it had already applied were removed again, so it never runs half-way" : ""));
                }
            }
            _patchesApplied = true;
            EasyBakeLog.Info(
                $"{PluginName} v{PluginVersion} active â€” {succeeded} patches registered ({methods} game methods), {failed} skipped{(failed > 0 ? ": " + string.Join(", ", failedNames) : "")}. " +
                $"Batching={(BatchingEnabled.Value ? "on" : "off")}, Prewarm={(PrewarmEnabled.Value ? "on" : "off")}.");
        }

        // Removes every patch this Harmony instance applied (won't touch
        // patches owned by other mods), then resets every EasyBake subsystem
        // that holds state so a re-enable later starts clean. ZoneTracker.Reset
        // is the cascade â€” it tears down all baked zones (calling
        // MeshBaker.Restore per zone to flip MeshRenderers back on) and
        // internally invokes Reset on InvulnerableClassifier, ZoneKeepalive,
        // DestroyTimeSlicer, SectorInstanceMirror, and StaticPieceZSyncSkip.
        // The two patch classes that carry per-frame skip-cache state
        // (CreateDestroyObjectsPatches, ClutterSystemPatches) need their own
        // ResetState calls â€” they're not subsystems ZoneTracker knows about.
        private void RemovePatches()
        {
            if (!_patchesApplied) return;
            try { _harmony?.UnpatchSelf(); }
            catch (System.Exception ex) { EasyBakeLog.Error($"UnpatchSelf failed: {ex.Message}"); }

            try { EasyBake.ZoneTracker.Reset(); } catch { }
            try { EasyBake.DamageReplay.Reset(); } catch { }
            try { Patches.ZNetScene_CreateDestroyObjects_Patch.ResetState(); } catch { }
            try { Patches.ClutterSystem_LateUpdate_Patch.ResetState(); } catch { }

            _patchesApplied = false;
        }

        // Tear down on plugin unload so the host process doesn't leave our
        // patches dangling against assembly_valheim. UnpatchSelf is a no-op
        // when _patchesApplied is false.
        // 1.2.97 ([fgn] 10-13): at game quit the process is ending, and UnpatchSelf made Harmony rebuild every method EBM patched and
        // re-run every other mod's patches on them during logout. Unpatch only for a real hot-unload, never once Application.quitting fired.
        private static bool s_quitting;
        private void OnDestroy() { EasyBake.Indirect.EbmIndirect.Shutdown(); if (!s_quitting) RemovePatches(); }

        private bool _ticksHandedBack;

        private void Update()
        {
            long tSelf = EasyBake.EbmSelfCost.T();   // 1.2.219: EBM's own per-frame cost
            try { UpdateInner(); } finally { EasyBake.EbmSelfCost.Acc(0, tSelf); }
        }

        private void UpdateInner()
        {
            long u7 = EasyBake.EbmSelfCost.UT(); EasyBake.EbmTrace.Heartbeat(); EasyBake.EbmSelfCost.U(7, u7);   // 1.2.96; 1.2.224 timed
            // 1.2.169: these ran with the master switch off, which removes only the Harmony patches
            if (PluginEnabled.Value)
            {
                _ticksHandedBack = false;
                long u0 = EasyBake.EbmSelfCost.UT();
                EasyBake.RemoteItemZSyncSkip.Tick();
                EasyBake.ShelterProbe.Tick();   // 1.2.126
                ShelterRain.Tick();   // 1.2.128
                EasyBake.WetOverride.Tick();   // 1.2.131
                EasyBake.LightBudget.Tick();   // 1.2.136
                EasyBake.EbmSelfCost.U(0, u0); u0 = EasyBake.EbmSelfCost.UT();
                EasyBake.FillTracker.Tick(); EasyBake.EbmSelfCost.U(1, u0); u0 = EasyBake.EbmSelfCost.UT();   EasyBake.Indirect.Coverage.CoverageReach.Tick(); EasyBake.EbmSelfCost.U(5, u0); u0 = EasyBake.EbmSelfCost.UT();   EasyBake.Indirect.CasterCensus.Tick(); EasyBake.Indirect.PhysicsCensus.Tick(); EasyBake.EbmHeapCensus.Tick();   EasyBake.EbmSelfCost.U(6, u0);   /* 1.2.225: + [ghost] physics census */   /* 1.2.223: three timers */   // 1.2.216 ([ghost]): Debug-only 30 s census of vanilla shadow casters   // 1.2.214 ([ghost] P2 + P1 census): one compare a frame unless the camera moved 8 m
            }
            else
            {
                if (!_ticksHandedBack)
                {
                    _ticksHandedBack = true;
                    EasyBake.RemoteItemZSyncSkip.Reset();
                    ShelterRain.HandBack();
                    EasyBake.LightBudget.RestoreAll();
                    EasyBakeLog.Debug("[EBM] PluginEnabled off: rain hider, wet override, shelter probe, light budget and item skip stopped, their changes handed back");
                }
                EasyBake.WetOverride.FollowWhileOff();
            }
            // Kick the disk-cache background preload as soon as the world UID
            // is available. ZNet.GetWorldUID returns 0 until the server world
            // handshake completes; once it's non-zero, the cache directory
            // for this world is well-defined and we can start reading. The
            // worker runs on a ThreadPool task; touching only file I/O + POCO
            // arrays, no Unity API. By the time pieces start streaming in for
            // any zone, most or all of its cache file is already parsed into
            // memory, so the per-zone hit cost is just Mesh.SetVertices.
            //
            // KickPreload is idempotent and cheap when already kicked, so
            // calling it every Update before the first non-zero UID is fine.
            //
            // Route through MeshCacheStore.TryGetWorldUid() â€” it pre-checks
            // ZNet.m_world via reflection. Vanilla GetWorldUID() does an
            // unchecked dereference of m_world and NREs every frame in the
            // ~6s window between ZNet.instance being assigned and the world
            // handshake landing.
            // 1.2.156 ([visual], Fire 10-31 event-driven rule): once the preload is kicked for this ZNet, the per-frame reflection read
            // of m_world stops; a new ZNet (the next world) arms it again. KickPreload was already a no-op on every later frame.
            long u8 = EasyBake.EbmSelfCost.UT();   // 1.2.224: the preload kick (reflection read until kicked)
            if (CachePersistEnabled.Value && !ReferenceEquals(ZNet.instance, s_preloadKickedFor))
            {
                long uid = EasyBake.MeshCacheStore.TryGetWorldUid();
                if (uid != 0L)
                {
                    EasyBake.MeshCacheStore.KickPreload(uid);
                    if (EasyBake.MeshCacheStore.PreloadKickedFor(uid)) s_preloadKickedFor = ZNet.instance;
                    if (s_preloadKickedFor != null) EasyBakeLog.Debug($"[Cache] world id {uid:x16} seen: preload kicked, the per-frame world-id check stops until the next world");
                }
            }

            // Debounced ZONES BAKED mini-box: emits once ~5s after the last
            // cache-construct / fresh-bake event. Runs before the batching
            // gate so the final box still lands if batching is toggled off
            // right after login.
            EasyBake.EbmSelfCost.U(8, u8);
            long u2 = EasyBake.EbmSelfCost.UT();
            BakeSummary.Update();

            // Drains ABOVE the batching gate on purpose. A hit caught for an uncreated piece must still be
            // replayed or reported lost even if batching is switched off in the same breath - inside the gate,
            // toggling the config off mid-flight would leave the queue frozen and the damage silently gone,
            // which is the one outcome this whole path exists to prevent. Costs one int compare when empty.
            EasyBake.DamageReplay.Update();
            EasyBake.EbmSelfCost.U(2, u2);

            if (!BatchingActive()) return;
            long tZone = EasyBake.Probe.Start();
            long trackStart = System.Diagnostics.Stopwatch.GetTimestamp();
            long u3 = EasyBake.EbmSelfCost.UT();
            EasyBake.EbmSelfCost.ZtBegin(); EasyBake.ZoneTracker.Update(); EasyBake.EbmSelfCost.ZtEnd(u3);   // 1.2.224: the worst frame keeps its phase split
            EasyBake.EbmSelfCost.U(3, u3);
            EasyBake.InstancedDraw.NoteZoneTracking(System.Diagnostics.Stopwatch.GetTimestamp() - trackStart);
            EasyBake.Probe.Stop("EasyBake:zoneTrack", tZone);

            // Keepalive lifecycle: refresh entries within the active ring,
            // prune entries that have drifted past KeepaliveRadius or aged
            // out. The RemoveObjects prefix consults the resulting set every
            // CreateDestroyObjects tick.
            // 1.2.122: keep-alive keys are x,z deltas; on the ring (object sectors ring-keyed) it stands down
            if (ZoneKeepaliveEnabled.Value && !EasyBake.EbmZones.Ring
                && ZNet.instance != null
                && ZoneSystem.instance != null
                && Player.m_localPlayer != null)
            {
                var refPos = ZNet.instance.GetReferencePosition();
                var center = ZoneSystem.GetZone(refPos);
                // Valheim 1.0: the fixed m_activeArea radius became the player-configurable,
                    // server-synced SimulationDistance.
                    int activeArea = ZNet.instance.GetSyncedSimulationDistance().NearSimulationDistance;
                long tKeep = EasyBake.Probe.Start();
                long u4 = EasyBake.EbmSelfCost.UT();
            EasyBake.ZoneKeepalive.Update(center, activeArea);
            EasyBake.EbmSelfCost.U(4, u4);
                EasyBake.Probe.Stop("EasyBake:keepalive", tKeep);
            }

            // Sector mirror periodic rebuild â€” corrects drift from instances
            // that moved sectors since they were instantiated. Cheap when
            // disabled (SectorMirrorRebuildSeconds=0) or interval not yet
            // elapsed; expensive (~1 walk of m_instances) when it actually
            // rebuilds.
            long u9 = EasyBake.EbmSelfCost.UT();   // 1.2.224
            if (SectorMirrorEnabled.Value && ZNetScene.instance != null)
            {
                long tMirror = EasyBake.Probe.Start();
                EasyBake.SectorInstanceMirror.MaybeRebuild(ZNetScene.instance);
                EasyBake.Probe.Stop("EasyBake:sectorMirror", tMirror);
            }

            // Static-piece ZSync skip: periodic verbose summary if enabled.
            // No-op when ZSyncStaticSkipVerbose=false. Cheap enough to call
            // every Update â€” guards on the throttle internally.
            EasyBake.StaticPieceZSyncSkip.MaybeReport();
            EasyBake.EbmSelfCost.U(9, u9);
        }

        // Instances are drawn here rather than in Update because GameCamera moves the
        // camera in its own LateUpdate: deciding what is out of view any earlier would
        // test last frame's view and let a fast turn clip geometry at the screen edge.
        private void LateUpdate()
        {
            long tSelf = EasyBake.EbmSelfCost.T();   // 1.2.219: EBM's own per-frame cost
            try { LateUpdateInner(); } finally { EasyBake.EbmSelfCost.Acc(1, tSelf); }
        }

        private void LateUpdateInner()
        {
            EasyBake.Indirect.Coverage.CoveredObject.FlushPending();   // CoverageBatchedStart: Unity runs this frame's Starts before the LateUpdate phase, so every spawn of ZNetScene.Update is queued by now
            if (!BatchingActive()) return;
            bool hot = EasyBake.EbmTrace.Hot;   // 1.2.96: trace the draw only right after destroys
            if (hot) EasyBake.EbmTrace.Step("draw instances begin", Time.frameCount);
            EasyBake.ZoneTracker.DrawInstances();
            if (hot) EasyBake.EbmTrace.Step("draw instances end", Time.frameCount);
        }

        public static bool BatchingActive()
        {
            if (!PluginEnabled.Value) return false;
            if (!BatchingEnabled.Value) return false;
            if (BatchingRunOnServer.Value) return true;
            return !(ZNet.instance != null && ZNet.instance.IsDedicated());
        }
    }
}
