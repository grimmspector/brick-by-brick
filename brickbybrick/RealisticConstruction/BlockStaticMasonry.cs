using System;
using System.Collections.Generic;
using System.Threading;
using AttributeRenderingLibrary;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace brickbybrick.RealisticConstruction
{
    // Represents cured masonry whose reconstruction state is stored on its
    // chunk. These variants describe geometry only and never own an entity.
    public sealed class BlockStaticMasonry : Block
    {
        private const string ShapeVariantKey = "shape";
        private static readonly BoundedCache<MeshData> MeshCache = new(
            mesh => Math.Max(256L, mesh.VerticesCount * 64L + mesh.IndicesCount * 8L),
            () => (long)brickbybrickModSystem.Config.Realism.StaticMeshCacheMiB * 1048576L);
        private static readonly BoundedCache<Cuboidf[]> BoxCache = new(
            boxes => Math.Max(128L, boxes.Length * 24L),
            () => Math.Max(1L, (long)brickbybrickModSystem.Config.Realism.StaticMeshCacheMiB * 1048576L / 8L));
        private static long staticTessellations;
        private static long sidecarBytes;
        private static long exposedQuads;
        private static long mergedQuads;
        private static long cacheRebuilds;
        private static long rejectedBuilds;

        private static readonly IReadOnlyDictionary<FrozenMasonryShape, string> ShapeCodes =
            new Dictionary<FrozenMasonryShape, string>
            {
                [FrozenMasonryShape.Block] = "block",
                [FrozenMasonryShape.SlabDown] = "slab-down",
                [FrozenMasonryShape.SlabUp] = "slab-up",
                [FrozenMasonryShape.SlabNorth] = "slab-north",
                [FrozenMasonryShape.SlabEast] = "slab-east",
                [FrozenMasonryShape.SlabSouth] = "slab-south",
                [FrozenMasonryShape.SlabWest] = "slab-west",
                [FrozenMasonryShape.StairNorth] = "stair-north",
                [FrozenMasonryShape.StairEast] = "stair-east",
                [FrozenMasonryShape.StairSouth] = "stair-south",
                [FrozenMasonryShape.StairWest] = "stair-west",
                [FrozenMasonryShape.StairDownNorth] = "stair-down-north",
                [FrozenMasonryShape.StairDownEast] = "stair-down-east",
                [FrozenMasonryShape.StairDownSouth] = "stair-down-south",
                [FrozenMasonryShape.StairDownWest] = "stair-down-west"
            };

        private static readonly IReadOnlyDictionary<string, FrozenMasonryShape> ShapesByCode =
            CreateReverseShapeMap();

        // Static meshes and collision unions are immutable once generated.
        // Track their actual buffer estimates instead of allowing unique
        // frozen arrangements to grow the renderer's dictionaries forever.
        private sealed class BoundedCache<TValue>
        {
            private sealed class Entry
            {
                internal required string Key { get; init; }
                internal required TValue Value { get; init; }
                internal required long EstimatedBytes { get; init; }
            }

            private readonly object syncRoot = new();
            private readonly Dictionary<string, LinkedListNode<Entry>> entries = new();
            private readonly LinkedList<Entry> usage = new();
            private readonly System.Func<TValue, long> estimate;
            private readonly System.Func<long> budget;
            private long estimatedBytes;
            private long hits;
            private long misses;
            private long evictions;
            private long admissionRejections;

            internal BoundedCache(System.Func<TValue, long> estimate, System.Func<long> budget)
            {
                this.estimate = estimate;
                this.budget = budget;
            }

            internal bool TryGet(string key, out TValue value)
            {
                lock (syncRoot)
                {
                    if (!entries.TryGetValue(key, out LinkedListNode<Entry>? node))
                    {
                        misses++;
                        value = default!;
                        return false;
                    }

                    usage.Remove(node);
                    usage.AddFirst(node);
                    hits++;
                    value = node.Value.Value;
                    return true;
                }
            }

            internal TValue StoreOrGet(string key, TValue value)
            {
                lock (syncRoot)
                {
                    if (entries.TryGetValue(key, out LinkedListNode<Entry>? existing))
                    {
                        usage.Remove(existing);
                        usage.AddFirst(existing);
                        hits++;
                        return existing.Value.Value;
                    }

                    long entryBytes = estimate(value);
                    long byteBudget = Math.Max(1L, budget());
                    if (entryBytes > byteBudget / 2)
                    {
                        admissionRejections++;
                        return value;
                    }

                    Entry entry = new() { Key = key, Value = value, EstimatedBytes = entryBytes };
                    entries.Add(key, usage.AddFirst(entry));
                    estimatedBytes += entryBytes;
                    while (estimatedBytes > byteBudget && usage.Last is LinkedListNode<Entry> oldest)
                    {
                        usage.RemoveLast();
                        entries.Remove(oldest.Value.Key);
                        estimatedBytes -= oldest.Value.EstimatedBytes;
                        evictions++;
                    }

                    return value;
                }
            }

            internal void Clear()
            {
                lock (syncRoot)
                {
                    entries.Clear();
                    usage.Clear();
                    estimatedBytes = 0;
                }
            }

            internal void ResetProfile()
            {
                lock (syncRoot)
                {
                    hits = 0;
                    misses = 0;
                    evictions = 0;
                    admissionRejections = 0;
                }
            }

            internal string GetProfile(string name)
            {
                lock (syncRoot)
                {
                    long byteBudget = Math.Max(1L, budget());
                    return $"{name}: {entries.Count:N0} entries, {estimatedBytes / 1048576d:N1}/{byteBudget / 1048576d:N1} MiB estimated; "
                        + $"{hits:N0} hits, {misses:N0} misses, {evictions:N0} evictions, {admissionRejections:N0} admission rejects";
                }
            }

            internal int Count
            {
                get
                {
                    lock (syncRoot) return entries.Count;
                }
            }
        }

        public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
        {
            return GetGeometryBoxes(blockAccessor, pos);
        }

        public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
        {
            return GetGeometryBoxes(blockAccessor, pos);
        }

        internal static bool TryGetBlockCode(FrozenMasonryShape shape, out AssetLocation code)
        {
            if (ShapeCodes.TryGetValue(shape, out string? shapeCode))
            {
                code = new AssetLocation("brickbybrick", $"staticmasonry-{shapeCode}");
                return true;
            }

            code = null!;
            return false;
        }

        internal bool TryGetShape(out FrozenMasonryShape shape)
        {
            shape = FrozenMasonryShape.Arbitrary;
            return Variant.TryGetValue(ShapeVariantKey, out string? shapeCode)
                && ShapesByCode.TryGetValue(shapeCode, out shape);
        }

        internal static bool TryRestoreEntity(IWorldAccessor world, BlockPos pos, out BlockEntityRealisticMasonry? entity)
        {
            return TryRestoreEntity(world, pos, out entity, out _);
        }

        internal static bool TryRestoreEntity(
            IWorldAccessor world,
            BlockPos pos,
            out BlockEntityRealisticMasonry? entity,
            out string failureReason)
        {
            entity = null;
            failureReason = string.Empty;
            byte[] packedState = Array.Empty<byte>();
            if (world.Side != EnumAppSide.Server) return FailRestore(world, pos, packedState, out failureReason, "restoration was requested outside the server");
            if (!FrozenMasonryChunkStore.TryGet(world.BlockAccessor, pos, out packedState))
            {
                return FailRestore(world, pos, packedState, out failureReason, "the chunk sidecar record is missing");
            }

            try
            {
                MasonryStateCodec.ReadSummary(packedState);
            }
            catch (Exception exception)
            {
                return FailRestore(world, pos, packedState, out failureReason, $"the packed state is invalid: {exception.Message}", exception);
            }

            Block? liveBlock = world.GetBlock(new AssetLocation("brickbybrick:realisticmasonry"));
            if (liveBlock == null || liveBlock.Id == 0) return FailRestore(world, pos, packedState, out failureReason, "the live masonry block is unavailable");

            Block originalBlock = world.BlockAccessor.GetBlock(pos);
            try
            {
                world.BlockAccessor.SetBlock(liveBlock.Id, pos);
                Block replacedBlock = world.BlockAccessor.GetBlock(pos);
                if (replacedBlock.Id != liveBlock.Id)
                {
                    return FailRestore(world, pos, packedState, out failureReason, $"block replacement produced {replacedBlock.Code} instead of {liveBlock.Code}");
                }

                entity = world.BlockAccessor.GetBlockEntity(pos) as BlockEntityRealisticMasonry;
                if (entity == null)
                {
                    world.BlockAccessor.SetBlock(originalBlock.Id, pos);
                    return FailRestore(world, pos, packedState, out failureReason, "the live block was placed but its block entity was not created");
                }

                entity.RestorePackedState(packedState);
                FrozenMasonryChunkStore.Remove(world.BlockAccessor, pos, out _);
                brickbybrickModSystem.BroadcastStaticMasonryState(pos, Array.Empty<byte>(), true);
                world.Api.Logger.Event($"Reopened static masonry at {pos} from {packedState.Length:N0} packed bytes (version {packedState[0]}).");
                return true;
            }
            catch (Exception exception)
            {
                if (world.BlockAccessor.GetBlock(pos).Id == liveBlock.Id)
                {
                    world.BlockAccessor.SetBlock(originalBlock.Id, pos);
                }
                entity = null;
                return FailRestore(world, pos, packedState, out failureReason, $"an exception interrupted restoration: {exception.Message}", exception);
            }
        }

        private static bool FailRestore(
            IWorldAccessor world,
            BlockPos pos,
            byte[] packedState,
            out string failureReason,
            string reason,
            Exception? exception = null)
        {
            failureReason = reason;
            Block currentBlock = world.BlockAccessor.GetBlock(pos);
            int chunkSize = GlobalConstants.ChunkSize;
            string details = $"Could not reopen static masonry at {pos} in chunk {pos.X / chunkSize}, {pos.InternalY / chunkSize}, {pos.Z / chunkSize}: {reason}. "
                + $"Current block: {currentBlock?.Code}; packed bytes: {packedState.Length}; version: {(packedState.Length > 0 ? packedState[0] : -1)}.";
            if (exception == null) world.Api.Logger.Warning(details);
            else world.Api.Logger.Error($"{details} {exception}");
            return false;
        }

        public override void OnJsonTesselation(
            ref MeshData sourceMesh,
            ref int[] lightRgbsByCorner,
            BlockPos pos,
            Block[] chunkExtBlocks,
            int extIndex3d)
        {
            if (!FrozenMasonryChunkStore.TryGet(api.World.BlockAccessor, pos, out byte[] packedState)) return;
            BlockBehaviorShapeTexturesFromAttributes? behavior = GetBehavior<BlockBehaviorShapeTexturesFromAttributes>();
            if (behavior == null) return;

            string cacheKey = Convert.ToBase64String(packedState);
            if (!MeshCache.TryGet(cacheKey, out MeshData? mesh))
            {
                MasonryCellState state = MasonryStateCodec.Decode(packedState);
                MeshData? built = MasonryStaticMeshBuilder.Build(state, behavior, pos, out int rawQuads);
                if (built == null) return;
                mesh = MeshCache.StoreOrGet(cacheKey, built);
                RecordMeshBuild(rawQuads, built.IndicesCount / 6);
            }

            sourceMesh = mesh;
            Interlocked.Increment(ref staticTessellations);
            Interlocked.Add(ref sidecarBytes, packedState.Length);
        }

        public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1)
        {
            if (world.Side != EnumAppSide.Server
                || !FrozenMasonryChunkStore.Remove(world.BlockAccessor, pos, out byte[] packedState))
            {
                base.OnBlockBroken(world, pos, byPlayer, dropQuantityMultiplier);
                return;
            }

            MasonryCellState state = MasonryStateCodec.Decode(packedState);
            foreach (MasonryUnitPlacement unit in state.Units)
            {
                Item? item = world.GetItem(GetDropCode(unit));
                if (item != null) world.SpawnItemEntity(new ItemStack(item), pos.ToVec3d().Add(0.5, 0.5, 0.5));
            }

            world.BlockAccessor.SetBlock(0, pos);
            brickbybrickModSystem.BroadcastStaticMasonryState(pos, Array.Empty<byte>(), true);
        }

        internal static void ResetProfile()
        {
            BoxCache.Clear();
            ResetProfileCounters();
        }

        internal static void ClearCaches()
        {
            MeshCache.Clear();
            BoxCache.Clear();
        }

        internal static void ResetProfileCounters()
        {
            MeshCache.ResetProfile();
            BoxCache.ResetProfile();
            Interlocked.Exchange(ref staticTessellations, 0);
            Interlocked.Exchange(ref sidecarBytes, 0);
            Interlocked.Exchange(ref exposedQuads, 0);
            Interlocked.Exchange(ref mergedQuads, 0);
            Interlocked.Exchange(ref cacheRebuilds, 0);
            Interlocked.Exchange(ref rejectedBuilds, 0);
        }

        internal static void RecordMeshBuild(int rawExposedQuads, int finalMergedQuads)
        {
            Interlocked.Increment(ref cacheRebuilds);
            Interlocked.Add(ref exposedQuads, rawExposedQuads);
            Interlocked.Add(ref mergedQuads, finalMergedQuads);
        }

        internal static void RecordRejectedBuild()
        {
            Interlocked.Increment(ref rejectedBuilds);
        }

        internal static string GetProfile()
        {
            return $"static tessellations: {Interlocked.Read(ref staticTessellations):N0}; "
                + $"tessellated sidecar bytes: {Interlocked.Read(ref sidecarBytes):N0}; "
                + $"exposed quads: {Interlocked.Read(ref exposedQuads):N0}; "
                + $"merged quads: {Interlocked.Read(ref mergedQuads):N0}; "
                + $"cache rebuilds: {Interlocked.Read(ref cacheRebuilds):N0}; "
                + $"rejected optimized builds: {Interlocked.Read(ref rejectedBuilds):N0}; "
                + $"static mesh entries: {MeshCache.Count:N0}; collision entries: {BoxCache.Count:N0}";
        }

        internal static string GetCacheProfile()
        {
            return $"{MeshCache.GetProfile("static mesh cache")}; {BoxCache.GetProfile("static collision cache")}. "
                + "GPU residency is not represented by these CPU-side estimates.";
        }

        private static AssetLocation GetDropCode(MasonryUnitPlacement unit)
        {
            return unit.Kind switch
            {
                MasonryUnitKind.HalfBrick => new AssetLocation("brickbybrick", unit.MaterialCode),
                MasonryUnitKind.RammedEarth or MasonryUnitKind.SmallRammedEarth => new AssetLocation("brickbybrick:testrammedearth"),
                _ => new AssetLocation("game", unit.MaterialCode)
            };
        }

        private Cuboidf[] GetGeometryBoxes(IBlockAccessor blockAccessor, BlockPos pos)
        {
            if (TryGetShape(out FrozenMasonryShape shape)
                && BlockRealisticMasonry.GetFrozenBoxes(shape) is Cuboidf[] frozenBoxes)
            {
                return frozenBoxes;
            }

            if (!FrozenMasonryChunkStore.TryGet(blockAccessor, pos, out byte[] packedState)) return Array.Empty<Cuboidf>();

            string cacheKey = Convert.ToBase64String(packedState);
            if (BoxCache.TryGet(cacheKey, out Cuboidf[]? boxes)) return boxes;

            MasonryCellState state = MasonryStateCodec.Decode(packedState);
            return BoxCache.StoreOrGet(cacheKey, MasonryVoxelGeometry.BuildMergedBoxes(state));
        }

        private static IReadOnlyDictionary<string, FrozenMasonryShape> CreateReverseShapeMap()
        {
            Dictionary<string, FrozenMasonryShape> shapes = new(StringComparer.Ordinal);
            foreach (KeyValuePair<FrozenMasonryShape, string> entry in ShapeCodes)
            {
                shapes[entry.Value] = entry.Key;
            }

            return shapes;
        }
    }
}
