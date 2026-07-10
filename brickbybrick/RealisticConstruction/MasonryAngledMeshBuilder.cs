using AttributeRenderingLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace brickbybrick.RealisticConstruction
{
    // Builds exact straight and diagonal regions independently, then appends
    // their small intersection fills. The final result remains one terrain
    // mesh and one frozen-cache entry regardless of its internal regions.
    internal static class MasonryAngledMeshBuilder
    {
        private const float JointInset = 0.0078125f;

        internal static bool TryBuildValidated(
            MasonryCellState state,
            BlockBehaviorShapeTexturesFromAttributes behavior,
            BlockPos pos,
            out MeshData? mesh,
            out int componentCount,
            out string rejectionReason)
        {
            mesh = null;
            componentCount = 0;
            rejectionReason = string.Empty;
            try
            {
                if (!state.Units.Any(unit => unit.IsDiagonal))
                {
                    rejectionReason = "cell has no diagonal region";
                    return false;
                }
                if (state.MortaredSideJoints.Count > 0)
                {
                    rejectionReason = "diagonal side-joint optimization is not implemented";
                    return false;
                }

                HashSet<(int X, int Y, int Z)> occupied = state.Units.SelectMany(MasonryVoxelGeometry.GetVoxels).ToHashSet();
                MeshData? straight = BuildUnits(state.Units.Where(unit => !unit.IsDiagonal), occupied, behavior, pos, ref componentCount);
                MeshData? diagonal = BuildUnits(state.Units.Where(unit => unit.IsDiagonal), occupied, behavior, pos, ref componentCount);
                MeshData? earth = BuildFills(state.EarthGapVoxels, true, behavior, pos, ref componentCount);
                MeshData? mortar = BuildFills(state.MortarGapVoxels, false, behavior, pos, ref componentCount);
                Append(ref mesh, straight);
                Append(ref mesh, diagonal);
                Append(ref mesh, earth);
                Append(ref mesh, mortar);
                rejectionReason = Validate(mesh);
                return rejectionReason.Length == 0;
            }
            catch (Exception exception)
            {
                mesh = null;
                rejectionReason = exception.Message;
                return false;
            }
        }

        private static MeshData? BuildUnits(
            IEnumerable<MasonryUnitPlacement> units,
            HashSet<(int X, int Y, int Z)> occupied,
            BlockBehaviorShapeTexturesFromAttributes behavior,
            BlockPos pos,
            ref int componentCount)
        {
            MeshData? region = null;
            foreach (MasonryUnitPlacement unit in units)
            {
                if (IsFullyEnclosed(unit, occupied)) continue;
                CompositeShape shape = new()
                {
                    Base = new AssetLocation(unit.Kind is MasonryUnitKind.RammedEarth or MasonryUnitKind.SmallRammedEarth
                        ? "brickbybrick:shapes/block/realistic/rammedearth.json"
                        : "brickbybrick:shapes/block/realistic/brick.json")
                };
                Variants variants = new();
                variants.Set("color", GetMaterialColor(unit));

                string cacheKey = $"angled-unit:{unit.Kind}:{unit.VisualShape}:{GetMaterialColor(unit)}:{unit.Origin.X}:{unit.Origin.Y}:{unit.Origin.Z}:{unit.OffsetX:0.###}:{unit.OffsetZ:0.###}:{unit.Orientation}";
                MeshData transformed = MasonryTransformedMeshCache.GetOrCreate(cacheKey, () =>
                {
                    MeshData source = behavior.GetOrCreateMesh(variants, shape, pos, $"angled-{unit.Kind}-{GetMaterialColor(unit)}").Clone();
                    if (unit.VisualShape == MasonryVisualShape.TriangleWedge) MasonryVoxelGeometry.DeformTriangle(source);
                    return MasonryVoxelGeometry.TransformUnitMesh(source, unit, JointInset);
                });
                Append(ref region, transformed);
                componentCount++;

                if (unit.Kind is MasonryUnitKind.RammedEarth or MasonryUnitKind.SmallRammedEarth || unit.MortaredPositions.Count == 0) continue;
                CompositeShape mortarShape = new() { Base = new AssetLocation("brickbybrick:shapes/block/realistic/mortar.json") };
                foreach (MasonryGridPosition mortared in unit.MortaredPositions)
                {
                    MeshData top = behavior.GetOrCreateMesh(new Variants(), mortarShape, pos, "angled-top-mortar").Clone()
                        .MatrixTransform(Matrixf.Create()
                            .Translate(mortared.X * 0.25f, (mortared.Y + 1) * 0.25f - 0.015625f, mortared.Z * 0.25f)
                            .Scale(0.25f, 0.03125f, 0.25f)
                            .Values);
                    Append(ref region, top);
                    componentCount++;
                }
            }

            return region;
        }

        private static bool IsFullyEnclosed(MasonryUnitPlacement unit, HashSet<(int X, int Y, int Z)> occupied)
        {
            (int X, int Y, int Z)[] directions =
            {
                (1, 0, 0), (-1, 0, 0), (0, 1, 0),
                (0, -1, 0), (0, 0, 1), (0, 0, -1)
            };
            HashSet<(int X, int Y, int Z)> own = MasonryVoxelGeometry.GetVoxels(unit).ToHashSet();
            if (own.Count == 0) return false;
            foreach ((int x, int y, int z) in own)
            foreach ((int offsetX, int offsetY, int offsetZ) in directions)
            {
                (int X, int Y, int Z) neighbor = (x + offsetX, y + offsetY, z + offsetZ);
                if (!own.Contains(neighbor) && !occupied.Contains(neighbor)) return false;
            }
            return true;
        }

        private static MeshData? BuildFills(
            IEnumerable<MasonryGridPosition> voxels,
            bool earth,
            BlockBehaviorShapeTexturesFromAttributes behavior,
            BlockPos pos,
            ref int componentCount)
        {
            Cuboidf[] boxes = MasonryVoxelGeometry.BuildMergedBoxes(voxels);
            if (boxes.Length == 0) return null;
            CompositeShape shape = new()
            {
                Base = new AssetLocation(earth
                    ? "brickbybrick:shapes/block/realistic/rammedearth.json"
                    : "brickbybrick:shapes/block/realistic/mortar.json")
            };
            MeshData? region = null;
            foreach (Cuboidf box in boxes)
            {
                MeshData fill = behavior.GetOrCreateMesh(new Variants(), shape, pos, earth ? "angled-earth-fill" : "angled-mortar-fill").Clone()
                    .MatrixTransform(Matrixf.Create()
                        .Translate(box.X1, box.Y1, box.Z1)
                        .Scale(box.XSize, box.YSize, box.ZSize)
                        .Values);
                Append(ref region, fill);
                componentCount++;
            }
            return region;
        }

        private static void Append(ref MeshData? target, MeshData? addition)
        {
            if (addition == null) return;
            if (target == null) target = addition;
            else target.AddMeshData(addition);
        }

        private static string Validate(MeshData? mesh)
        {
            if (mesh == null || mesh.VerticesCount <= 0 || mesh.IndicesCount <= 0) return "builder returned no geometry";
            if (mesh.VerticesCount % 4 != 0 || mesh.IndicesCount % 6 != 0) return "mesh has incomplete quads";
            int quads = mesh.IndicesCount / 6;
            if (mesh.XyzFaces == null || mesh.XyzFacesCount != quads) return "mesh face-lighting metadata is incomplete";
            if (mesh.TextureIndices == null || mesh.TextureIndicesCount != quads) return "mesh texture metadata is incomplete";
            return string.Empty;
        }

        private static string GetMaterialColor(MasonryUnitPlacement unit)
        {
            int separator = unit.MaterialCode.LastIndexOf('-');
            return separator >= 0 ? unit.MaterialCode[(separator + 1)..] : "cream";
        }
    }
}
