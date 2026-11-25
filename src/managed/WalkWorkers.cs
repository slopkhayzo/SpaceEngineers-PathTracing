using System;
using System.Diagnostics;
using System.Threading;

namespace SpaceEngineers.RTHWAccel
{
    // Worker threads the scene walk spreads its reading of the game's objects over (SceneMirror: the model
    // instances' and renderables' state, read in parallel, gathered in their order afterwards). The render thread
    // takes chunks too and waits in the walk until all are done, so the game's render state does not change under
    // the workers: the render thread is the one that changes it. What the workers call only reads it (field reads;
    // MyActor.WorldMatrix brings a matrix up to date as the game's parallel culling also lets it, behind a
    // volatile flag).
    //
    // Waking a thread takes a while (on the test machine's virtual processors up to about a millisecond), so the
    // walk wakes the workers when it starts (Begin) and they stay awake until it ends (End), taking chunks of each
    // run as it comes and yielding their processor to any other thread meanwhile. The render thread waits only for
    // the chunks being worked on, spinning (the .NET waits sleep a millisecond at a time after a while, which
    // made the first version slower than the walk alone). Chunks go out by an interlocked counter; a worker's
    // failure is thrown again on the render thread.
    internal static class WalkWorkers
    {
        private const int Chunk = 128;               // objects per chunk
        private const int Idle = int.MaxValue / 2;  // s_next between runs: no chunk to take
        private static Thread[] s_threads = new Thread[0];
        private static readonly SemaphoreSlim s_wake = new SemaphoreSlim(0, int.MaxValue);
        private static int s_sleeping;               // workers waiting on s_wake
        private static volatile bool s_awake;        // between Begin and End: the workers stay awake
        private static Action<int, int> s_work;      // the run's work: (first, end)
        private static int s_count;                  // the run's objects
        private static int s_next = Idle;            // the next chunk's first index
        private static int s_inFlight;               // threads between taking a chunk and finishing it
        private static Exception s_failure;

        public static int Count => s_threads.Length;

        // `workers` threads (-1: a quarter of the logical processors, at most 4, with 8 or more of them; none with
        // fewer, where workers kept awake through the walk would take processors the game needs). Sparks of the
        // Future (8.4k model instances, 2.8k renderables) on 16: the walk 4.6 -> 3.0 ms, the frame 11.7 -> 11.0 ms.
        public static void Start(int workers)
        {
            if (workers < 0)
                workers = Environment.ProcessorCount >= 8 ? Math.Min(4, Environment.ProcessorCount / 4) : 0;
            if (s_threads.Length != 0 || workers <= 0)
                return;
            var threads = new Thread[workers];
            for (int i = 0; i < workers; i++)
            {
                threads[i] = new Thread(Loop) { IsBackground = true, Name = $"RTHWAccel walk {i + 1}" };
                threads[i].Start();
            }
            s_threads = threads;
        }

        // The walk starts: the workers wake (they stay awake until End).
        public static void Begin()
        {
            if (s_threads.Length == 0 || s_awake)
                return;
            s_awake = true;
            int sleeping = Volatile.Read(ref s_sleeping);
            if (sleeping > 0)
                s_wake.Release(sleeping);
        }

        // The walk ends: the workers go back to sleep.
        public static void End() => s_awake = false;

        // work(first, end) over [0, count) in chunks: on the awake workers and the calling thread when `parallel`,
        // else on the calling thread alone; returns when all chunks are done.
        public static void Run(int count, Action<int, int> work, bool parallel)
        {
            if (count <= 0)
                return;
            if (!parallel || !s_awake || count <= Chunk)
            {
                work(0, count);
                return;
            }
            s_failure = null;
            s_work = work;
            s_count = count;
            Volatile.Write(ref s_next, 0);  // published: the workers may take chunks from now on
            Drain(false);
            // The chunks the workers took: done soon (a chunk is about a tenth of a millisecond).
            int spins = 0;
            while (Volatile.Read(ref s_inFlight) != 0)
            {
                if (++spins < 4096)
                    Thread.SpinWait(16);
                else
                    Thread.Yield();
            }
            Volatile.Write(ref s_next, Idle);
            Exception failure = s_failure;
            if (failure != null)
                throw new InvalidOperationException("a scene walk worker failed", failure);
        }

        // Takes chunks until none is left. On a worker a failure is kept for the render thread and stops the run's
        // other chunks; on the render thread it is thrown (the workers' chunks done first).
        private static void Drain(bool worker)
        {
            for (;;)
            {
                Interlocked.Increment(ref s_inFlight);
                int first = Interlocked.Add(ref s_next, Chunk) - Chunk;
                int count = Volatile.Read(ref s_count);
                if (first >= count)
                {
                    Interlocked.Decrement(ref s_inFlight);
                    return;
                }
                try
                {
                    s_work(first, Math.Min(first + Chunk, count));
                }
                catch (Exception exception)
                {
                    Volatile.Write(ref s_next, Idle);  // the run's other chunks are not taken
                    if (!worker)
                    {
                        Interlocked.Decrement(ref s_inFlight);
                        while (Volatile.Read(ref s_inFlight) != 0)
                            Thread.Yield();
                        throw;
                    }
                    s_failure = exception;
                }
                Interlocked.Decrement(ref s_inFlight);
            }
        }

        private static void Loop()
        {
            for (;;)
            {
                Interlocked.Increment(ref s_sleeping);
                s_wake.Wait();
                Interlocked.Decrement(ref s_sleeping);
                while (s_awake)
                {
                    if (Volatile.Read(ref s_next) < Volatile.Read(ref s_count))
                        Drain(true);
                    else if (!Thread.Yield())
                        Thread.SpinWait(16);
                }
            }
        }
    }

    // Times the walk's frames for the report: the interval from one walk's start to the next one's.
    internal struct FrameClock
    {
        private long m_last;
        public long Sum;
        public int Count;

        public void Tick()
        {
            long now = Stopwatch.GetTimestamp();
            if (m_last != 0)
            {
                Sum += now - m_last;
                ++Count;
            }
            m_last = now;
        }

        public double AverageMs => Count == 0 ? 0 : Sum * 1000.0 / Stopwatch.Frequency / Count;

        public void Restart()
        {
            Sum = 0;
            Count = 0;
        }
    }
}
