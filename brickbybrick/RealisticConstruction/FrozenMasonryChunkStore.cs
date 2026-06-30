using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace brickbybrick.RealisticConstruction
{
    // Persists entityless masonry reconstruction records with their chunk.
    // Values are the same compact payload used by live masonry entities.
    internal static class FrozenMasonryChunkStore
    {
        private const string ModDataKey = "brickbybrick:frozenmasonry:v1";
        private static readonly ConditionalWeakTable<IWorldChunk, Dictionary<int, byte[]>> Cache = new();

        internal static void Set(IBlockAccessor accessor, BlockPos pos, byte[] state)
        {
            IWorldChunk? chunk = accessor.GetChunkAtBlockPos(pos);
            if (chunk == null) return;

            Dictionary<int, byte[]> records = GetRecords(chunk);
            records[GetLocalIndex(pos)] = (byte[])state.Clone();
            Save(chunk, records);
        }

        internal static bool TryGet(IBlockAccessor accessor, BlockPos pos, out byte[] state)
        {
            state = System.Array.Empty<byte>();
            IWorldChunk? chunk = accessor.GetChunkAtBlockPos(pos);
            if (chunk == null || !GetRecords(chunk).TryGetValue(GetLocalIndex(pos), out byte[]? stored)) return false;

            state = (byte[])stored.Clone();
            return true;
        }

        internal static bool Remove(IBlockAccessor accessor, BlockPos pos, out byte[] state)
        {
            state = System.Array.Empty<byte>();
            IWorldChunk? chunk = accessor.GetChunkAtBlockPos(pos);
            if (chunk == null) return false;

            Dictionary<int, byte[]> records = GetRecords(chunk);
            if (!records.Remove(GetLocalIndex(pos), out byte[]? stored)) return false;
            state = (byte[])stored.Clone();
            Save(chunk, records);
            return true;
        }

        private static Dictionary<int, byte[]> GetRecords(IWorldChunk chunk)
        {
            return Cache.GetValue(chunk, loadedChunk => Decode(loadedChunk.GetModdata(ModDataKey)));
        }

        private static void Save(IWorldChunk chunk, Dictionary<int, byte[]> records)
        {
            if (records.Count == 0) chunk.RemoveModdata(ModDataKey);
            else chunk.SetModdata(ModDataKey, Encode(records));
            chunk.MarkModified();
        }

        private static int GetLocalIndex(BlockPos pos)
        {
            int size = GlobalConstants.ChunkSize;
            int x = GameMath.Mod(pos.X, size);
            int y = GameMath.Mod(pos.InternalY, size);
            int z = GameMath.Mod(pos.Z, size);
            return (y * size + z) * size + x;
        }

        private static byte[] Encode(Dictionary<int, byte[]> records)
        {
            using MemoryStream stream = new();
            using BinaryWriter writer = new(stream);
            writer.Write(records.Count);
            foreach (KeyValuePair<int, byte[]> record in records)
            {
                writer.Write(record.Key);
                writer.Write(record.Value.Length);
                writer.Write(record.Value);
            }

            return stream.ToArray();
        }

        private static Dictionary<int, byte[]> Decode(byte[]? data)
        {
            Dictionary<int, byte[]> records = new();
            if (data == null || data.Length == 0) return records;

            try
            {
                using MemoryStream stream = new(data, false);
                using BinaryReader reader = new(stream);
                int count = reader.ReadInt32();
                if (count < 0 || count > GlobalConstants.ChunkSize * GlobalConstants.ChunkSize * GlobalConstants.ChunkSize)
                {
                    return records;
                }

                for (int index = 0; index < count; index++)
                {
                    int localIndex = reader.ReadInt32();
                    int length = reader.ReadInt32();
                    if (localIndex < 0 || length < 0 || length > stream.Length - stream.Position) return new();
                    records[localIndex] = reader.ReadBytes(length);
                }
            }
            catch (EndOfStreamException)
            {
                return new();
            }

            return records;
        }
    }
}
