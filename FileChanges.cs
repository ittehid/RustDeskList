using System.Globalization;
using System.Text;

namespace RustDeskList;

internal sealed record OriginalFile(byte[] Bytes, DateTime LastWriteUtc)
{
    public static OriginalFile? Read(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            file.CopyTo(buffer);
            return new OriginalFile(buffer.ToArray(), File.GetLastWriteTimeUtc(path));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}

internal sealed record ProfileChange(
    string RelativePath, OriginalFile? Original, byte[] Bytes, DateTime? LastWriteUtc);

internal static class FileChanges
{
    public static string? Apply(
        string profileDirectory, string backupRoot, IReadOnlyList<ProfileChange> changes, Action ensureClientClosed)
    {
        if (changes.Count == 0)
        {
            return null;
        }

        string label = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        string backup = Path.Combine(backupRoot, $"{label}_{Guid.NewGuid():N}");
        var prepared = new List<(string Destination, string Temporary, ProfileChange Change)>();
        int applied = 0;

        try
        {
            Directory.CreateDirectory(backup);
            foreach (ProfileChange change in changes)
            {
                string destination = Path.Combine(profileDirectory, change.RelativePath);
                EnsureOriginal(destination, change.Original);
                if (change.Original is { } original)
                {
                    string backupFile = Path.Combine(backup, change.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
                    File.WriteAllBytes(backupFile, original.Bytes);
                    File.SetLastWriteTimeUtc(backupFile, original.LastWriteUtc);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                string temporary = Stage(destination, change.Bytes, change.LastWriteUtc);
                prepared.Add((destination, temporary, change));
            }

            File.WriteAllLines(Path.Combine(backup, "created-files.txt"),
                changes.Where(change => change.Original is null).Select(change => change.RelativePath), Encoding.UTF8);
            ensureClientClosed();

            foreach (var (destination, temporary, change) in prepared)
            {
                EnsureOriginal(destination, change.Original);
                if (change.Original is null)
                {
                    File.Move(temporary, destination);
                }
                else
                {
                    File.Replace(temporary, destination, destinationBackupFileName: null);
                }

                applied++;
            }

            return backup;
        }
        catch (Exception error) when (applied > 0 && error is (IOException or UnauthorizedAccessException or InvalidDataException))
        {
            int restoreFailures = 0;
            for (int index = applied - 1; index >= 0; index--)
            {
                var (destination, _, change) = prepared[index];
                try
                {
                    OriginalFile? current = OriginalFile.Read(destination);
                    if (current is null || !current.Bytes.AsSpan().SequenceEqual(change.Bytes))
                    {
                        throw new IOException("Файл профиля был изменён другим процессом.");
                    }

                    if (change.Original is { } original)
                    {
                        string restore = Stage(destination, original.Bytes, original.LastWriteUtc);
                        try
                        {
                            File.Replace(restore, destination, destinationBackupFileName: null);
                        }
                        finally
                        {
                            DeleteTemporary(restore);
                        }
                    }
                    else
                    {
                        File.Delete(destination);
                    }
                }
                catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException)
                {
                    restoreFailures++;
                }
            }

            string message = restoreFailures == 0
                ? $"Импорт прерван. Изменения отменены.\n\n{error.Message}"
                : $"Импорт прерван. Часть файлов требует восстановления из резервной копии.\n\n{backup}\n\n{error.Message}";
            throw new IOException(message, error);
        }
        finally
        {
            foreach (var (_, temporary, _) in prepared)
            {
                DeleteTemporary(temporary);
            }
        }
    }

    public static string Stage(string destination, byte[] bytes, DateTime? lastWriteUtc = null)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".rustdesklist-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }

            if (lastWriteUtc is { } timestamp)
            {
                File.SetLastWriteTimeUtc(temporary, timestamp);
            }

            return temporary;
        }
        catch
        {
            DeleteTemporary(temporary);
            throw;
        }
    }

    public static void DeleteTemporary(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The .tmp extension keeps RustDesk from loading an incomplete file.
        }
    }

    private static void EnsureOriginal(string path, OriginalFile? expected)
    {
        OriginalFile? current = OriginalFile.Read(path);
        if (current is null && expected is null)
        {
            return;
        }

        if (current is null || expected is null || current.LastWriteUtc != expected.LastWriteUtc ||
            !current.Bytes.AsSpan().SequenceEqual(expected.Bytes))
        {
            throw new IOException("Профиль RustDesk изменился во время импорта. Повторите операцию.");
        }
    }
}
