using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
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
        private static readonly System.TimeSpan WriteDebounce = System.TimeSpan.FromMilliseconds(100);
        private static readonly ConditionalWeakTable<IWorldChunk, Dictionary<int, byte[]>> Cache = new();
        private static readonly Dictionary<IWorldChunk, long> PendingSaves = new();
        private static readonly object PendingSaveSync = new();
        private static long saveCount;
        private static long deferredWriteCount;
        private static long totalSavedBytes;
        private static long largestSavedBytes;
        private static long removedModdataCount;

        internal static void Set(IBlockAccessor accessor, BlockPos pos, byte[] state)
        {
            IWorldChunk? chunk = accessor.GetChunkAtBlockPos(pos);
            if (chunk == null) return;

            Dictionary<int, byte[]> records = GetRecords(chunk);
            records[GetLocalIndex(pos)] = (byte[])state.Clone();
            ScheduleSave(chunk);
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
            ScheduleSave(chunk);
            return true;
        }

        // Reads use the in-memory record cache, so deferring chunk encoding
        // does not delay static tessellation, restoration, or dropped items.
        internal static void FlushDue()
        {
            List<IWorldChunk> due = new();
            long nowTicks = System.DateTime.UtcNow.Ticks;
            lock (PendingSaveSync)
            {
                foreach (KeyValuePair<IWorldChunk, long> pending in PendingSaves)
                {
                    if (pending.Value <= nowTicks) due.Add(pending.Key);
                }

                foreach (IWorldChunk chunk in due) PendingSaves.Remove(chunk);
            }

            foreach (IWorldChunk chunk in due) Save(chunk, GetRecords(chunk));
        }

        internal static void FlushAll()
        {
            List<IWorldChunk> pending;
            lock (PendingSaveSync)
            {
                pending = new List<IWorldChunk>(PendingSaves.Keys);
                PendingSaves.Clear();
            }

            foreach (IWorldChunk chunk in pending) Save(chunk, GetRecords(chunk));
        }

        private static Dictionary<int, byte[]> GetRecords(IWorldChunk chunk)
        {
            return Cache.GetValue(chunk, loadedChunk => Decode(loadedChunk.GetModdata(ModDataKey)));
        }

        private static void ScheduleSave(IWorldChunk chunk)
        {
            lock (PendingSaveSync)
            {
                PendingSaves[chunk] = System.DateTime.UtcNow.Add(WriteDebounce).Ticks;
            }

            Interlocked.Increment(ref deferredWriteCount);
        }

        private static void Save(IWorldChunk chunk, Dictionary<int, byte[]> records)
        {
            byte[] saved = System.Array.Empty<byte>();
            if (records.Count == 0)
            {
                chunk.RemoveModdata(ModDataKey);
                Interlocked.Increment(ref removedModdataCount);
            }
            else
            {
                saved = Encode(records);
                chunk.SetModdata(ModDataKey, saved);
            }
            chunk.MarkModified();
            Interlocked.Increment(ref saveCount);
            Interlocked.Add(ref totalSavedBytes, saved.Length);
            long previous = Interlocked.Read(ref largestSavedBytes);
            while (saved.Length > previous)
            {
                long observed = Interlocked.CompareExchange(ref largestSavedBytes, saved.Length, previous);
                if (observed == previous) break;
                previous = observed;
            }
        }

        internal static string GetProfile()
        {
            return $"frozen sidecar writes: {Interlocked.Read(ref saveCount):N0}; "
                + $"deferred updates: {Interlocked.Read(ref deferredWriteCount):N0}; pending chunks: {GetPendingSaveCount():N0}; "
                + $"encoded bytes: {Interlocked.Read(ref totalSavedBytes):N0}; "
                + $"largest write: {Interlocked.Read(ref largestSavedBytes):N0} bytes; "
                + $"removed chunk records: {Interlocked.Read(ref removedModdataCount):N0}";
        }

        internal static void ResetProfile()
        {
            Interlocked.Exchange(ref saveCount, 0);
            Interlocked.Exchange(ref deferredWriteCount, 0);
            Interlocked.Exchange(ref totalSavedBytes, 0);
            Interlocked.Exchange(ref largestSavedBytes, 0);
            Interlocked.Exchange(ref removedModdataCount, 0);
        }

        private static int GetPendingSaveCount()
        {
            lock (PendingSaveSync) return PendingSaves.Count;
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
