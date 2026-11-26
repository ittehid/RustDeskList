using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace RustDeskList;

internal sealed record Contact
{
    public required string Id { get; init; }
    public string Alias { get; init; } = "";
    public string Hostname { get; init; } = "";
    public string Username { get; init; } = "";
    public string Platform { get; init; } = "";
    public bool Favorite { get; init; }
    public DateTimeOffset? ModifiedUtc { get; init; }
}

[Flags]
internal enum ContactFields
{
    Alias = 1,
    Hostname = 2,
    Username = 4,
    Platform = 8,
    Favorite = 16,
    ModifiedUtc = 32,
    All = Alias | Hostname | Username | Platform | Favorite | ModifiedUtc,
}

internal static class ContactFile
{
    private const string Format = "rustdesk-contacts";
    private const int Version = 1;
    private const int MaxFileBytes = 32 * 1024 * 1024;
    private static readonly DateTime EarliestFileTimestamp = DateTime.FromFileTimeUtc(0);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };

    public static Contact[] Load(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxFileBytes)
        {
            throw new InvalidDataException("Файл контактов превышает 32 МБ.");
        }

        BackupDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<BackupDocument>(stream, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Формат JSON не соответствует файлу контактов утилиты.", ex);
        }

        if (document?.Format != Format || document.Version != Version || document.Contacts is null)
        {
            throw new InvalidDataException("Укажите JSON-файл, созданный кнопкой «Экспорт» в этой утилите.");
        }

        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < document.Contacts.Length; index++)
        {
            Contact? contact = document.Contacts[index];
            if (contact is null || !PeerId.TryGetFileName(contact.Id, out string fileName) ||
                contact.Alias is null || contact.Hostname is null ||
                contact.Username is null || contact.Platform is null ||
                (contact.ModifiedUtc is { } modified && modified.UtcDateTime < EarliestFileTimestamp))
            {
                throw new InvalidDataException($"Проверьте ID и поля записи №{index + 1} в файле контактов.");
            }

            if (!fileNames.Add(fileName))
            {
                throw new InvalidDataException($"У записи №{index + 1} совпадает ID или имя файла с другой записью.");
            }
        }

        return document.Contacts;
    }

    public static void Save(string path, Contact[] contacts)
    {
        if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Для экспорта укажите имя файла с расширением .json.");
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new BackupDocument(Format, Version, contacts), Options);
        if (bytes.Length > MaxFileBytes)
        {
            throw new InvalidDataException("Список контактов превышает предельный размер JSON-файла в 32 МБ.");
        }

        string temporary = FileChanges.Stage(path, bytes);
        try
        {
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            FileChanges.DeleteTemporary(temporary);
        }
    }

    public static Contact[] Merge(Contact[] first, Contact[] second, ContactFields fields)
    {
        var contacts = new Dictionary<string, Contact>(StringComparer.OrdinalIgnoreCase);
        foreach (Contact contact in first.Concat(second))
        {
            if (!PeerId.TryGetFileName(contact.Id, out string fileName))
            {
                throw new InvalidDataException($"ID «{contact.Id}» не поддерживается в профиле RustDesk.");
            }

            if (!contacts.TryGetValue(fileName, out Contact? existing))
            {
                contacts.Add(fileName, contact);
                continue;
            }

            if (existing.Id != contact.Id)
            {
                throw new InvalidDataException(
                    $"ID «{existing.Id}» и «{contact.Id}» соответствуют одному имени файла RustDesk в Windows.");
            }

            (Contact newer, Contact older) = Nullable.Compare(existing.ModifiedUtc, contact.ModifiedUtc) >= 0
                ? (existing, contact)
                : (contact, existing);
            contacts[fileName] = newer with
            {
                Alias = PreferText(newer.Alias, older.Alias),
                Hostname = PreferText(newer.Hostname, older.Hostname),
                Username = PreferText(newer.Username, older.Username),
                Platform = PreferText(newer.Platform, older.Platform),
                Favorite = newer.Favorite || older.Favorite,
            };
        }

        return contacts.Values
            .OrderByDescending(contact => contact.ModifiedUtc)
            .ThenBy(contact => contact.Id, StringComparer.Ordinal)
            .Select(contact => contact with
            {
                Alias = fields.HasFlag(ContactFields.Alias) ? contact.Alias : "",
                Hostname = fields.HasFlag(ContactFields.Hostname) ? contact.Hostname : "",
                Username = fields.HasFlag(ContactFields.Username) ? contact.Username : "",
                Platform = fields.HasFlag(ContactFields.Platform) ? contact.Platform : "",
                Favorite = fields.HasFlag(ContactFields.Favorite) && contact.Favorite,
                ModifiedUtc = fields.HasFlag(ContactFields.ModifiedUtc) ? contact.ModifiedUtc : null,
            })
            .ToArray();
    }

    private static string PreferText(string preferred, string fallback) =>
        preferred.Length == 0 ? fallback : preferred;

    private sealed record BackupDocument(string Format, int Version, Contact[] Contacts);
}
