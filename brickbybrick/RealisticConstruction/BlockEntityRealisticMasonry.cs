using AttributeRenderingLibrary;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
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
            if (unit.GetFootprint().Any(position =>
                State.ReservedPositions.Contains(position)
                || State.Units.Any(existing => existing.Occupies(position)))) return false;
            if (unit.Origin.Y == 0) return true;

            MasonryGridPosition[] footprint = unit.GetFootprint().ToArray();
            int supportedCells = footprint.Count(position => State.Units.Any(existing =>
                existing.Supports(new MasonryGridPosition(position.X, position.Y - 1, position.Z))));

            // A whole brick may cantilever by one half-brick cell in any
            // direction. Half bricks and rammed earth require full support.
            return unit.Kind == MasonryUnitKind.WholeBrick
                ? supportedCells >= 1
                : supportedCells == footprint.Length;
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

                Variants variants = new();
                variants.Set("color", GetMaterialColor(unit));
                MeshData mesh = behavior.GetOrCreateMesh(variants, shape, Pos, $"{unit.Kind}-{GetMaterialColor(unit)}").Clone();
                MasonryGridPosition[] footprint = unit.GetFootprint().ToArray();
                int minimumX = footprint.Min(position => position.X);
                int minimumZ = footprint.Min(position => position.Z);
                float width = (footprint.Max(position => position.X) - minimumX + 1) * 0.25f;
                float depth = (footprint.Max(position => position.Z) - minimumZ + 1) * 0.25f;

                float[] matrix = Matrixf.Create()
                    .Translate(
                        minimumX * 0.25f + JointInset,
                        unit.Origin.Y * 0.25f + JointInset,
                        minimumZ * 0.25f + JointInset)
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

            AddSideJointMortar(mesher, behavior);

            return true;
        }

        // Mortar fills only the shared vertical joint between adjacent masonry
        // units. Rammed earth intentionally keeps direct contact with bricks.
        private void AddSideJointMortar(ITerrainMeshPool mesher, BlockBehaviorShapeTexturesFromAttributes behavior)
        {
            HashSet<string> renderedJoints = new();
            (int X, int Z)[] directions = { (1, 0), (-1, 0), (0, 1), (0, -1) };
            CompositeShape mortarShape = new()
            {
                Base = new AssetLocation("brickbybrick:shapes/block/realistic/mortar.json")
            };

            foreach (MasonryUnitPlacement unit in State.Units.Where(candidate => candidate.Kind != MasonryUnitKind.RammedEarth))
            {
                foreach (MasonryGridPosition cell in unit.MortaredPositions)
                {
                    foreach ((int offsetX, int offsetZ) in directions)
                    {
                        MasonryGridPosition neighborPosition = new(cell.X + offsetX, cell.Y, cell.Z + offsetZ);
                        MasonryUnitPlacement? neighbor = State.Units.FirstOrDefault(candidate =>
                            candidate != unit
                            && candidate.Kind != MasonryUnitKind.RammedEarth
                            && candidate.Occupies(neighborPosition));
                        if (neighbor == null) continue;

                        string first = $"{cell.X},{cell.Y},{cell.Z}";
                        string second = $"{neighborPosition.X},{neighborPosition.Y},{neighborPosition.Z}";
                        string jointKey = string.CompareOrdinal(first, second) < 0 ? $"{first}|{second}" : $"{second}|{first}";
                        if (!renderedJoints.Add(jointKey)) continue;

                        MeshData mortarMesh = behavior.GetOrCreateMesh(new Variants(), mortarShape, Pos, "mortar-side").Clone();
                        bool alongX = offsetX != 0;
                        float x = alongX
                            ? (offsetX > 0 ? (cell.X + 1) * 0.25f - JointInset : cell.X * 0.25f - JointInset)
                            : cell.X * 0.25f + JointInset;
                        float z = alongX
                            ? cell.Z * 0.25f + JointInset
                            : (offsetZ > 0 ? (cell.Z + 1) * 0.25f - JointInset : cell.Z * 0.25f - JointInset);
                        float[] jointMatrix = Matrixf.Create()
                            .Translate(x, cell.Y * 0.25f + JointInset, z)
                            .Scale(
                                alongX ? JointInset * 2 : 0.25f - JointInset * 2,
                                0.25f - JointInset * 2,
                                alongX ? 0.25f - JointInset * 2 : JointInset * 2)
                            .Values;
                        mesher.AddMeshData(mortarMesh.MatrixTransform(jointMatrix));
                    }
                }
            }
        }

        public bool CanReserve(IEnumerable<MasonryGridPosition> positions)
        {
            return positions.All(position =>
                !State.ReservedPositions.Contains(position)
                && !State.Units.Any(existing => existing.Occupies(position)));
        }

        public void Reserve(IEnumerable<MasonryGridPosition> positions)
        {
            foreach (MasonryGridPosition position in positions)
            {
                State.ReservedPositions.Add(position);
            }

            MarkDirty(true);
            Api.World.BlockAccessor.MarkBlockDirty(Pos);
        }

        private static string GetMaterialColor(MasonryUnitPlacement unit)
        {
            int separator = unit.MaterialCode.LastIndexOf('-');
            return separator >= 0 ? unit.MaterialCode[(separator + 1)..] : "cream";
        }
    }
}
