using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
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
        private static readonly ConcurrentDictionary<string, MeshData> MeshCache = new();
        private static readonly ConcurrentDictionary<string, Cuboidf[]> BoxCache = new();
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
            if (!MeshCache.TryGetValue(cacheKey, out MeshData? mesh))
            {
                MasonryCellState state = MasonryStateCodec.Decode(packedState);
                MeshData? built = MasonryStaticMeshBuilder.Build(state, behavior, pos, out int rawQuads);
                if (built == null) return;
                mesh = MeshCache.GetOrAdd(cacheKey, built);
                if (ReferenceEquals(mesh, built))
                {
                    RecordMeshBuild(rawQuads, mesh.IndicesCount / 6);
                }
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
                + $"rejected optimized builds: {Interlocked.Read(ref rejectedBuilds):N0}";
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
            return BoxCache.GetOrAdd(cacheKey, _ =>
            {
                MasonryCellState state = MasonryStateCodec.Decode(packedState);
                return MasonryVoxelGeometry.BuildMergedBoxes(state);
            });
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
