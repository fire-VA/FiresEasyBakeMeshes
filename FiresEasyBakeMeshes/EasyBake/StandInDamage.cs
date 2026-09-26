using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // ═══ MAKING A PIECE THAT DOES NOT EXIST HITTABLE ══════════════════════════
    // A skipped piece has stand-in colliders and nothing else. Players, creatures and projectiles collide
    // with them, and a support check that finds no WearNTear treats them as solid ground - but a WEAPON finds
    // nothing to damage, and a raid finds nothing to target. Two small components fix both, because vanilla
    // resolves each through a component lookup rather than through the piece itself.
    //
    // WHERE THEY GO, and why the parent is correct:
    //
    //   Attack -> Projectile.FindHitObject(collider):
    //       collider.gameObject.GetComponentInParent<IDestructible>()   <-- IN PARENT
    //   then  gameObject.GetComponent<IDestructible>()  on whatever that returned.
    //
    // So the proxy belongs on the object StandInColliders.Build() returns, which is the collider itself when
    // a piece has one and the "StandIn" holder when it has several. Both shapes resolve: from a child
    // collider GetComponentInParent finds the holder, FindHitObject returns the holder, and the second
    // GetComponent finds the same proxy. It must be a MonoBehaviour - FindHitObject casts to one.
    //
    // Raids need nothing from us beyond the collider: BaseAI.FindClosestStaticPriorityTarget does
    // Physics.OverlapSphere then GetComponentInParent<StaticTarget>(), so the overlap ALREADY finds stand-in
    // colliders; it just finds no StaticTarget to return. StaticTarget is 91 lines of pure geometry - centre,
    // closest point, priority flags, no Update and no state beyond cached colliders - so it works off the
    // stand-in exactly as it works off a real piece.
    //
    // DAMAGE IS OWNER-AUTHORITATIVE, which is what makes this sound rather than a fake:
    //
    //   WearNTear.Damage(hit)  =>  m_nview.InvokeRPC("RPC_Damage", hit)      // fire and forget
    //   WearNTear.RPC_Damage   =>  returns unless m_nview.IsOwner()          // only the owner acts
    //
    // The client swinging never applies damage; it routes. So this proxy does the same thing WearNTear does,
    // from a ZDOID instead of a ZNetView, and the owner applies it with full support, collapse and drop
    // handling. On a dedicated server the owner is usually the server, which keeps every piece live.
    //
    // THE HOLE THIS DOES NOT CLOSE: when THIS client owns the piece, the RPC comes back to us and
    // ZRoutedRpc.HandleRoutedRPC drops it silently, because FindInstance returns null for something never
    // created. Measured 2026-09-25 in the BlueHills town, 20,777 of 38,154 building pieces (54.5%) were owned
    // by the client. That is phase 2's job; until it exists, only pieces that CANNOT be damaged are ever
    // bound here, so nothing can be lost.
    internal sealed class StandInDamage : MonoBehaviour, IDestructible
    {
        internal ZDOID PieceId;
        internal DestructibleType StandsInFor = DestructibleType.Default;

        public void Damage(HitData hit)
        {
            if (PieceId == ZDOID.None) return;
            var man = ZDOMan.instance;
            if (man == null) return;
            var zdo = man.GetZDO(PieceId);
            if (zdo == null) return;

            // THE PIECE MAY HAVE BECOME REAL SINCE THIS PROXY WAS BOUND. Stand-in GameObjects are keyed by piece
            // identity and are reused across zone revisits, so a proxy can outlive the skip that created it. If
            // the piece is live again it has its own WearNTear, which answered this swing itself - routing here
            // too would apply the same hit twice.
            if (!ZoneTracker.IsSkipped(zdo)) return;

            var rpc = ZRoutedRpc.instance;
            if (rpc == null) return;

            // Exactly what ZNetView.InvokeRPC does for a piece that exists: aim the routed call at the ZDO's
            // owner. When that owner is us the call lands back here and is dropped until phase 2 catches it.
            rpc.InvokeRoutedRPC(zdo.GetOwner(), PieceId, "RPC_Damage", hit);
        }

        // Whatever the real prefab answers, because Attack bitmasks this against the weapon's skill hit type -
        // (GetDestructibleType() & m_skillHitType) != 0 - so a wrong value makes some weapons silently fail to
        // register. WearNTear answers Default, but TreeBase and TreeLog answer Tree and Destructible answers its
        // own serialized m_destructibleType, so a hardcoded Default would make an axe unable to chop a skipped tree.
        public DestructibleType GetDestructibleType() => StandsInFor;
    }

    internal static class StandInDamageBinding
    {
        // Phase 1 is INERT by construction. Bind is only ever called for a skipped piece that can actually be
        // damaged, and no damageable piece is skipped until phase 3 turns that on. Invulnerable stand-ins -
        // everything skipped today - are deliberately left exactly as they are: giving them an IDestructible
        // would change how they answer a weapon, which is a behaviour change nobody asked for and which this
        // phase is supposed not to make.
        internal static void Bind(GameObject standIn, ZDO zdo)
        {
            if (standIn == null || zdo == null) return;

            var proxy = standIn.GetComponent<StandInDamage>();
            if (proxy == null) proxy = standIn.AddComponent<StandInDamage>();
            proxy.PieceId = zdo.m_uid;
            proxy.StandsInFor = DestructibleTypeOf(zdo.GetPrefab());

            if (standIn.GetComponent<StaticTarget>() == null)
            {
                var target = standIn.AddComponent<StaticTarget>();
                // A building piece is a legitimate raid target but not a priority one - priority is for the
                // things a raid makes a beeline for, and treating every wall as one would rewrite raid
                // behaviour rather than preserve it.
                target.m_primaryTarget = false;
                target.m_randomTarget = true;
            }
        }

        private static readonly Dictionary<int, DestructibleType> _byPrefab = new Dictionary<int, DestructibleType>();

        internal static void ClearDestructibleTypeCache() => _byPrefab.Clear();

        private static DestructibleType DestructibleTypeOf(int prefabHash)
        {
            if (_byPrefab.TryGetValue(prefabHash, out var cached)) return cached;

            var scene = ZNetScene.instance;
            var prefab = scene != null ? scene.GetPrefab(prefabHash) : null;
            var destructible = prefab != null ? prefab.GetComponent<IDestructible>() : null;
            var type = destructible != null ? destructible.GetDestructibleType() : DestructibleType.Default;

            _byPrefab[prefabHash] = type;
            return type;
        }

        // A piece handed back becomes real and gets its own WearNTear and StaticTarget, so the stand-in must
        // stop answering for it - two IDestructibles for one piece would double every hit.
        internal static void Unbind(GameObject standIn)
        {
            if (standIn == null) return;
            var proxy = standIn.GetComponent<StandInDamage>();
            if (proxy != null) proxy.PieceId = ZDOID.None;
        }
    }
}
