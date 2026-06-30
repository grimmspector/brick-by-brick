using AttributeRenderingLibrary;
using System;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace brickbybrick.RealisticConstruction
{
    // Builds a single terrain mesh from packed masonry state. ARL still owns
    // texture resolution, while the sidecar owns the material arrangement.
    internal static class MasonryStaticMeshBuilder
    {
        private const float JointInset = 0.0078125f;

        internal static MeshData? Build(
            MasonryCellState state,
            BlockBehaviorShapeTexturesFromAttributes behavior,
            BlockPos pos,
            out int exposedQuads)
        {
            MeshData? combined = null;
            int rawQuads = 0;

            void Add(MeshData mesh)
            {
                rawQuads += mesh.IndicesCount / 6;
                if (combined == null) combined = mesh.Clone();
                else combined.AddMeshData(mesh);
            }

            foreach (MasonryUnitPlacement unit in state.Units)
            {
                CompositeShape shape = new()
                {
                    Base = new AssetLocation(unit.Kind == MasonryUnitKind.RammedEarth
                        ? "brickbybrick:shapes/block/realistic/rammedearth.json"
                        : "brickbybrick:shapes/block/realistic/brick.json")
                };
                Variants variants = new();
                variants.Set("color", GetMaterialColor(unit));
                MasonryGridPosition[] footprint = unit.GetFootprint().ToArray();
                int minimumX = footprint.Min(position => position.X);
                int minimumZ = footprint.Min(position => position.Z);
                float width = (footprint.Max(position => position.X) - minimumX + 1) * 0.25f;
                float depth = (footprint.Max(position => position.Z) - minimumZ + 1) * 0.25f;
                MeshData unitMesh = behavior.GetOrCreateMesh(variants, shape, pos, $"static-{unit.Kind}-{GetMaterialColor(unit)}").Clone();
                Add(unitMesh.MatrixTransform(Matrixf.Create()
                    .Translate(minimumX * 0.25f + JointInset, unit.Origin.Y * 0.25f + JointInset, minimumZ * 0.25f + JointInset)
                    .Scale(width - JointInset * 2, 0.25f - JointInset * 2, depth - JointInset * 2)
                    .Values));

                foreach (MasonryGridPosition mortared in unit.MortaredPositions)
                {
                    CompositeShape mortarShape = new()
                    {
                        Base = new AssetLocation("brickbybrick:shapes/block/realistic/mortar.json")
                    };
                    MeshData mortarMesh = behavior.GetOrCreateMesh(new Variants(), mortarShape, pos, "static-mortar").Clone();
                    Add(mortarMesh.MatrixTransform(Matrixf.Create()
                        .Translate(mortared.X * 0.25f, (mortared.Y + 1) * 0.25f - 0.015625f, mortared.Z * 0.25f)
                        .Scale(0.25f, 0.03125f, 0.25f)
                        .Values));
                }
            }

            exposedQuads = rawQuads;
            return combined;
        }

        private static string GetMaterialColor(MasonryUnitPlacement unit)
        {
            int separator = unit.MaterialCode.LastIndexOf('-');
            return separator >= 0 ? unit.MaterialCode[(separator + 1)..] : "cream";
        }
    }
}
