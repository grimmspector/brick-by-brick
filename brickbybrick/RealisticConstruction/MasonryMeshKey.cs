using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace brickbybrick.RealisticConstruction
{
    // Produces a position- and timestamp-independent identity for rendered
    // masonry. Changing mesh semantics requires incrementing FormatVersion.
    internal static class MasonryMeshKey
    {
        private const byte FormatVersion = 3;

        internal static string Create(MasonryCellState state, bool optimized)
        {
            using MemoryStream stream = new();
            using BinaryWriter writer = new(stream);
            writer.Write(FormatVersion);
            writer.Write(optimized);
            writer.Write((byte)state.FrozenShape);
            writer.Write(state.MortarMaterialCode);

            foreach (MasonryUnitPlacement unit in state.Units
                .OrderBy(unit => unit.Origin.Y)
                .ThenBy(unit => unit.Origin.X)
                .ThenBy(unit => unit.Origin.Z)
                .ThenBy(unit => unit.Kind)
                .ThenBy(unit => unit.Orientation)
                .ThenBy(unit => unit.MaterialCode, StringComparer.Ordinal))
            {
                writer.Write((byte)unit.Kind);
                writer.Write((byte)unit.Orientation);
                writer.Write(unit.Origin.X);
                writer.Write(unit.Origin.Y);
                writer.Write(unit.Origin.Z);
                writer.Write(unit.MaterialCode);
                foreach (MasonryGridPosition position in unit.MortaredPositions
                    .OrderBy(position => position.Y)
                    .ThenBy(position => position.X)
                    .ThenBy(position => position.Z))
                {
                    writer.Write(position.X);
                    writer.Write(position.Y);
                    writer.Write(position.Z);
                }
                writer.Write(int.MinValue);
            }

            foreach (string joint in state.MortaredSideJoints.OrderBy(value => value, StringComparer.Ordinal)) writer.Write(joint);
            return Convert.ToBase64String(SHA256.HashData(stream.ToArray()));
        }
    }
}
