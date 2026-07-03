using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;

namespace brickbybrick.RealisticConstruction
{
    // Retains immutable frozen meshes by rendered-state identity. Multiple
    // cells with the same arrangement share one buffer and one budget charge.
    internal static class MasonryFrozenMeshCache
    {
        private sealed class CacheEntry
        {
            internal required string Key { get; init; }
            internal required MeshData Mesh { get; init; }
            internal required long EstimatedBytes { get; init; }
            internal HashSet<BlockEntityRealisticMasonry> Owners { get; } = new();
        }

        private static readonly object SyncRoot = new();
        private static readonly LinkedList<CacheEntry> Usage = new();
        private static readonly Dictionary<string, LinkedListNode<CacheEntry>> Entries = new();
        private static readonly Dictionary<BlockEntityRealisticMasonry, LinkedListNode<CacheEntry>> Owners = new();
        private static ConditionalWeakTable<BlockEntityRealisticMasonry, object> OversizedSeen = new();
        private static long estimatedBytes;
        private static long evictions;
        private static long admissionRejections;
        private static long deduplicationHits;
        private static long evictionRebuilds;
        private static long evictionRebuildTicks;
        private static long slowestEvictionRebuildTicks;
        private static long largestRebuiltBytes;

        internal static bool Store(BlockEntityRealisticMasonry owner, string key, MeshData mesh)
        {
            lock (SyncRoot)
            {
                RemoveOwnerInternal(owner);
                if (Entries.TryGetValue(key, out LinkedListNode<CacheEntry>? shared))
                {
                    shared.Value.Owners.Add(owner);
                    Owners[owner] = shared;
                    Usage.Remove(shared);
                    Usage.AddFirst(shared);
                    deduplicationHits++;
                    owner.AdoptFrozenMesh(shared.Value.Mesh);
                    return true;
                }

                mesh.CompactBuffers();
                long bytes = EstimateBytes(mesh);
                long budget = (long)brickbybrickModSystem.Config.Realism.FrozenMeshCacheMiB * 1048576L;
                long firstHitLimit = Math.Min(8L * 1048576L, budget / 8);
                if (bytes > budget / 2 || bytes > firstHitLimit && !OversizedSeen.TryGetValue(owner, out _))
                {
                    if (bytes <= budget / 2) OversizedSeen.Add(owner, new object());
                    admissionRejections++;
                    return false;
                }

                CacheEntry entry = new() { Key = key, Mesh = mesh, EstimatedBytes = bytes };
                entry.Owners.Add(owner);
                LinkedListNode<CacheEntry> node = Usage.AddFirst(entry);
                Entries[key] = node;
                Owners[owner] = node;
                estimatedBytes += bytes;
                TrimToBudget();
                return Owners.ContainsKey(owner);
            }
        }

        internal static void Touch(BlockEntityRealisticMasonry owner)
        {
            lock (SyncRoot)
            {
                if (!Owners.TryGetValue(owner, out LinkedListNode<CacheEntry>? node)) return;
                Usage.Remove(node);
                Usage.AddFirst(node);
            }
        }

        internal static void Remove(BlockEntityRealisticMasonry owner)
        {
            lock (SyncRoot) RemoveOwnerInternal(owner);
        }

        internal static void RecordEvictionRebuild(long elapsedTicks, MeshData mesh)
        {
            lock (SyncRoot)
            {
                evictionRebuilds++;
                evictionRebuildTicks += elapsedTicks;
                slowestEvictionRebuildTicks = Math.Max(slowestEvictionRebuildTicks, elapsedTicks);
                largestRebuiltBytes = Math.Max(largestRebuiltBytes, EstimateBytes(mesh));
            }
        }

        internal static string GetProfile()
        {
            lock (SyncRoot)
            {
                double rebuildMilliseconds = evictionRebuildTicks * 1000d / System.Diagnostics.Stopwatch.Frequency;
                double slowestMilliseconds = slowestEvictionRebuildTicks * 1000d / System.Diagnostics.Stopwatch.Frequency;
                return $"retained frozen meshes: {Entries.Count:N0} shared across {Owners.Count:N0} cells; "
                    + $"estimated cache: {estimatedBytes / 1048576d:N1} MiB; dedup hits: {deduplicationHits:N0}; "
                    + $"evictions: {evictions:N0}; admission rejects: {admissionRejections:N0}; "
                    + $"eviction rebuilds: {evictionRebuilds:N0}, {rebuildMilliseconds:N1} ms total, "
                    + $"{slowestMilliseconds:N2} ms slowest, {largestRebuiltBytes / 1048576d:N1} MiB largest";
            }
        }

        internal static void ResetProfile()
        {
            lock (SyncRoot)
            {
                evictions = admissionRejections = deduplicationHits = evictionRebuilds = evictionRebuildTicks = 0;
                slowestEvictionRebuildTicks = largestRebuiltBytes = 0;
            }
        }

        internal static void Clear()
        {
            List<(BlockEntityRealisticMasonry Owner, MeshData Mesh)> releases = new();
            lock (SyncRoot)
            {
                foreach (CacheEntry entry in Usage)
                foreach (BlockEntityRealisticMasonry owner in entry.Owners) releases.Add((owner, entry.Mesh));
                Entries.Clear();
                Owners.Clear();
                Usage.Clear();
                estimatedBytes = 0;
                OversizedSeen = new ConditionalWeakTable<BlockEntityRealisticMasonry, object>();
            }

            foreach ((BlockEntityRealisticMasonry owner, MeshData mesh) in releases) owner.ReleaseFrozenMesh(mesh, false);
        }

        private static void TrimToBudget()
        {
            long budget = (long)brickbybrickModSystem.Config.Realism.FrozenMeshCacheMiB * 1048576L;
            while (estimatedBytes > budget && Usage.Last != null)
            {
                CacheEntry entry = Usage.Last.Value;
                Usage.RemoveLast();
                Entries.Remove(entry.Key);
                estimatedBytes -= entry.EstimatedBytes;
                evictions++;
                foreach (BlockEntityRealisticMasonry owner in entry.Owners)
                {
                    Owners.Remove(owner);
                    owner.ReleaseFrozenMesh(entry.Mesh);
                }
            }
        }

        private static void RemoveOwnerInternal(BlockEntityRealisticMasonry owner)
        {
            if (!Owners.Remove(owner, out LinkedListNode<CacheEntry>? node)) return;
            node.Value.Owners.Remove(owner);
            if (node.Value.Owners.Count > 0) return;
            Usage.Remove(node);
            Entries.Remove(node.Value.Key);
            estimatedBytes -= node.Value.EstimatedBytes;
        }

        private static long EstimateBytes(MeshData mesh)
        {
            return Math.Max(256, mesh.VerticesCount * 64L + mesh.IndicesCount * 8L);
        }
    }
}
