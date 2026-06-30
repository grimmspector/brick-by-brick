using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace brickbybrick.RealisticConstruction
{
    // Compact persistence avoids repeated JSON strings, GUIDs, and property
    // names for every loaded masonry cell.
    internal static class MasonryStateCodec
    {
        private const byte Version = 2;

        internal static byte[] Encode(MasonryCellState state)
        {
            using MemoryStream stream = new();
            using BinaryWriter writer = new(stream);
            writer.Write(Version);
            writer.Write(state.Frozen);
            writer.Write((byte)state.FrozenShape);
            writer.Write(state.LastModifiedTotalHours);

            string[] palette = state.Units.Select(unit => unit.MaterialCode).Distinct().Take(255).ToArray();
            writer.Write((byte)palette.Length);
            foreach (string material in palette) writer.Write(material);

            writer.Write((ushort)Math.Min(state.Units.Count, ushort.MaxValue));
            foreach (MasonryUnitPlacement unit in state.Units.Take(ushort.MaxValue))
            {
                writer.Write((byte)unit.Kind);
                writer.Write((byte)unit.Orientation);
                writer.Write((short)unit.Origin.X);
                writer.Write((short)unit.Origin.Y);
                writer.Write((short)unit.Origin.Z);
                writer.Write((byte)Math.Max(0, Array.IndexOf(palette, unit.MaterialCode)));
                WritePositions(writer, unit.MortaredPositions);
            }

            WritePositions(writer, state.ReservedPositions);
            writer.Write((ushort)Math.Min(state.MortaredSideJoints.Count, ushort.MaxValue));
            foreach (string joint in state.MortaredSideJoints.Take(ushort.MaxValue)) writer.Write(joint);
            return stream.ToArray();
        }

        internal static MasonryCellState Decode(byte[] data)
        {
            using MemoryStream stream = new(data, false);
            using BinaryReader reader = new(stream);
            byte version = reader.ReadByte();
            if (version is < 1 or > Version) throw new InvalidDataException("Unsupported masonry state version.");

            MasonryCellState state = new()
            {
                Frozen = reader.ReadBoolean(),
                FrozenShape = (FrozenMasonryShape)reader.ReadByte(),
                LastModifiedTotalHours = reader.ReadDouble()
            };
            string[] palette = new string[reader.ReadByte()];
            for (int index = 0; index < palette.Length; index++) palette[index] = reader.ReadString();

            int unitCount = reader.ReadUInt16();
            for (int index = 0; index < unitCount; index++)
            {
                MasonryUnitPlacement unit = new()
                {
                    Id = index.ToString(),
                    Kind = (MasonryUnitKind)reader.ReadByte(),
                    Orientation = (MasonryOrientation)reader.ReadByte(),
                    Origin = new MasonryGridPosition(reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16())
                };
                int paletteIndex = reader.ReadByte();
                unit.MaterialCode = paletteIndex < palette.Length ? palette[paletteIndex] : "burnedbrick-cream";
                unit.MortaredPositions = ReadPositions(reader);
                state.Units.Add(unit);
            }

            state.ReservedPositions = ReadPositions(reader);
            if (version >= 2)
            {
                int sideJointCount = reader.ReadUInt16();
                for (int index = 0; index < sideJointCount; index++) state.MortaredSideJoints.Add(reader.ReadString());
            }
            return state;
        }

        private static void WritePositions(BinaryWriter writer, IEnumerable<MasonryGridPosition> positions)
        {
            MasonryGridPosition[] values = positions.Take(ushort.MaxValue).ToArray();
            writer.Write((ushort)values.Length);
            foreach (MasonryGridPosition position in values)
            {
                writer.Write((short)position.X);
                writer.Write((short)position.Y);
                writer.Write((short)position.Z);
            }
        }

        private static HashSet<MasonryGridPosition> ReadPositions(BinaryReader reader)
        {
            HashSet<MasonryGridPosition> positions = new();
            int count = reader.ReadUInt16();
            for (int index = 0; index < count; index++)
            {
                positions.Add(new MasonryGridPosition(reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16()));
            }

            return positions;
        }
    }
}
