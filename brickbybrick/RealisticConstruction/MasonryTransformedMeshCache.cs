using System;
using System.Collections.Generic;
using Vintagestory.API.Client;

namespace brickbybrick.RealisticConstruction
{
    // Retains small, frequently reused component transforms independently of
    // per-cell frozen meshes. A byte budget avoids misleading entry limits.
    internal static class MasonryTransformedMeshCache
    {
        private sealed class CacheEntry
        {
            internal required string Key { get; init; }
            internal required MeshData Mesh { get; init; }
            internal required long EstimatedBytes { get; init; }
        }

        private static readonly object SyncRoot = new();
        private static readonly Dictionary<string, LinkedListNode<CacheEntry>> Entries = new();
        private static readonly LinkedList<CacheEntry> Usage = new();
        private static long estimatedBytes;
        private static long hits;
        private static long misses;
        private static long evictions;

        internal static MeshData GetOrCreate(string key, Func<MeshData> createMesh)
        {
            lock (SyncRoot)
            {
                if (Entries.TryGetValue(key, out LinkedListNode<CacheEntry>? existing))
                {
                    Usage.Remove(existing);
                    Usage.AddFirst(existing);
                    hits++;
                    return existing.Value.Mesh;
                }
            }

            MeshData created = createMesh();
            created.CompactBuffers();
            long bytes = EstimateBytes(created);
            lock (SyncRoot)
            {
                if (Entries.TryGetValue(key, out LinkedListNode<CacheEntry>? raced))
                {
                    Usage.Remove(raced);
                    Usage.AddFirst(raced);
                    hits++;
                    return raced.Value.Mesh;
                }

                CacheEntry entry = new() { Key = key, Mesh = created, EstimatedBytes = bytes };
                Entries[key] = Usage.AddFirst(entry);
                estimatedBytes += bytes;
                misses++;
                TrimToBudget();
                return created;
            }
        }

        internal static void ResetProfile()
        {
            lock (SyncRoot) hits = misses = evictions = 0;
        }

        internal static string GetProfile()
        {
            lock (SyncRoot)
            {
                return $"transformed cache: {Entries.Count:N0} entries, {estimatedBytes / 1048576d:N1} MiB, "
                    + $"{hits:N0} hits, {misses:N0} misses, {evictions:N0} evictions";
            }
        }

        internal static void Clear()
        {
            lock (SyncRoot)
            {
                Entries.Clear();
                Usage.Clear();
                estimatedBytes = 0;
            }
        }

        private static void TrimToBudget()
        {
            long budget = (long)brickbybrickModSystem.Config.Realism.TransformedMeshCacheMiB * 1048576L;
            while (estimatedBytes > budget && Usage.Last != null)
            {
                CacheEntry entry = Usage.Last.Value;
                Usage.RemoveLast();
                Entries.Remove(entry.Key);
                estimatedBytes -= entry.EstimatedBytes;
                evictions++;
            }
        }

        private static long EstimateBytes(MeshData mesh)
        {
            return Math.Max(256, mesh.VerticesCount * 64L + mesh.IndicesCount * 8L);
        }
    }
}
