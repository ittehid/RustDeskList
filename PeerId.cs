using System.Text;

namespace RustDeskList;

internal static class PeerId
{
    private const string EncodedPrefix = "base64_";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
    private static readonly char[] RustDeskEncodedChars = ['<', '>', ':', '/', '\\', '|', '?', '*'];
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",
    };

    public static bool TryGetFileName(string? id, out string fileName)
    {
        fileName = "";
        if (string.IsNullOrEmpty(id) || id.Any(char.IsWhiteSpace) || id.Any(char.IsControl) ||
            id is "." or "..")
        {
            return false;
        }

        string stem = id;
        if (id.IndexOfAny(RustDeskEncodedChars) >= 0)
        {
            try
            {
                stem = EncodedPrefix + Convert.ToBase64String(Utf8.GetBytes(id));
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
        }
        else if (id.StartsWith(EncodedPrefix, StringComparison.Ordinal) && id.Length > EncodedPrefix.Length)
        {
            return false;
        }

        // RustDesk uses standard Base64. A slash in the result cannot be a Windows file-name character.
        if (stem.Length > 250 || stem.IndexOfAny(InvalidFileNameChars) >= 0 ||
            DeviceNames.Contains(stem.Split('.', 2)[0]))
        {
            return false;
        }

        fileName = stem + ".toml";
        return true;
    }

    public static bool TryDecodeFileName(string fileName, out string id)
    {
        id = Path.GetFileNameWithoutExtension(fileName);
        if (id.StartsWith(EncodedPrefix, StringComparison.Ordinal) && id.Length > EncodedPrefix.Length)
        {
            try
            {
                id = Utf8.GetString(Convert.FromBase64String(id[EncodedPrefix.Length..]));
            }
            catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
            {
                return false;
            }
        }

        return TryGetFileName(id, out string canonicalName) &&
            string.Equals(fileName, canonicalName, StringComparison.Ordinal);
    }
}
