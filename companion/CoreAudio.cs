using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace ClawPal.Companion;

// Every Windows Core Audio call (Media's player volume, Shortcuts' mute and volume tiles, the
// Audio app's mixer) runs on this one MTA thread, one at a time. The device enumerator is shared
// across the process, so a release on one thread could otherwise pull it from under another;
// the result was every later call in that run failing with InvalidCastException.
internal static class CoreAudio
{
    private static readonly BlockingCollection<Action> work = new();
    private static readonly Thread worker = Start();

    private static Thread Start()
    {
        var thread = new Thread(() => { foreach (var job in work.GetConsumingEnumerable()) job(); })
        { IsBackground = true, Name = "ClawPal Core Audio" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return thread;
    }

    // Runs the job on the audio thread and returns its result, or rethrows its exception. A job
    // that takes over five seconds is reported as a COM failure, which every caller handles.
    internal static T Run<T>(Func<T> job)
    {
        if (Thread.CurrentThread == worker) return job();
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        work.Add(() => { try { done.SetResult(job()); } catch (Exception error) { done.SetException(error); } });
        if (!((IAsyncResult)done.Task).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5))) throw new COMException("Windows audio did not answer in time.");
        return done.Task.GetAwaiter().GetResult();
    }
}
