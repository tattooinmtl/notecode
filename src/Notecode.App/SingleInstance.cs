namespace Notecode.App;

/// <summary>
/// One copy of notecode at a time.
///
/// The app lives in the notification area, so it is very easy to end up with
/// two copies: the one in the tray, and one started by clicking the shortcut
/// again. Two copies means two llama-servers and two conversations, and the
/// second one to start wins the port.
///
/// So the second copy does not start. It tells the first one to come forward
/// and leaves.
///
/// The lock is a named mutex, which the operating system releases if the
/// process dies, so a copy that crashes does not lock the app out of its own
/// machine.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\notecode-single-instance";
    private const string ShowEventName = @"Local\notecode-show-window";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly Thread _watcher;
    private bool _released;

    /// <summary>Raised when another copy asks this one to come forward.</summary>
    public event Action? ShowRequested;

    /// <summary>
    /// The copy of the app that holds the lock, if there is one.
    ///
    /// The app gives the lock up before it starts a replacement of itself,
    /// so the new copy is not turned away as a duplicate.
    /// </summary>
    public static SingleInstance? Current { get; private set; }

    private SingleInstance(Mutex mutex, EventWaitHandle showEvent)
    {
        _mutex = mutex;
        _showEvent = showEvent;

        // The watcher waits on a background thread, because waiting on the UI
        // thread would be waiting on the app.
        _watcher = new Thread(Watch) { IsBackground = true, Name = "notecode-single-instance" };
        _watcher.Start();
    }

    /// <summary>
    /// Takes the lock, or returns null when another copy already holds it.
    /// </summary>
    public static SingleInstance? TryAcquire()
    {
        try
        {
            var mutex = new Mutex(true, MutexName, out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return null;
            }

            // The event may be left over from a copy that died. Open it if it
            // is there, and make one if it is not.
            EventWaitHandle showEvent;
            try { showEvent = EventWaitHandle.OpenExisting(ShowEventName); }
            catch { showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName); }

            var instance = new SingleInstance(mutex, showEvent);
            Current = instance;
            return instance;        }
        catch
        {
            // If the lock cannot be taken for any reason, the app still runs.
            // A broken lock is better than an app that will not start.
            return null;
        }
    }

    /// <summary>
    /// Asks the copy that is already running to come forward.
    ///
    /// This is called by the copy that did not get the lock, just before it
    /// leaves.
    /// </summary>
    public static void SignalExisting()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(ShowEventName);
            ev.Set();
        }
        catch
        {
            // The other copy may be between processes. Nothing to do.
        }
    }

    /// <summary>
    /// Gives the lock up, so a copy that is about to be replaced by a new
    /// one does not lock the replacement out.
    /// </summary>
    public void Release()
    {
        try
        {
            if (_released) return;
            _released = true;
            _mutex.ReleaseMutex();
        }
        catch { }
    }

    private void Watch()
    {
        while (true)
        {
            try
            {
                if (_showEvent.WaitOne(500)) ShowRequested?.Invoke();
            }
            catch
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        try { _showEvent.Dispose(); } catch { }
        try { _mutex.Dispose(); } catch { }
    }
}
