namespace Passwrd.Services;

public sealed class SingleInstance : IDisposable
{
    private const string MutexName = "Passwrd.SingleInstance.7F2D5A3C";
    private const string ActivationEventName = "Passwrd.ShowWindow.7F2D5A3C";

    private Mutex? _mutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _registeredWait;

    private SingleInstance(bool isOwner)
    {
        IsOwner = isOwner;
    }

    public bool IsOwner { get; }

    public event EventHandler? ActivationRequested;

    public static SingleInstance Create()
    {
        Mutex mutex = new(initiallyOwned: true, MutexName, out bool createdNew);
        SingleInstance instance = new(createdNew) { _mutex = mutex };

        if (!createdNew)
        {
            mutex.Dispose();
            instance._mutex = null;
            instance.OpenActivationEvent();
            return instance;
        }

        instance._activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ActivationEventName);

        instance._registeredWait = ThreadPool.RegisterWaitForSingleObject(
            instance._activationEvent,
            (_, _) => instance.ActivationRequested?.Invoke(instance, EventArgs.Empty),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        return instance;
    }

    public void NotifyOwner()
    {
        if (IsOwner)
        {
            return;
        }

        _activationEvent?.Set();
    }

    public void Dispose()
    {
        _registeredWait?.Unregister(null);
        _activationEvent?.Dispose();
        _mutex?.Dispose();
    }

    private void OpenActivationEvent()
    {
        _activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ActivationEventName);
    }
}
