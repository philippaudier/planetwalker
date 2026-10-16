using System;
using System.Collections.Generic;
using UnityEngine;

namespace Planetwalker.Terrain
{
    /// <summary>
    /// Owner of the adaptive terrain patch meshes (V0.2.0A): at most <see cref="Capacity"/> patches (each a surface
    /// mesh and its vertex-grid line mesh),
    /// created on demand, recycled (least recently used first) instead of destroyed, and destroyed explicitly by
    /// <see cref="Dispose"/> (never left to the garbage collector). Entries in use by the caller are protected.
    /// </summary>
    public sealed class PatchMeshCache : IDisposable
    {
        public sealed class Entry
        {
            public PlanetPatchId Id;
            public Mesh Mesh;
            /// <summary>The patch's vertex grid as lines (diagnostics; null for the adaptive renderer).</summary>
            public Mesh Grid;
            /// <summary>Planet generation the mesh was built for (adaptive renderer).</summary>
            public int Generation = -1;
            /// <summary>Stitch mask of the index buffer currently uploaded (-1: none).</summary>
            public int Mask = -1;
            public PatchGeometry Geometry;
            public int LastUsedFrame;
        }

        readonly Dictionary<PlanetPatchId, Entry> entries = new Dictionary<PlanetPatchId, Entry>();
        readonly List<Entry> free = new List<Entry>();
        int meshCount;

        public PatchMeshCache(int capacity)
        {
            Capacity = Math.Max(1, capacity);
        }

        /// <summary>Most Mesh objects ever alive at once.</summary>
        public int Capacity { get; private set; }

        /// <summary>Meshes holding a patch.</summary>
        public int ResidentCount => entries.Count;

        /// <summary>Patch slots alive (resident + recycled, at most <see cref="Capacity"/>; two Mesh objects each).</summary>
        public int MeshCount => meshCount;

        /// <summary>Meshes created since the cache exists (bounded reuse shows as a plateau).</summary>
        public int CreatedCount { get; private set; }

        public int BuildCount { get; private set; }

        public bool TryGet(PlanetPatchId id, out Entry entry) => entries.TryGetValue(id, out entry);

        public bool Contains(PlanetPatchId id) => entries.ContainsKey(id);

        /// <summary>Lowers or raises the capacity (excess meshes are destroyed, least recently used first).</summary>
        public void SetCapacity(int capacity, Func<PlanetPatchId, bool> isProtected)
        {
            Capacity = Math.Max(1, capacity);
            while (meshCount > Capacity)
            {
                if (free.Count > 0)
                {
                    DestroyEntry(free[free.Count - 1]);
                    free.RemoveAt(free.Count - 1);
                    continue;
                }
                Entry victim = FindVictim(isProtected);
                if (victim == null)
                {
                    break;
                }
                entries.Remove(victim.Id);
                DestroyEntry(victim);
            }
        }

        /// <summary>
        /// Builds a patch into a mesh: a recycled one, a new one while under capacity, or the least recently used
        /// unprotected entry. Returns null when every mesh is protected (the caller reports the budget).
        /// </summary>
        public Entry Build(PlanetPatchId id, PatchMeshBuilder builder, TerrainElevationSampler sampler, Color tint, Color gridColor,
            float gridLift, int frame, Func<PlanetPatchId, bool> isProtected)
        {
            if (entries.TryGetValue(id, out Entry existing))
            {
                existing.LastUsedFrame = frame;
                return existing;
            }
            Entry entry = Acquire(id, frame, isProtected);
            if (entry == null)
            {
                return null;
            }
            entry.Geometry = builder.Build(id, sampler, tint);
            builder.Upload(entry.Mesh, entry.Geometry);
            entry.Grid ??= new Mesh { hideFlags = HideFlags.DontSave };
            builder.UploadGrid(entry.Grid, entry.Geometry, gridColor, gridLift);
            BuildCount++;
            return entry;
        }

        /// <summary>
        /// A mesh slot registered under <paramref name="id"/> for the caller to fill: a recycled one, a new one while
        /// under capacity, or the least recently used unprotected entry. Null when every slot is protected.
        /// </summary>
        public Entry Acquire(PlanetPatchId id, int frame, Func<PlanetPatchId, bool> isProtected)
        {
            if (entries.TryGetValue(id, out Entry existing))
            {
                existing.LastUsedFrame = frame;
                return existing;
            }
            Entry entry;
            if (free.Count > 0)
            {
                entry = free[free.Count - 1];
                free.RemoveAt(free.Count - 1);
            }
            else if (meshCount < Capacity)
            {
                entry = new Entry { Mesh = new Mesh { hideFlags = HideFlags.DontSave } };
                meshCount++;
                CreatedCount++;
            }
            else
            {
                entry = FindVictim(isProtected);
                if (entry == null)
                {
                    return null;
                }
                entries.Remove(entry.Id);
            }
            entry.Id = id;
            entry.Generation = -1;
            entry.Mask = -1;
            entry.LastUsedFrame = frame;
            entries.Add(id, entry);
            return entry;
        }

        Entry FindVictim(Func<PlanetPatchId, bool> isProtected)
        {
            Entry victim = null;
            foreach (Entry e in entries.Values)
            {
                if ((isProtected == null || !isProtected(e.Id))
                    && (victim == null || e.LastUsedFrame < victim.LastUsedFrame || (e.LastUsedFrame == victim.LastUsedFrame && e.Id.Key < victim.Id.Key)))
                {
                    victim = e;
                }
            }
            return victim;
        }

        /// <summary>Forgets one patch (its slot is kept for reuse).</summary>
        public void Remove(PlanetPatchId id)
        {
            if (entries.TryGetValue(id, out Entry entry))
            {
                entries.Remove(id);
                free.Add(entry);
            }
        }

        /// <summary>Forgets every patch (stale planet or resolution); the meshes are kept for reuse.</summary>
        public void Invalidate()
        {
            foreach (Entry e in entries.Values)
            {
                free.Add(e);
            }
            entries.Clear();
        }

        /// <summary>Destroys every native mesh.</summary>
        public void Dispose()
        {
            foreach (Entry e in entries.Values)
            {
                DestroyEntry(e);
            }
            foreach (Entry e in free)
            {
                DestroyEntry(e);
            }
            entries.Clear();
            free.Clear();
            meshCount = 0;
        }

        void DestroyEntry(Entry entry)
        {
            meshCount--;
            DestroyMesh(entry.Mesh);
            DestroyMesh(entry.Grid);
            entry.Mesh = null;
            entry.Grid = null;
        }

        static void DestroyMesh(Mesh mesh)
        {
            if (mesh == null)
            {
                return;
            }
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(mesh);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }
    }
}
