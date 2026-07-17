using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace brickbybrick.RealisticConstruction
{
    // One background scheduler waits for cell deadlines without touching the
    // world API. Due work is committed by the server tick on the main thread.
    internal static class MasonryFreezeScheduler
    {
        private sealed class ScheduledFreeze
        {
            internal ScheduledFreeze(BlockEntityRealisticMasonry entity, int revision, long dueTicks, long sequence)
            {
                Entity = new WeakReference<BlockEntityRealisticMasonry>(entity);
                Revision = revision;
                DueTicks = dueTicks;
                Sequence = sequence;
            }

            internal WeakReference<BlockEntityRealisticMasonry> Entity { get; }

            internal int Revision { get; set; }

            internal long DueTicks { get; set; }

            internal long Sequence { get; set; }
        }

        private static readonly object SyncRoot = new();
        private static readonly SortedSet<ScheduledFreeze> Pending = new(Comparer<ScheduledFreeze>.Create((left, right) =>
        {
            int dueComparison = left.DueTicks.CompareTo(right.DueTicks);
            return dueComparison != 0 ? dueComparison : left.Sequence.CompareTo(right.Sequence);
        }));
        private static readonly ConcurrentQueue<ScheduledFreeze> Ready = new();
        private static readonly ConditionalWeakTable<BlockEntityRealisticMasonry, ScheduledFreeze> ScheduledByEntity = new();
        private static readonly AutoResetEvent Changed = new(false);
        private static readonly Thread Worker = StartWorker();
        private static long scheduled;
        private static long coalesced;
        private static long expired;
        private static long readyEnqueued;
        private static long drainAttempts;
        private static long peakPending;
        private static long nextSequence;

        internal static void Schedule(BlockEntityRealisticMasonry entity, int revision, TimeSpan delay)
        {
            long dueTicks = DateTime.UtcNow.Add(delay < TimeSpan.Zero ? TimeSpan.Zero : delay).Ticks;
            lock (SyncRoot)
            {
                if (ScheduledByEntity.TryGetValue(entity, out ScheduledFreeze? existing))
                {
                    Pending.Remove(existing);
                    existing.Revision = revision;
                    existing.DueTicks = dueTicks;
                    existing.Sequence = ++nextSequence;
                    Pending.Add(existing);
                    Interlocked.Increment(ref coalesced);
                }
                else
                {
                    ScheduledFreeze scheduledFreeze = new(entity, revision, dueTicks, ++nextSequence);
                    ScheduledByEntity.Add(entity, scheduledFreeze);
                    Pending.Add(scheduledFreeze);
                }
                peakPending = Math.Max(peakPending, Pending.Count);
            }

            Interlocked.Increment(ref scheduled);
            Changed.Set();
        }

        internal static void DrainReady()
        {
            // Bound commits per tick so mass chunk loads cannot create one
            // large block-update and remeshing spike on the server thread.
            const int maximumCommitsPerTick = 128;
            for (int committed = 0; committed < maximumCommitsPerTick && Ready.TryDequeue(out ScheduledFreeze? scheduled); committed++)
            {
                Interlocked.Increment(ref drainAttempts);
                if (scheduled.Entity.TryGetTarget(out BlockEntityRealisticMasonry? entity))
                {
                    entity.TryApplyScheduledFreeze(scheduled.Revision);
                }
            }
        }

        internal static string GetProfile()
        {
            int pending;
            lock (SyncRoot) pending = Pending.Count;
            return $"freeze scheduler: pending {pending:N0}; ready {Ready.Count:N0}; "
                + $"scheduled {Interlocked.Read(ref scheduled):N0}; coalesced {Interlocked.Read(ref coalesced):N0}; "
                + $"expired {Interlocked.Read(ref expired):N0}; ready enqueued {Interlocked.Read(ref readyEnqueued):N0}; "
                + $"drain attempts {Interlocked.Read(ref drainAttempts):N0}; peak pending {Interlocked.Read(ref peakPending):N0}";
        }

        internal static void ResetProfile()
        {
            Interlocked.Exchange(ref scheduled, 0);
            Interlocked.Exchange(ref coalesced, 0);
            Interlocked.Exchange(ref expired, 0);
            Interlocked.Exchange(ref readyEnqueued, 0);
            Interlocked.Exchange(ref drainAttempts, 0);
            Interlocked.Exchange(ref peakPending, 0);
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
                    while (Pending.Min is ScheduledFreeze scheduled && scheduled.DueTicks <= nowTicks)
                    {
                        Pending.Remove(scheduled);
                        if (scheduled.Entity.TryGetTarget(out BlockEntityRealisticMasonry? entity))
                        {
                            ScheduledByEntity.Remove(entity);
                            Ready.Enqueue(scheduled);
                            Interlocked.Increment(ref readyEnqueued);
                        }
                        else
                        {
                            Interlocked.Increment(ref expired);
                        }
                    }

                    if (Pending.Min is ScheduledFreeze next)
                    {
                        waitMilliseconds = (int)Math.Clamp((next.DueTicks - nowTicks) / TimeSpan.TicksPerMillisecond, 1, int.MaxValue);
                    }
                }

                Changed.WaitOne(waitMilliseconds);
            }
        }
    }
}
