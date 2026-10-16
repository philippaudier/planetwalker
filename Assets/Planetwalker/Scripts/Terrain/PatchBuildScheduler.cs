using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Planetwalker.Terrain
{
    /// <summary>
    /// Bounded patch construction pipeline (V0.2.0B). The main thread states, each frame, which patches it needs
    /// (in priority order); the scheduler keeps at most <see cref="MaxPending"/> of them queued or building, drops
    /// queued requests that are no longer needed (counted as cancelled), builds geometry on worker threads (or on
    /// the calling thread within a time budget when <see cref="WorkerCount"/> is 0) and hands finished geometry
    /// back to the main thread, which alone touches Unity objects. Every job carries its
    /// <see cref="AdaptiveSurfaceSnapshot"/>: a result of an older generation, or of a cancelled job, is
    /// discarded and never reaches a mesh. Workers read only the immutable snapshot and write only pooled CPU
    /// buffers.
    /// </summary>
    public sealed class PatchBuildScheduler : IDisposable
    {
        sealed class Job
        {
            public PlanetPatchId Id;
            public AdaptiveSurfaceSnapshot Snapshot;
            public float Priority;
            public volatile bool Cancelled;
        }

        struct Finished
        {
            public Job Job;
            public PatchGeometryData Data;
            public Exception Error;
        }

        readonly List<Job> queued = new List<Job>();
        readonly Dictionary<PlanetPatchId, Job> inFlight = new Dictionary<PlanetPatchId, Job>();
        readonly HashSet<PlanetPatchId> wanted = new HashSet<PlanetPatchId>();
        readonly ConcurrentQueue<Finished> finished = new ConcurrentQueue<Finished>();
        readonly ConcurrentBag<PatchGeometryData> dataPool = new ConcurrentBag<PatchGeometryData>();
        readonly ConcurrentBag<AdaptivePatchGeometryBuilder> builderPool = new ConcurrentBag<AdaptivePatchGeometryBuilder>();
        readonly WaitCallback runnerLoop;
        // Guards queued and inFlight: worker runners take the next job themselves (no wait for the next frame).
        readonly object gate = new object();
        int runners;
        readonly Stack<Job> jobPool = new Stack<Job>();
        volatile bool disposed;

        public PatchBuildScheduler(int workerCount, int maxPending)
        {
            WorkerCount = Math.Max(0, workerCount);
            MaxPending = Math.Max(1, maxPending);
            runnerLoop = RunnerLoop;
        }

        /// <summary>Concurrent worker builds (0 = synchronous, on the calling thread).</summary>
        public int WorkerCount { get; set; }

        /// <summary>Most requests queued or building at once.</summary>
        public int MaxPending { get; set; }

        public int Queued { get { lock (gate) { return queued.Count; } } }
        public int InFlight { get { lock (gate) { return inFlight.Count; } } }
        public int Pending { get { lock (gate) { return queued.Count + inFlight.Count; } } }

        public int Completed { get; private set; }
        /// <summary>Queued requests dropped because no longer needed (camera moved, world changed).</summary>
        public int Cancelled { get; private set; }
        /// <summary>Finished builds thrown away: cancelled while building or of an older generation.</summary>
        public int Discarded { get; private set; }
        public int Failed { get; private set; }
        public Exception LastError { get; private set; }

        /// <summary>CPU build time of the completed builds (worker or synchronous), milliseconds.</summary>
        public double BuildMilliseconds { get; private set; }

        /// <summary>
        /// The patches needed now, best first. Starts what fits; cancels queued requests that left the list.
        /// <paramref name="isAvailable"/> tells which patches need no build (resident or already finished).
        /// </summary>
        public void Request(AdaptiveSurfaceSnapshot snapshot, List<PatchRequest> needed, Func<PlanetPatchId, bool> isAvailable)
        {
            CollectFinished();
            lock (gate)
            {
                RequestLocked(snapshot, needed, isAvailable);
            }
            Dispatch();
        }

        void RequestLocked(AdaptiveSurfaceSnapshot snapshot, List<PatchRequest> needed, Func<PlanetPatchId, bool> isAvailable)
        {
            wanted.Clear();
            // Builds already running occupy their slots until they finish (even if no longer wanted): new requests
            // only get what remains, so queued + in flight never exceeds MaxPending.
            int budget = MaxPending - inFlight.Count;
            foreach (PatchRequest request in needed)
            {
                if (isAvailable(request.Id) || readyIds.Contains(request.Id))
                {
                    continue;
                }
                if (inFlight.TryGetValue(request.Id, out Job running) && running.Snapshot == snapshot)
                {
                    wanted.Add(request.Id); // keeps its slot
                    continue;
                }
                if (budget <= 0)
                {
                    continue;
                }
                wanted.Add(request.Id);
                budget--;
            }
            // Drop queued requests that are no longer wanted (or belong to another generation).
            for (int i = queued.Count - 1; i >= 0; i--)
            {
                Job job = queued[i];
                if (job.Snapshot != snapshot || !wanted.Contains(job.Id))
                {
                    queued.RemoveAt(i);
                    Cancelled++;
                    Release(job);
                }
            }
            foreach (Job job in inFlight.Values)
            {
                // Not wanted any more: its result will be discarded (wanted again before it finished: kept).
                job.Cancelled = job.Snapshot != snapshot || !wanted.Contains(job.Id);
            }
            // Queue the new ones (priority order is the order of the needed list).
            foreach (PatchRequest request in needed)
            {
                if (!wanted.Contains(request.Id) || inFlight.ContainsKey(request.Id) || IsQueued(request.Id))
                {
                    continue;
                }
                Job job = jobPool.Count > 0 ? jobPool.Pop() : new Job();
                job.Id = request.Id;
                job.Snapshot = snapshot;
                job.Priority = request.Priority;
                job.Cancelled = false;
                queued.Add(job);
            }
            queued.Sort(ByPriority);
        }

        static readonly Comparison<Job> ByPriority = (a, b) => a.Priority != b.Priority ? b.Priority.CompareTo(a.Priority) : a.Id.Key.CompareTo(b.Id.Key);

        bool IsQueued(PlanetPatchId id)
        {
            foreach (Job job in queued)
            {
                if (job.Id == id)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Starts worker runners (at most <see cref="WorkerCount"/>), each building queued jobs until none is left.</summary>
        void Dispatch()
        {
            if (WorkerCount == 0 || disposed)
            {
                return;
            }
            lock (gate)
            {
                while (runners < Math.Min(WorkerCount, MaxPending) && runners < queued.Count)
                {
                    runners++;
                    ThreadPool.QueueUserWorkItem(runnerLoop);
                }
            }
        }

        void RunnerLoop(object _)
        {
            while (true)
            {
                Job job;
                lock (gate)
                {
                    if (disposed || queued.Count == 0)
                    {
                        runners--;
                        return;
                    }
                    job = queued[0];
                    queued.RemoveAt(0);
                    inFlight[job.Id] = job;
                }
                RunJob(job);
            }
        }

        /// <summary>Synchronous mode: builds queued requests on the calling thread for about this long (at least one).</summary>
        public void RunSynchronous(double milliseconds)
        {
            if (WorkerCount != 0)
            {
                return;
            }
            long start = Stopwatch.GetTimestamp();
            double limit = milliseconds * Stopwatch.Frequency / 1000.0;
            while (true)
            {
                Job job;
                lock (gate)
                {
                    if (queued.Count == 0)
                    {
                        break;
                    }
                    job = queued[0];
                    queued.RemoveAt(0);
                    inFlight[job.Id] = job;
                }
                RunJob(job);
                if (Stopwatch.GetTimestamp() - start >= limit)
                {
                    break;
                }
            }
        }

        void RunJob(object state)
        {
            var job = (Job)state;
            if (disposed || job.Cancelled)
            {
                finished.Enqueue(new Finished { Job = job });
                return;
            }
            PatchGeometryData data = null;
            AdaptivePatchGeometryBuilder builder = null;
            try
            {
                int count = job.Snapshot.Topology.VertexCount;
                while (dataPool.TryTake(out data) && data.Vertices.Length != count)
                {
                    data = null; // another resolution: let it go
                }
                data ??= new PatchGeometryData(count);
                if (!builderPool.TryTake(out builder))
                {
                    builder = new AdaptivePatchGeometryBuilder();
                }
                builder.Build(job.Snapshot, job.Id, data);
                finished.Enqueue(new Finished { Job = job, Data = data });
            }
            catch (Exception e)
            {
                finished.Enqueue(new Finished { Job = job, Error = e });
            }
            finally
            {
                if (builder != null)
                {
                    builderPool.Add(builder);
                }
            }
        }

        readonly Queue<PatchGeometryData> ready = new Queue<PatchGeometryData>();
        readonly HashSet<PlanetPatchId> readyIds = new HashSet<PlanetPatchId>();

        void CollectFinished()
        {
            while (finished.TryDequeue(out Finished f))
            {
                lock (gate)
                {
                    if (inFlight.TryGetValue(f.Job.Id, out Job current) && current == f.Job)
                    {
                        inFlight.Remove(f.Job.Id);
                    }
                }
                if (f.Error != null)
                {
                    Failed++;
                    LastError = f.Error;
                }
                else if (f.Data == null || f.Job.Cancelled || disposed)
                {
                    Discarded++;
                    Recycle(f.Data);
                }
                else
                {
                    BuildMilliseconds += f.Data.BuildMilliseconds;
                    ready.Enqueue(f.Data);
                    readyIds.Add(f.Data.Id);
                }
                Release(f.Job);
            }
            Dispatch();
        }

        /// <summary>
        /// Next finished geometry for <paramref name="snapshot"/>'s generation (older generations are discarded).
        /// The caller uploads it, then gives it back with <see cref="Recycle"/>.
        /// </summary>
        public bool TryTakeResult(AdaptiveSurfaceSnapshot snapshot, out PatchGeometryData data)
        {
            CollectFinished();
            while (ready.Count > 0)
            {
                data = ready.Dequeue();
                readyIds.Remove(data.Id);
                if (data.Generation == snapshot.Generation)
                {
                    Completed++;
                    return true;
                }
                Discarded++;
                Recycle(data);
            }
            data = null;
            return false;
        }

        /// <summary>True when finished geometry for this patch waits to be taken.</summary>
        public bool HasResult(PlanetPatchId id) => readyIds.Contains(id);

        public int ReadyCount => ready.Count;

        public void Recycle(PatchGeometryData data)
        {
            if (data != null && !disposed && dataPool.Count < 64)
            {
                dataPool.Add(data);
            }
        }

        void Release(Job job)
        {
            job.Snapshot = null;
            if (jobPool.Count < 256)
            {
                jobPool.Push(job);
            }
        }

        /// <summary>Drops every queued request and marks running builds for discard (world changed).</summary>
        public void CancelAll()
        {
            CollectFinished();
            lock (gate)
            {
                foreach (Job job in queued)
                {
                    Cancelled++;
                    Release(job);
                }
                queued.Clear();
                foreach (Job job in inFlight.Values)
                {
                    job.Cancelled = true;
                }
            }
            while (ready.Count > 0)
            {
                Discarded++;
                Recycle(ready.Dequeue());
            }
            readyIds.Clear();
        }

        /// <summary>Stops the pipeline: nothing queued survives; running builds finish into the void.</summary>
        public void Dispose()
        {
            CancelAll();
            disposed = true;
        }
    }
}
