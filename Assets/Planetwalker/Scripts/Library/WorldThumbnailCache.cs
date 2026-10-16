using System;
using System.Collections.Generic;
using Planetwalker.Persistence;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Planetwalker.Library
{
    /// <summary>
    /// Decoded world thumbnails for the library (V0.1.3B): loaded lazily, at most <see cref="Capacity"/> textures
    /// (least recently used evicted and destroyed), all released by <see cref="Clear"/> when the library closes.
    /// Missing or corrupt thumbnails are remembered (no repeated disk reads) and return null: the UI shows a
    /// placeholder, the archive is unaffected.
    /// </summary>
    public sealed class WorldThumbnailCache
    {
        public const int MaxDimension = 1024;

        readonly WorldThumbnailStore store;
        readonly Dictionary<Guid, LinkedListNode<(Guid Id, Texture2D Texture)>> entries = new Dictionary<Guid, LinkedListNode<(Guid, Texture2D)>>();
        readonly LinkedList<(Guid Id, Texture2D Texture)> recency = new LinkedList<(Guid, Texture2D)>();
        readonly HashSet<Guid> unavailable = new HashSet<Guid>();

        public WorldThumbnailCache(WorldThumbnailStore store, int capacity = 48)
        {
            this.store = store;
            Capacity = Mathf.Max(1, capacity);
        }

        public int Capacity { get; }

        /// <summary>Textures currently alive in the cache.</summary>
        public int LoadedCount => entries.Count;

        /// <summary>Image decodes performed (disk read + PNG decode).</summary>
        public int DecodeCount { get; private set; }

        /// <summary>True when the thumbnail is decoded or known to be unavailable (no disk access needed).</summary>
        public bool IsResolved(Guid id) => entries.ContainsKey(id) || unavailable.Contains(id);

        /// <summary>The decoded thumbnail, decoding it if needed; null when missing or corrupt.</summary>
        public Texture2D Get(Guid id)
        {
            if (entries.TryGetValue(id, out LinkedListNode<(Guid, Texture2D)> node))
            {
                recency.Remove(node);
                recency.AddFirst(node);
                return node.Value.Item2;
            }
            if (unavailable.Contains(id) || store == null)
            {
                return null;
            }
            Texture2D texture = Decode(id);
            if (texture == null)
            {
                unavailable.Add(id);
                return null;
            }
            entries[id] = recency.AddFirst((id, texture));
            while (entries.Count > Capacity)
            {
                LinkedListNode<(Guid Id, Texture2D Texture)> last = recency.Last;
                recency.RemoveLast();
                entries.Remove(last.Value.Id);
                DestroySafe(last.Value.Texture);
            }
            return texture;
        }

        Texture2D Decode(Guid id)
        {
            if (!store.TryRead(id, out byte[] png, out _))
            {
                return null;
            }
            DecodeCount++;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = "World Thumbnail " + PlanetArchive.FormatId(id).Substring(0, 8),
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            bool ok;
            try
            {
                ok = texture.LoadImage(png, true);
            }
            catch (Exception)
            {
                ok = false;
            }
            if (!ok || texture.width > MaxDimension || texture.height > MaxDimension || texture.width < 8 || texture.height < 8)
            {
                DestroySafe(texture);
                return null;
            }
            return texture;
        }

        /// <summary>Forgets one thumbnail (its file changed or was removed); its texture is destroyed.</summary>
        public void Invalidate(Guid id)
        {
            unavailable.Remove(id);
            if (entries.TryGetValue(id, out LinkedListNode<(Guid Id, Texture2D Texture)> node))
            {
                recency.Remove(node);
                entries.Remove(id);
                DestroySafe(node.Value.Texture);
            }
        }

        /// <summary>Destroys every decoded texture (library closed).</summary>
        public void Clear()
        {
            foreach (LinkedListNode<(Guid Id, Texture2D Texture)> node in entries.Values)
            {
                DestroySafe(node.Value.Texture);
            }
            entries.Clear();
            recency.Clear();
            unavailable.Clear();
        }

        static void DestroySafe(Object target)
        {
            if (target == null)
            {
                return;
            }
            if (Application.isPlaying)
            {
                Object.Destroy(target);
            }
            else
            {
                Object.DestroyImmediate(target);
            }
        }
    }
}
