using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // Every instance of one prefab inside one zone. Scoping groups per zone rather
    // than globally is what gives each draw a tight worldBounds, so Unity can cull a
    // whole zone's instances in one test; it also makes invalidation O(1) - a removed
    // piece drops its matrix instead of forcing the zone to re-combine.
    internal class ZoneInstanceGroup
    {
        // Graphics.RenderMeshInstanced draws at most this many instances per call.
        private const int MaxInstancesPerDraw = 1023;

        public int PrefabHash;
        public InstanceDefinition Definition;
        // In the first part's space: the piece transform with that part's offset folded in.
        // Later parts are this list carried through their own offset from the first.
        public readonly List<Matrix4x4> Matrices = new List<Matrix4x4>();
        // Parallel to Matrices. Lets a removed piece drop out of the group directly
        // instead of dirtying the zone into a full rebake, which is the whole reason
        // instancing is worth having for pieces players actually tear down.
        public readonly List<MeshBaker.PieceIdentity> Identities = new List<MeshBaker.PieceIdentity>();

        private Bounds _worldBounds;
        private bool _boundsDirty;
        private List<Matrix4x4>[] _partMatrices;
        private bool _partsDirty = true;

        public int Count => Matrices.Count;

        public Bounds WorldBounds
        {
            get
            {
                if (_boundsDirty)
                {
                    _boundsDirty = false;
                    RecomputeBounds();
                }
                return _worldBounds;
            }
        }

        public bool CastsShadows => Definition == null || Definition.CastsShadows;

        public void Add(Matrix4x4 pieceLocalToWorld, MeshBaker.PieceIdentity identity)
        {
            Matrices.Add(pieceLocalToWorld * Definition.LocalOffset);
            Identities.Add(identity);
            _partsDirty = true;
        }

        // A piece joining after the bake; many can land in one frame, so bounds are recomputed once at the next draw.
        public void AddLate(Matrix4x4 pieceLocalToWorld, MeshBaker.PieceIdentity identity)
        {
            Add(pieceLocalToWorld, identity);
            _boundsDirty = true;
        }

        public void AddCached(Matrix4x4 instanceMatrix, MeshBaker.PieceIdentity identity)
        {
            Matrices.Add(instanceMatrix);
            Identities.Add(identity);
            _partsDirty = true;
        }

        public bool TryRemove(MeshBaker.PieceIdentity identity)
        {
            int index = Identities.IndexOf(identity);
            if (index < 0) return false;

            int last = Matrices.Count - 1;
            Matrices[index] = Matrices[last];
            Identities[index] = Identities[last];
            Matrices.RemoveAt(last);
            Identities.RemoveAt(last);
            _partsDirty = true;
            RecomputeBounds();
            return true;
        }

        // Bounds are recomputed once after the group is filled. Pieces in a base do
        // not move, so there is nothing to keep in sync per frame. Each instance's
        // mesh box is carried through its whole matrix: wood_floor's vertices sit
        // 52 m from its pivot, and a box around the pivot let Unity cull the group
        // while it was on screen.
        public void RecomputeBounds()
        {
            if (Matrices.Count == 0)
            {
                _worldBounds = new Bounds(Vector3.zero, Vector3.zero);
                return;
            }

            var local = Definition.MeshBounds;
            var e = local.extents;
            for (int i = 0; i < Matrices.Count; i++)
            {
                var m = Matrices[i];
                var centre = m.MultiplyPoint3x4(local.center);
                var reach = new Vector3(
                    Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z,
                    Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z,
                    Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z);
                if (i == 0)
                {
                    _worldBounds = new Bounds(centre, reach * 2f);
                    continue;
                }
                _worldBounds.Encapsulate(centre + reach);
                _worldBounds.Encapsulate(centre - reach);
            }
        }

        public void Draw(bool farTier)
        {
            int count = Matrices.Count;
            if (count == 0) return;

            var parts = Definition.Parts;
            if (parts == null || parts.Length == 0) return;
            EnsurePartMatrices(parts);
            var bounds = WorldBounds;

            for (int p = 0; p < parts.Length; p++)
            {
                var part = parts[p];
                var mesh = farTier ? part.FarMesh : part.NearMesh;
                if (mesh == null) continue;

                var matrices = p == 0 ? Matrices : _partMatrices[p];
                var renderParams = part.RenderParams;
                renderParams.worldBounds = bounds;

                for (int start = 0; start < count; start += MaxInstancesPerDraw)
                {
                    int batchCount = Mathf.Min(MaxInstancesPerDraw, count - start);
                    Graphics.RenderMeshInstanced(in renderParams, mesh, part.SubMesh, matrices, batchCount, start);
                }
            }
        }

        // Parts after the first draw the same pieces from a fixed offset, so their matrix lists
        // are derived once per change rather than rebuilt per frame.
        private void EnsurePartMatrices(InstancePart[] parts)
        {
            if (parts.Length < 2) return;
            if (!_partsDirty && _partMatrices != null && _partMatrices.Length == parts.Length) return;

            if (_partMatrices == null || _partMatrices.Length != parts.Length)
                _partMatrices = new List<Matrix4x4>[parts.Length];

            for (int p = 1; p < parts.Length; p++)
            {
                var list = _partMatrices[p];
                if (list == null)
                {
                    list = new List<Matrix4x4>(Matrices.Count);
                    _partMatrices[p] = list;
                }
                list.Clear();
                var fromFirst = parts[p].FromFirst;
                for (int i = 0; i < Matrices.Count; i++) list.Add(Matrices[i] * fromFirst);
            }
            _partsDirty = false;
        }
    }
}
