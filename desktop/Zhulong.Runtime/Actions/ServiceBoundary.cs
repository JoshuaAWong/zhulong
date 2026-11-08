using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Zhulong.Runtime.Actions;

public enum ServiceState { Stopped = 1, StartPending = 2, StopPending = 3, Running = 4, Other = 0 }
public interface IServiceBoundary
{
    ServiceState Status(string service);
    void Stop(string service);
    void Start(string service);
}

public sealed class NativeServiceBoundary : IServiceBoundary
{
    public ServiceState Status(string service)
    {
        using var manager = Manager();
        using var handle = Service(manager, service, 4);
        var size = Marshal.SizeOf<ServiceStatus>();
        if (!QueryServiceStatusEx(handle.DangerousGetHandle(), 0, out var status, size, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return Enum.IsDefined(typeof(ServiceState), (int)status.State) ? (ServiceState)status.State : ServiceState.Other;
    }
    public void Stop(string service)
    {
        using var manager = Manager();
        using var handle = Service(manager, service, 0x20 | 4);
        if (!ControlService(handle.DangerousGetHandle(), 1, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Start(string service)
    {
        using var manager = Manager();
        using var handle = Service(manager, service, 0x10 | 4);
        if (!StartService(handle.DangerousGetHandle(), 0, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    static ServiceHandle Manager()
    {
        var value = new ServiceHandle(OpenSCManager(null, null, 1));
        if (value.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        return value;
    }
    static ServiceHandle Service(ServiceHandle manager, string name, uint access)
    {
        var value = new ServiceHandle(OpenService(manager.DangerousGetHandle(), name, access));
        if (value.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        return value;
    }
    sealed class ServiceHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle(IntPtr value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] struct ServiceStatus
    { public uint Type, State, Controls, ExitCode, SpecificExitCode, Checkpoint, WaitHint, Pid, Flags; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatus status, int size, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool StartService(IntPtr service, int count, IntPtr arguments);
}
