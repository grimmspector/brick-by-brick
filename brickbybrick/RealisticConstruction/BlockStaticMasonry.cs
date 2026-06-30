using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace brickbybrick.RealisticConstruction
{
    // Represents cured masonry whose reconstruction state is stored on its
    // chunk. These variants describe geometry only and never own an entity.
    public sealed class BlockStaticMasonry : Block
    {
        private const string ShapeVariantKey = "shape";

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
            return GetGeometryBoxes();
        }

        public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
        {
            return GetGeometryBoxes();
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
            entity = null;
            if (world.Side != EnumAppSide.Server
                || !FrozenMasonryChunkStore.TryGet(world.BlockAccessor, pos, out byte[] packedState)) return false;

            Block? liveBlock = world.GetBlock(new AssetLocation("brickbybrick:realisticmasonry"));
            if (liveBlock == null) return false;

            world.BlockAccessor.SetBlock(liveBlock.Id, pos);
            entity = world.BlockAccessor.GetBlockEntity(pos) as BlockEntityRealisticMasonry;
            if (entity == null) return false;

            entity.RestorePackedState(packedState);
            FrozenMasonryChunkStore.Remove(world.BlockAccessor, pos, out _);
            brickbybrickModSystem.BroadcastStaticMasonryState(pos, Array.Empty<byte>(), true);
            return true;
        }

        private Cuboidf[] GetGeometryBoxes()
        {
            return TryGetShape(out FrozenMasonryShape shape)
                ? BlockRealisticMasonry.GetFrozenBoxes(shape) ?? Array.Empty<Cuboidf>()
                : Array.Empty<Cuboidf>();
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
