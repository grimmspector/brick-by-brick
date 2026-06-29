using AttributeRenderingLibrary;
using Newtonsoft.Json;
using System;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace brickbybrick.RealisticConstruction
{
    // One entity owns every masonry unit inside a block cell. Units are merged
    // into the terrain mesh, so they do not create individual entities.
    public sealed class BlockEntityRealisticMasonry : BlockEntity
    {
        private const float JointInset = 0.0078125f;

        public MasonryCellState State { get; private set; } = new();

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetString("masonryState", JsonConvert.SerializeObject(State));
            tree.SetBool("wet", true);
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
        {
            base.FromTreeAttributes(tree, worldForResolving);
            string json = tree.GetString("masonryState", string.Empty);
            State = string.IsNullOrEmpty(json)
                ? new MasonryCellState()
                : JsonConvert.DeserializeObject<MasonryCellState>(json) ?? new MasonryCellState();
        }

        public bool CanPlace(MasonryUnitPlacement unit)
        {
            if (unit.GetFootprint().Any(position => State.Units.Any(existing => existing.Occupies(position)))) return false;
            if (unit.Origin.Y == 0) return true;

            return unit.GetFootprint().All(position => State.Units.Any(existing =>
                existing.Supports(new MasonryGridPosition(position.X, position.Y - 1, position.Z))));
        }

        public bool TryPlace(MasonryUnitPlacement unit)
        {
            if (!CanPlace(unit)) return false;

            State.Units.Add(unit);
            MarkDirty(true);
            Api.World.BlockAccessor.MarkBlockDirty(Pos);
            return true;
        }

        public int ApplyMortar(MasonryUnitPlacement unit)
        {
            int changed = State.ApplyMortar(unit.GetFootprint());
            if (changed > 0)
            {
                MarkDirty(true);
                Api.World.BlockAccessor.MarkBlockDirty(Pos);
            }

            return changed;
        }

        public MasonryUnitPlacement? FindUnit(MasonryGridPosition position)
        {
            return State.Units.LastOrDefault(unit => unit.Occupies(position));
        }

        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
        {
            BlockBehaviorShapeTexturesFromAttributes? behavior = Block.GetBehavior<BlockBehaviorShapeTexturesFromAttributes>();
            if (behavior == null) return false;

            foreach (MasonryUnitPlacement unit in State.Units)
            {
                CompositeShape shape = new()
                {
                    Base = new AssetLocation(unit.Kind == MasonryUnitKind.RammedEarth
                        ? "brickbybrick:shapes/block/realistic/rammedearth.json"
                        : "brickbybrick:shapes/block/realistic/brick.json")
                };

                MeshData mesh = behavior.GetOrCreateMesh(new Variants(), shape, Pos, unit.Kind.ToString()).Clone();
                float width = unit.Kind == MasonryUnitKind.HalfBrick ? 0.25f : 0.5f;
                float depth = unit.Kind == MasonryUnitKind.RammedEarth ? 0.5f : 0.25f;
                if (unit.Orientation == MasonryOrientation.NorthSouth && unit.Kind != MasonryUnitKind.RammedEarth)
                {
                    (width, depth) = (depth, width);
                }

                float[] matrix = Matrixf.Create()
                    .Translate(
                        unit.Origin.X * 0.25f + JointInset,
                        unit.Origin.Y * 0.25f + JointInset,
                        unit.Origin.Z * 0.25f + JointInset)
                    .Scale(width - JointInset * 2, 0.25f - JointInset * 2, depth - JointInset * 2)
                    .Values;
                mesher.AddMeshData(mesh.MatrixTransform(matrix));

                foreach (MasonryGridPosition mortared in unit.MortaredPositions)
                {
                    CompositeShape mortarShape = new()
                    {
                        Base = new AssetLocation("brickbybrick:shapes/block/realistic/mortar.json")
                    };
                    MeshData mortarMesh = behavior.GetOrCreateMesh(new Variants(), mortarShape, Pos, "mortar").Clone();
                    float[] mortarMatrix = Matrixf.Create()
                        .Translate(mortared.X * 0.25f, (mortared.Y + 1) * 0.25f - 0.015625f, mortared.Z * 0.25f)
                        .Scale(0.25f, 0.03125f, 0.25f)
                        .Values;
                    mesher.AddMeshData(mortarMesh.MatrixTransform(mortarMatrix));
                }
            }

            return true;
        }
    }
}
