using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace RustDeskList;

internal sealed class RustDeskClient
{
    private static readonly HashSet<string> SessionModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "--server", "--tray", "--cm", "--cm-no-ui", "--portable-service",
    };
    private static readonly HashSet<string> InteractiveModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "--connect", "--file-transfer", "--port-forward", "--rdp", "--view-camera",
        "--terminal", "--terminal-admin", "--play", "--no-server",
    };

    public string? RestoreWarning { get; private set; }

    public static bool IsRunning()
    {
        Process[] processes = Process.GetProcessesByName("rustdesk");
        try
        {
            return processes.Length > 0 || RustDeskServicePause.IsActive();
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    public async Task<T> RunClosedAsync<T>(Func<Action, T> operation, bool allowClose)
    {
        var (users, systemIds) = InspectProcesses();
        if (!allowClose && (users.Count != 0 || systemIds.Count != 0 || RustDeskServicePause.IsActive()))
        {
            throw new InvalidOperationException("RustDesk запустился перед операцией. Повторите её и подтвердите закрытие.");
        }

        ClientProcess? restart = users.Find(client => client.Kind == ClientKind.Window)
            ?? users.Find(client => client.Kind == ClientKind.Tray)
            ?? users.Find(client => client.Kind == ClientKind.Server);
        var service = allowClose ? await RustDeskServicePause.BeginAsync() : null;
        try
        {
            await WaitForSystemProcessesAsync(systemIds);
            if (allowClose)
            {
                var (clients, _) = InspectProcesses();
                await CloseClientsAsync(clients);
            }

            EnsureClosed();
            return operation(EnsureClosed);
        }
        finally
        {
            if (service is not null)
            {
                await service.DisposeAsync();
                RestoreWarning = service.RestoreWarning;
            }

            if (restart is not null && TryRestart(restart) is { } warning)
            {
                RestoreWarning = RestoreWarning is null ? warning : $"{RestoreWarning}\n{warning}";
            }
        }
    }

    private static async Task CloseClientsAsync(List<ClientProcess> clients)
    {
        foreach (ClientProcess client in clients)
        {
            Process process;
            try
            {
                process = Process.GetProcessById(client.Id);
            }
            catch (ArgumentException)
            {
                continue;
            }

            using (process)
            {
                try
                {
                    if (!string.Equals(process.MainModule?.FileName, client.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Keep a handle to the verified process until we have finished closing it.
                    _ = process.Handle;
                    if (process.CloseMainWindow())
                    {
                        try
                        {
                            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                        }
                        catch (TimeoutException)
                        {
                            // Current Flutter builds hide the main window in response to WM_CLOSE.
                        }
                    }

                    if (!process.HasExited)
                    {
                        process.Kill();
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The user closed it while we were checking it.
                }
                catch (Win32Exception) when (process.HasExited)
                {
                    // The process exited before Windows finished the request.
                }
                catch (Exception ex) when (ex is Win32Exception or TimeoutException)
                {
                    throw new InvalidOperationException("Не удалось закрыть процесс RustDesk. Завершите его и повторите операцию.", ex);
                }
            }
        }
    }

    private static async Task WaitForSystemProcessesAsync(HashSet<uint> systemIds)
    {
        var elapsed = Stopwatch.StartNew();
        while (HasSystemProcesses(systemIds))
        {
            if (elapsed.Elapsed >= TimeSpan.FromSeconds(8))
            {
                throw new InvalidOperationException("Фоновые процессы службы RustDesk не завершились. Повторите операцию.");
            }

            await Task.Delay(250);
        }
    }

    private static bool HasSystemProcesses(HashSet<uint> ids)
    {
        if (ids.Count == 0)
        {
            return false;
        }

        Process[] processes = Process.GetProcessesByName("rustdesk");
        try
        {
            return processes.Any(process => ids.Contains((uint)process.Id));
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static void EnsureClosed()
    {
        var (users, systems) = InspectProcesses();
        if (users.Count != 0 || systems.Count != 0 || RustDeskServicePause.IsActive())
        {
            throw new InvalidOperationException("RustDesk снова запущен. Повторите операцию после закрытия клиента.");
        }
    }

    private static string? TryRestart(ClientProcess restart)
    {
        try
        {
            var (clients, _) = InspectProcesses();
            if (!clients.Exists(client => client.Kind == restart.Kind))
            {
                var start = new ProcessStartInfo(restart.Path)
                {
                    WorkingDirectory = Path.GetDirectoryName(restart.Path)!,
                    UseShellExecute = true,
                };
                if (restart.Kind == ClientKind.Tray)
                {
                    start.ArgumentList.Add("--tray");
                }
                else if (restart.Kind == ClientKind.Server)
                {
                    start.ArgumentList.Add("--server");
                }

                using var launched = Process.Start(start);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException or ManagementException
            or UnauthorizedAccessException or ArgumentException)
        {
            return "Откройте RustDesk вручную. Не удалось запустить его автоматически.";
        }
    }

    private static (List<ClientProcess> Clients, HashSet<uint> SystemIds) InspectProcesses()
    {
        using var current = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        string currentSid = identity.User!.Value;

        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, ParentProcessId, SessionId, CommandLine, ExecutablePath " +
            "FROM Win32_Process WHERE Name = 'rustdesk.exe'");
        var systemIds = new HashSet<uint>();
        var candidates = new List<ManagementObject>();
        try
        {
            using var processes = searcher.Get();
            foreach (ManagementObject process in processes)
            {
                if ((uint)process["SessionId"] == 0)
                {
                    systemIds.Add((uint)process["ProcessId"]);
                    process.Dispose();
                }
                else
                {
                    candidates.Add(process);
                }
            }

            // The installed service starts a SYSTEM-owned worker in an interactive session.
            bool addedWorker;
            do
            {
                addedWorker = false;
                foreach (ManagementObject process in candidates)
                {
                    if (systemIds.Contains((uint)process["ParentProcessId"]))
                    {
                        addedWorker |= systemIds.Add((uint)process["ProcessId"]);
                    }
                }
            }
            while (addedWorker);

            var clients = new List<ClientProcess>();
            foreach (ManagementObject process in candidates)
            {
                uint processId = (uint)process["ProcessId"];
                if (systemIds.Contains(processId))
                {
                    continue;
                }

                var commandLine = (string?)process["CommandLine"];
                var path = (string?)process["ExecutablePath"];
                bool currentSession = (uint)process["SessionId"] == current.SessionId;
                if (string.IsNullOrEmpty(commandLine) || string.IsNullOrEmpty(path))
                {
                    if (currentSession)
                    {
                        throw new InvalidOperationException(
                            "Не удалось проверить процесс RustDesk. Закройте клиент, запущенный с повышенными правами, и повторите операцию.");
                    }

                    continue;
                }

                using var processInstance = new ManagementObject(FormattableString.Invariant($"Win32_Process.Handle='{processId}'"));
                ManagementBaseObject? ownerResult;
                try
                {
                    ownerResult = processInstance.InvokeMethod("GetOwnerSid", null, null);
                }
                catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.NotFound)
                {
                    continue;
                }

                using var owner = ownerResult;
                if ((uint?)owner?["ReturnValue"] != 0)
                {
                    if (currentSession)
                    {
                        throw new InvalidOperationException("Не удалось определить владельца процесса RustDesk. Закройте клиент и повторите операцию.");
                    }

                    continue;
                }

                if (!string.Equals((string?)owner?["Sid"], currentSid, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!currentSession)
                {
                    throw new InvalidOperationException("Закройте клиент RustDesk в другом сеансе Windows и повторите операцию.");
                }

                string? argument = GetFirstArgument(commandLine);
                bool background = argument is not null && SessionModes.Contains(argument);
                if (!background && argument is not null && argument.StartsWith('-') && !InteractiveModes.Contains(argument))
                {
                    throw new InvalidOperationException("Дождитесь завершения другой операции RustDesk и повторите.");
                }

                if (!string.Equals(FileVersionInfo.GetVersionInfo(path).ProductName, "RustDesk", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Не удалось подтвердить, что запущенный RustDesk.exe относится к клиенту RustDesk.");
                }

                ClientKind kind = background ? argument!.ToLowerInvariant() switch
                {
                    "--tray" => ClientKind.Tray,
                    "--server" => ClientKind.Server,
                    _ => ClientKind.Helper,
                } : ClientKind.Window;
                clients.Add(new ClientProcess(checked((int)processId), path, kind));
            }

            return (clients, systemIds);
        }
        catch (ManagementException ex)
        {
            throw new InvalidOperationException("Windows не удалось проверить процессы RustDesk.", ex);
        }
        finally
        {
            foreach (ManagementObject candidate in candidates)
            {
                candidate.Dispose();
            }
        }
    }

    private static string? GetFirstArgument(string commandLine)
    {
        nint arguments = CommandLineToArgvW(commandLine, out int count);
        if (arguments == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            return count > 1 ? Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, IntPtr.Size)) : null;
        }
        finally
        {
            LocalFree(arguments);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

    private enum ClientKind
    {
        Window,
        Tray,
        Server,
        Helper,
    }

    private sealed record ClientProcess(int Id, string Path, ClientKind Kind);
}
