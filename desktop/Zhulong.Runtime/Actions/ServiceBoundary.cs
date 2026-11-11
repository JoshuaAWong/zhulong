using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Zhulong.Runtime.Actions;

public enum ServiceState { Stopped = 1, StartPending = 2, StopPending = 3, Running = 4, Other = 0 }
public interface IServiceBoundary
{
    IServiceSession OpenForRestart(string service);
}
public interface IServiceSession : IDisposable
{
    ServiceState Status();
    void Stop();
    void Start();
}

public sealed class NativeServiceBoundary : IServiceBoundary
{
    const uint ScManagerConnect = 0x01;
    const uint ServiceQueryStatus = 0x04;
    const uint ServiceStart = 0x10;
    const uint ServiceStop = 0x20;

    public IServiceSession OpenForRestart(string service)
    {
        using var manager = RequireHandle(OpenSCManager(null, null, ScManagerConnect));
        // 一次取得重启所需权限，后续启停和取消补偿都使用同一服务句柄。
        return new ServiceSession(RequireHandle(OpenService(manager, service, ServiceQueryStatus | ServiceStart | ServiceStop)));
    }

    sealed class ServiceSession(ServiceHandle handle) : IServiceSession
    {
        public ServiceState Status()
        {
            var size = Marshal.SizeOf<ServiceStatus>();
            if (!QueryServiceStatusEx(handle, 0, out var status, size, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return Enum.IsDefined(typeof(ServiceState), (int)status.State) ? (ServiceState)status.State : ServiceState.Other;
        }
        public void Stop()
        {
            if (!ControlService(handle, 1, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Start()
        {
            if (!StartService(handle, 0, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose() => handle.Dispose();
    }

    static ServiceHandle RequireHandle(ServiceHandle handle)
    {
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
    }
    sealed class ServiceHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] struct ServiceStatus
    { public uint Type, State, Controls, ExitCode, SpecificExitCode, Checkpoint, WaitHint, Pid, Flags; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool QueryServiceStatusEx(ServiceHandle service, int level, out ServiceStatus status, int size, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool ControlService(ServiceHandle service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool StartService(ServiceHandle service, int count, IntPtr arguments);
}
