using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace RustDeskList;

internal sealed record ContactSnapshot(Contact[] Contacts, int Skipped);

internal sealed record ImportPlan(
    int Added, int Updated, int AddedToFavorites, IReadOnlyList<ProfileChange> Changes);

internal sealed class RustDeskProfile(string directory, string workingDirectory)
{
    private const string LocalFileName = "RustDesk_local.toml";
    private const string PeersFolderName = "peers";
    private const string UnknownPlatform = "Unknown";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    private string PeersPath => Path.Combine(DirectoryPath, PeersFolderName);
    private string LocalFilePath => Path.Combine(DirectoryPath, LocalFileName);

    public ContactSnapshot ReadContacts()
    {
        var contacts = new Dictionary<string, Contact>(StringComparer.OrdinalIgnoreCase);
        var favorites = new HashSet<string>(ReadFavorites(ReadToml(LocalFilePath)), StringComparer.OrdinalIgnoreCase);
        int skipped = 0;

        foreach (string peerFile in EnumeratePeerFiles())
        {
            if (!peerFile.EndsWith(".toml", StringComparison.Ordinal) ||
                !PeerId.TryDecodeFileName(Path.GetFileName(peerFile), out string id))
            {
                skipped++;
                continue;
            }

            try
            {
                OriginalFile? original = OriginalFile.Read(peerFile);
                if (original is null)
                {
                    continue;
                }

                TomlTable peer = ReadToml(peerFile, original);
                TomlTable? info = ReadTable(peer, "info");
                string platform = ReadString(info, "platform");
                bool favorite = favorites.Contains(id);
                if (platform.Length == 0 && !favorite)
                {
                    // RustDesk filters these files from Recent sessions.
                    continue;
                }

                var contact = new Contact
                {
                    Id = id,
                    Alias = ReadString(ReadTable(peer, "options"), "alias"),
                    Hostname = ReadString(info, "hostname"),
                    Username = ReadString(info, "username"),
                    Platform = platform == UnknownPlatform ? "" : platform,
                    Favorite = favorite,
                    ModifiedUtc = original.LastWriteUtc,
                };
                if (!contacts.TryAdd(id, contact))
                {
                    skipped++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                skipped++;
            }
        }

        foreach (string id in favorites)
        {
            if (PeerId.TryGetFileName(id, out _))
            {
                contacts.TryAdd(id, new Contact { Id = id, Favorite = true });
            }
            else
            {
                skipped++;
            }
        }

        return new ContactSnapshot(
            contacts.Values.OrderByDescending(contact => contact.ModifiedUtc)
                .ThenBy(contact => contact.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray(), skipped);
    }

    public ImportPlan PlanImport(IEnumerable<Contact> contacts)
    {
        OriginalFile? originalLocal = OriginalFile.Read(LocalFilePath);
        TomlTable local = ReadToml(LocalFilePath, originalLocal);
        var favorites = ReadFavorites(local);
        var favoriteIds = favorites.ToHashSet(StringComparer.Ordinal);
        var peerFileNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string peerFile in EnumeratePeerFiles())
        {
            string name = Path.GetFileName(peerFile);
            if (!peerFileNames.TryAdd(name, name))
            {
                throw new InvalidDataException("В профиле найдены совпадающие имена файлов компьютеров.");
            }
        }

        var changes = new List<ProfileChange>();
        int added = 0;
        int updated = 0;
        int addedToFavorites = 0;

        foreach (Contact contact in contacts)
        {
            if (!PeerId.TryGetFileName(contact.Id, out string fileName))
            {
                throw new InvalidDataException($"ID «{contact.Id}» не поддерживается именами файлов RustDesk в Windows.");
            }

            if (peerFileNames.TryGetValue(fileName, out string? existingName) && existingName != fileName)
            {
                throw new InvalidDataException($"Для ID «{contact.Id}» уже есть файл с другим регистром символов.");
            }

            string relativePath = Path.Combine(PeersFolderName, fileName);
            string path = Path.Combine(DirectoryPath, relativePath);
            OriginalFile? original = OriginalFile.Read(path);
            TomlTable peer = ReadToml(path, original);
            bool infoChanged = FillContactInfo(peer, contact);
            if (original is null || infoChanged)
            {
                changes.Add(new ProfileChange(relativePath, original, Serialize(peer),
                    original?.LastWriteUtc ?? contact.ModifiedUtc?.UtcDateTime));

                if (original is null)
                {
                    added++;
                }
                else
                {
                    updated++;
                }
            }

            if (contact.Favorite && favoriteIds.Add(contact.Id))
            {
                favorites.Add(contact.Id);
                addedToFavorites++;
            }
        }

        if (addedToFavorites > 0)
        {
            var array = new TomlArray(favorites.Count);
            foreach (string id in favorites)
            {
                array.Add(id);
            }

            local["fav"] = array;
            changes.Add(new ProfileChange(LocalFileName, originalLocal, Serialize(local), null));
        }

        return new ImportPlan(added, updated, addedToFavorites, changes);
    }

    public FileStream LockImport()
    {
        Directory.CreateDirectory(workingDirectory);
        try
        {
            return new FileStream(Path.Combine(workingDirectory, "import.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
        {
            throw new IOException("Дождитесь завершения импорта в другом окне утилиты.", ex);
        }
    }

    public string? Apply(ImportPlan plan, Action ensureClientClosed) =>
        FileChanges.Apply(DirectoryPath, Path.Combine(workingDirectory, "Backups"), plan.Changes, ensureClientClosed);

    private IEnumerable<string> EnumeratePeerFiles() =>
        Directory.Exists(PeersPath) ? Directory.EnumerateFiles(PeersPath, "*.toml") : [];

    private static TomlTable ReadToml(string path) => ReadToml(path, OriginalFile.Read(path));

    private static TomlTable ReadToml(string path, OriginalFile? file)
    {
        if (file is null)
        {
            return new TomlTable();
        }

        try
        {
            string text = Utf8.GetString(file.Bytes).TrimStart('\uFEFF');
            return TomlSerializer.Deserialize<TomlTable>(text) ?? new TomlTable();
        }
        catch (Exception ex) when (ex is TomlException or DecoderFallbackException)
        {
            throw new InvalidDataException($"Файл «{Path.GetFileName(path)}» содержит некорректный TOML.", ex);
        }
    }

    private static byte[] Serialize(TomlTable table) => Utf8.GetBytes(TomlSerializer.Serialize(table));

    private static List<string> ReadFavorites(TomlTable local)
    {
        if (!local.TryGetValue("fav", out object? value))
        {
            return [];
        }

        if (value is TomlArray array && array.All(item => item is string))
        {
            return array.Cast<string>().ToList();
        }

        throw new InvalidDataException("В RustDesk_local.toml повреждён список избранного.");
    }

    private static bool FillContactInfo(TomlTable peer, Contact contact)
    {
        TomlTable info = GetOrCreateTable(peer, "info");
        bool changed = FillText(info, "hostname", contact.Hostname);
        changed |= FillText(info, "username", contact.Username);

        string currentPlatform = ReadString(info, "platform");
        if (currentPlatform.Length == 0 ||
            (currentPlatform == UnknownPlatform && contact.Platform.Length != 0 && contact.Platform != UnknownPlatform))
        {
            // RustDesk ignores entries whose platform is empty, including favorites.
            info["platform"] = contact.Platform.Length == 0 ? UnknownPlatform : contact.Platform;
            changed = true;
        }

        if (contact.Alias.Length != 0)
        {
            changed |= FillText(GetOrCreateTable(peer, "options"), "alias", contact.Alias);
        }

        return changed;
    }

    private static bool FillText(TomlTable table, string key, string value)
    {
        if (value.Length == 0 || ReadString(table, key).Length != 0)
        {
            return false;
        }

        table[key] = value;
        return true;
    }

    private static TomlTable? ReadTable(TomlTable table, string key) =>
        table.TryGetValue(key, out object? value) ? value as TomlTable : null;

    private static string ReadString(TomlTable? table, string key) =>
        table is not null && table.TryGetValue(key, out object? value) && value is string text ? text : "";

    private static TomlTable GetOrCreateTable(TomlTable parent, string key)
    {
        if (!parent.TryGetValue(key, out object? value))
        {
            var table = new TomlTable();
            parent[key] = table;
            return table;
        }

        return value as TomlTable ?? throw new InvalidDataException($"Ожидалась таблица «{key}» в файле профиля.");
    }
}
