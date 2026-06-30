using System;
using System.Collections.Generic;
using Vintagestory.API.Client;

namespace brickbybrick.RealisticConstruction
{
    // Bounds retained per-cell meshes so dense Realistic builds cannot consume
    // unbounded client memory. Evicted cells rebuild from packed unit state.
    internal static class MasonryFrozenMeshCache
    {
        private sealed class CacheEntry
        {
            internal required BlockEntityRealisticMasonry Entity { get; init; }
            internal required MeshData Mesh { get; init; }
            internal required long EstimatedBytes { get; init; }
        }

        private static readonly object SyncRoot = new();
        private static readonly LinkedList<CacheEntry> Usage = new();
        private static readonly Dictionary<BlockEntityRealisticMasonry, LinkedListNode<CacheEntry>> Entries = new();
        private static long estimatedBytes;
        private static long evictions;

        internal static void Store(BlockEntityRealisticMasonry entity, MeshData mesh)
        {
            lock (SyncRoot)
            {
                RemoveInternal(entity);
                CacheEntry entry = new()
                {
                    Entity = entity,
                    Mesh = mesh,
                    EstimatedBytes = EstimateBytes(mesh)
                };
                Entries[entity] = Usage.AddFirst(entry);
                estimatedBytes += entry.EstimatedBytes;
                TrimToBudget();
            }
        }

        internal static void Touch(BlockEntityRealisticMasonry entity)
        {
            lock (SyncRoot)
            {
                if (!Entries.TryGetValue(entity, out LinkedListNode<CacheEntry>? node)) return;
                Usage.Remove(node);
                Usage.AddFirst(node);
            }
        }

        internal static void Remove(BlockEntityRealisticMasonry entity)
        {
            lock (SyncRoot) RemoveInternal(entity);
        }

        internal static string GetProfile()
        {
            lock (SyncRoot)
            {
                return $"retained frozen meshes: {Entries.Count:N0}; estimated cache: {estimatedBytes / 1048576d:N1} MiB; evictions: {evictions:N0}";
            }
        }

        private static void TrimToBudget()
        {
            long budgetBytes = (long)brickbybrickModSystem.Config.Realism.FrozenMeshCacheMiB * 1048576L;
            while (estimatedBytes > budgetBytes && Usage.Last != null)
            {
                CacheEntry entry = Usage.Last.Value;
                Usage.RemoveLast();
                Entries.Remove(entry.Entity);
                estimatedBytes -= entry.EstimatedBytes;
                evictions++;
                entry.Entity.ReleaseFrozenMesh(entry.Mesh);
            }
        }

        private static void RemoveInternal(BlockEntityRealisticMasonry entity)
        {
            if (!Entries.Remove(entity, out LinkedListNode<CacheEntry>? node)) return;
            Usage.Remove(node);
            estimatedBytes -= node.Value.EstimatedBytes;
        }

        private static long EstimateBytes(MeshData mesh)
        {
            // MeshData uses power-of-two backing buffers. This conservative
            // estimate includes vertex attributes, indices, and array slack.
            return Math.Max(256, mesh.VerticesCount * 64L + mesh.IndicesCount * 8L);
        }
    }
}
