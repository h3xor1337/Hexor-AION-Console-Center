namespace Hexor;

// One UI process per Windows logon session/user, including copies in other folders.
internal sealed class ApplicationInstance : IDisposable
{
    readonly Mutex mutex;
    readonly bool owned;
    internal bool IsPrimary => owned;
    internal ApplicationInstance(string? name = null)
    {
        mutex = new Mutex(false, name ?? @"Local\Hexor.ControlCenter.UI." + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
    }
    public void Dispose() { if (owned) mutex.ReleaseMutex(); mutex.Dispose(); }
}
