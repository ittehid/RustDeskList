using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace RustDeskList;

internal sealed class RustDeskClient
{
    private static readonly HashSet<string> BackgroundModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "--service", "--server", "--tray", "--cm", "--cm-no-ui", "--portable-service",
        "--run-as-system", "--elevate", "--get-id", "--get-uuid", "--version", "--hwcodec-config",
    };
    private static readonly HashSet<string> InteractiveModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "--connect", "--file-transfer", "--port-forward", "--rdp", "--view-camera",
        "--terminal", "--terminal-admin", "--play", "--no-server",
    };

    private string? restartPath;

    public async Task CloseAsync()
    {
        foreach (ClientProcess client in FindClients())
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

                    restartPath ??= client.Path;
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
                    throw new InvalidOperationException("Не удалось закрыть клиент RustDesk. Завершите его и повторите импорт.", ex);
                }
            }
        }
    }

    public void EnsureClosed()
    {
        if (FindClients().Count != 0)
        {
            throw new InvalidOperationException("Клиент RustDesk снова запущен. Повторите импорт после закрытия клиента.");
        }
    }

    public string? TryRestart()
    {
        if (restartPath is not { } path)
        {
            return null;
        }

        try
        {
            if (FindClients().Count == 0)
            {
                using var launched = Process.Start(new ProcessStartInfo(path)
                {
                    WorkingDirectory = Path.GetDirectoryName(path)!,
                    UseShellExecute = true,
                });
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException or ManagementException
            or UnauthorizedAccessException or ArgumentException)
        {
            return "Откройте RustDesk вручную. Не удалось запустить его автоматически.";
        }
    }

    private static List<ClientProcess> FindClients()
    {
        using var current = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        string currentSid = identity.User!.Value;

        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, ParentProcessId, SessionId, CommandLine, ExecutablePath " +
            "FROM Win32_Process WHERE Name = 'rustdesk.exe'");
        var serviceIds = new HashSet<uint>();
        var candidates = new List<ManagementObject>();
        try
        {
            using var processes = searcher.Get();
            foreach (ManagementObject process in processes)
            {
                if ((uint)process["SessionId"] == 0)
                {
                    serviceIds.Add((uint)process["ProcessId"]);
                    process.Dispose();
                }
                else
                {
                    candidates.Add(process);
                }
            }

            var clients = new List<ClientProcess>();
            foreach (ManagementObject process in candidates)
            {
                var commandLine = (string?)process["CommandLine"];
                var path = (string?)process["ExecutablePath"];
                bool currentSession = (uint)process["SessionId"] == current.SessionId;
                if (string.IsNullOrEmpty(commandLine) || string.IsNullOrEmpty(path))
                {
                    // The installed service creates a SYSTEM-owned --server in the user's session.
                    if (currentSession && !serviceIds.Contains((uint)process["ParentProcessId"]))
                    {
                        throw new InvalidOperationException(
                            "Не удалось определить тип запущенного процесса RustDesk. Закройте клиент, запущенный с повышенными правами, и повторите импорт.");
                    }

                    continue;
                }

                string? argument = GetFirstArgument(commandLine);
                if (argument is not null && BackgroundModes.Contains(argument))
                {
                    continue;
                }

                uint processId = (uint)process["ProcessId"];
                using var processInstance = new ManagementObject(FormattableString.Invariant($"Win32_Process.Handle='{processId}'"));
                using var owner = processInstance.InvokeMethod("GetOwnerSid", null, null);
                if ((uint?)owner?["ReturnValue"] != 0)
                {
                    if (currentSession)
                    {
                        throw new InvalidOperationException("Не удалось определить владельца процесса RustDesk. Закройте клиент и повторите импорт.");
                    }

                    continue;
                }

                if (!string.Equals((string?)owner?["Sid"], currentSid, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!currentSession)
                {
                    throw new InvalidOperationException("Закройте клиент RustDesk в другом сеансе Windows и повторите импорт.");
                }

                if (argument is not null && argument.StartsWith('-') && !InteractiveModes.Contains(argument))
                {
                    throw new InvalidOperationException("Дождитесь завершения другой операции RustDesk и повторите импорт.");
                }

                if (!string.Equals(FileVersionInfo.GetVersionInfo(path).ProductName, "RustDesk", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Не удалось подтвердить, что запущенный RustDesk.exe относится к клиенту RustDesk.");
                }

                clients.Add(new ClientProcess(checked((int)processId), path));
            }

            return clients;
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

    private sealed record ClientProcess(int Id, string Path);
}
