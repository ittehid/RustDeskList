using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32.SafeHandles;

namespace RustDeskList;

internal sealed class RustDeskServicePause(NamedPipeServerStream pipe, Process helper) : IAsyncDisposable
{
    public const string HelperArgument = "--hold-rustdesk-service";
    private const string ServiceName = "RustDesk";
    private const string PipePrefix = "RustDeskList-";
    private const byte Ok = 1;
    private const byte Failed = 0;
    private const byte RestoreFailed = 2;
    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(45);

    public string? RestoreWarning { get; private set; }

    public static bool IsActive()
    {
        using var service = new ServiceController(ServiceName);
        try
        {
            return service.Status != ServiceControllerStatus.Stopped;
        }
        catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 1060 })
        {
            return false;
        }
    }

    public static async Task<RustDeskServicePause?> BeginAsync()
    {
        if (!IsActive())
        {
            return null;
        }

        string token = Guid.NewGuid().ToString("N");
        using var identity = WindowsIdentity.GetCurrent();
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(admins, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        var channel = NamedPipeServerStreamAcl.Create(PipePrefix + token, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        Process? child = null;
        bool ready = false;

        try
        {
            string path = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь к RustDeskList.exe.");
            var start = new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = new WindowsPrincipal(identity).IsInRole(admins) ? "" : "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            start.ArgumentList.Add(HelperArgument);
            start.ArgumentList.Add(token);
            child = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить остановку службы RustDesk.");

            using var timeout = new CancellationTokenSource(ReplyTimeout);
            Task connection = channel.WaitForConnectionAsync(timeout.Token);
            await Task.WhenAny(connection, child.WaitForExitAsync(timeout.Token));
            if (!connection.IsCompleted)
            {
                throw new InvalidOperationException("Процесс остановки службы RustDesk завершился раньше времени.");
            }

            await connection;
            if (!GetNamedPipeClientProcessId(channel.SafePipeHandle, out uint caller) || caller != child.Id)
            {
                throw new InvalidOperationException("Не удалось подтвердить процесс остановки службы RustDesk.");
            }

            int status = await ReadSignalAsync(channel, timeout.Token);
            if (status != Ok)
            {
                string message = status == RestoreFailed
                    ? "Не удалось восстановить службу RustDesk. Проверьте её состояние в «Службах» Windows."
                    : "Не удалось остановить службу RustDesk.";
                throw new InvalidOperationException(message);
            }

            ready = true;
            return new RustDeskServicePause(channel, child);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Подтверждение прав администратора отменено.", ex);
        }
        catch (OperationCanceledException ex)
        {
            throw new System.TimeoutException("Windows не ответила на остановку службы RustDesk.", ex);
        }
        finally
        {
            if (!ready)
            {
                channel.Dispose();
                child?.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(ReplyTimeout);
            await pipe.WriteAsync(new byte[] { Ok }, timeout.Token);
            if (await ReadSignalAsync(pipe, timeout.Token) != Ok)
            {
                RestoreWarning = "Не удалось автоматически запустить службу RustDesk. Проверьте её в «Службах» Windows.";
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
        {
            RestoreWarning = "Не удалось подтвердить запуск службы RustDesk. Проверьте её в «Службах» Windows.";
        }
        finally
        {
            pipe.Dispose();
            helper.Dispose();
        }
    }

    public static async Task HoldServiceAsync(string token)
    {
        if (!Guid.TryParseExact(token, "N", out _))
        {
            return;
        }

        // The client cannot impersonate this elevated process through the pipe.
        using var channel = new NamedPipeClientStream(".", PipePrefix + token, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await channel.ConnectAsync(15000);
        }
        catch (Exception ex) when (ex is System.TimeoutException or IOException or UnauthorizedAccessException)
        {
            return;
        }

        using var service = new ServiceController(ServiceName);
        bool restore = false;
        bool ready = false;
        try
        {
            if (service.Status == ServiceControllerStatus.StartPending)
            {
                service.WaitForStatus(ServiceControllerStatus.Running, ServiceTimeout);
            }
            else if (service.Status == ServiceControllerStatus.StopPending)
            {
                service.WaitForStatus(ServiceControllerStatus.Stopped, ServiceTimeout);
            }

            if (service.Status != ServiceControllerStatus.Stopped)
            {
                if (service.Status != ServiceControllerStatus.Running || !service.CanStop ||
                    service.StartType == ServiceStartMode.Disabled)
                {
                    throw new InvalidOperationException("The RustDesk service cannot be stopped and restored.");
                }

                restore = true;
                service.Stop(stopDependentServices: false);
                service.WaitForStatus(ServiceControllerStatus.Stopped, ServiceTimeout);
            }

            await channel.WriteAsync(new byte[] { Ok });
            ready = true;
            // A disconnected caller also releases the service.
            await ReadSignalAsync(channel, CancellationToken.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException or System.ServiceProcess.TimeoutException)
        {
        }
        finally
        {
            bool restored = !restore || Restore(service);
            try
            {
                await channel.WriteAsync(new byte[] { restored ? (ready ? Ok : Failed) : RestoreFailed });
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                // The caller may have exited. Service restoration has already been attempted.
            }
        }
    }

    private static bool Restore(ServiceController service)
    {
        try
        {
            service.Refresh();
            if (service.Status == ServiceControllerStatus.StopPending)
            {
                service.WaitForStatus(ServiceControllerStatus.Stopped, ServiceTimeout);
            }

            if (service.Status == ServiceControllerStatus.Stopped)
            {
                service.Start();
            }

            service.WaitForStatus(ServiceControllerStatus.Running, ServiceTimeout);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or System.ServiceProcess.TimeoutException)
        {
            return false;
        }
    }

    private static async Task<int> ReadSignalAsync(PipeStream stream, CancellationToken cancellation)
    {
        var buffer = new byte[1];
        return await stream.ReadAsync(buffer, cancellation) == 0 ? -1 : buffer[0];
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipeHandle, out uint clientProcessId);
}
