using System;
using System.Threading;
using VRageRender;
using VRageRender.ExternalApp;

namespace SpaceEngineers.RTHWAccel
{
    // The game's render thread (VRage.Render MyRenderThread).
    internal static class RenderThread
    {
        public static MyRenderThread WaitFor(TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                MyRenderThread thread = MyRenderProxy.RenderThread;
                if (thread != null)
                    return thread;
                Thread.Sleep(10);
            }
            return null;
        }

        // Runs the action once on the render thread, from its BeforeDraw event
        // (raised in MyRenderThread.RenderFrame before message processing and
        // drawing), and waits for it. Hooks on render-path methods are
        // installed this way: between two draws, no recording thread is inside
        // them (HighFPSFix: patching them from another thread mid-frame
        // crashed the runtime).
        public static void Run(Action action)
        {
            MyRenderThread thread = WaitFor(TimeSpan.FromMinutes(5))
                ?? throw new DetourException("the render thread never started");
            Exception failure = null;
            int ran = 0;
            using (var done = new ManualResetEventSlim(false))
            {
                Action handler = null;
                handler = () =>
                {
                    if (Interlocked.Exchange(ref ran, 1) != 0)
                        return;
                    thread.BeforeDraw -= handler;
                    try
                    {
                        action();
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                    finally
                    {
                        done.Set();
                    }
                };
                thread.BeforeDraw += handler;
                if (!done.Wait(TimeSpan.FromMinutes(5)))
                {
                    thread.BeforeDraw -= handler;
                    if (Interlocked.Exchange(ref ran, 1) == 0)
                        throw new DetourException("the render thread did not draw within 5 minutes");
                    done.Wait();
                }
            }
            if (failure != null)
                throw failure;
        }
    }
}
