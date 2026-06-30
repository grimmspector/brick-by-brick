using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace brickbybrick.RealisticConstruction
{
    // One background scheduler waits for cell deadlines without touching the
    // world API. Due work is committed by the server tick on the main thread.
    internal static class MasonryFreezeScheduler
    {
        private sealed record ScheduledFreeze(WeakReference<BlockEntityRealisticMasonry> Entity, int Revision);

        private static readonly object SyncRoot = new();
        private static readonly PriorityQueue<ScheduledFreeze, long> Pending = new();
        private static readonly ConcurrentQueue<ScheduledFreeze> Ready = new();
        private static readonly AutoResetEvent Changed = new(false);
        private static readonly Thread Worker = StartWorker();

        internal static void Schedule(BlockEntityRealisticMasonry entity, int revision, TimeSpan delay)
        {
            long dueTicks = DateTime.UtcNow.Add(delay < TimeSpan.Zero ? TimeSpan.Zero : delay).Ticks;
            lock (SyncRoot)
            {
                Pending.Enqueue(new ScheduledFreeze(new WeakReference<BlockEntityRealisticMasonry>(entity), revision), dueTicks);
            }

            Changed.Set();
        }

        internal static void DrainReady()
        {
            // Bound commits per tick so mass chunk loads cannot create one
            // large block-update and remeshing spike on the server thread.
            const int maximumCommitsPerTick = 128;
            for (int committed = 0; committed < maximumCommitsPerTick && Ready.TryDequeue(out ScheduledFreeze? scheduled); committed++)
            {
                if (scheduled.Entity.TryGetTarget(out BlockEntityRealisticMasonry? entity))
                {
                    entity.TryApplyScheduledFreeze(scheduled.Revision);
                }
            }
        }

        private static Thread StartWorker()
        {
            Thread thread = new(ProcessQueue)
            {
                IsBackground = true,
                Name = "BrickByBrick masonry freeze scheduler"
            };
            thread.Start();
            return thread;
        }

        private static void ProcessQueue()
        {
            while (true)
            {
                int waitMilliseconds = Timeout.Infinite;
                lock (SyncRoot)
                {
                    long nowTicks = DateTime.UtcNow.Ticks;
                    while (Pending.TryPeek(out ScheduledFreeze? scheduled, out long dueTicks) && dueTicks <= nowTicks)
                    {
                        Pending.Dequeue();
                        Ready.Enqueue(scheduled);
                    }

                    if (Pending.TryPeek(out _, out long nextTicks))
                    {
                        waitMilliseconds = (int)Math.Clamp((nextTicks - nowTicks) / TimeSpan.TicksPerMillisecond, 1, int.MaxValue);
                    }
                }

                Changed.WaitOne(waitMilliseconds);
            }
        }
    }
}
