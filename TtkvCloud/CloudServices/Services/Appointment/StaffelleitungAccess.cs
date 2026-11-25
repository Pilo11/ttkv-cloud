using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace CloudServices.Services.Appointment;

/// <summary>
/// Guards the Staffelleitung pages. There is no login: whoever knows the secret
/// path segment is the Staffelleiter. The key never appears in public pages.
/// </summary>
public sealed class StaffelleitungAccess
{
    public const string PathPrefix = "/staffelleitung/";
    private const string KeyFileName = "staffelleitung.key";
    private const int MinKeyLength = 16;

    private readonly byte[] _keyBytes;

    public StaffelleitungAccess(
        IOptions<TermineOptions> options,
        TermineStore store,
        ILogger<StaffelleitungAccess> logger)
    {
        var configured = options.Value.AdminKey?.Trim();
        string key;
        if (!string.IsNullOrEmpty(configured))
        {
            if (configured.Length < MinKeyLength || !IsUrlSafe(configured))
            {
                throw new InvalidOperationException(
                    $"Termine:AdminKey muss mindestens {MinKeyLength} Zeichen lang sein und darf nur A-Z, a-z, 0-9, '-' und '_' enthalten.");
            }

            key = configured;
        }
        else
        {
            key = LoadOrCreateKey(Path.Combine(store.DataDirectory, KeyFileName));
        }

        Key = key;
        _keyBytes = Encoding.UTF8.GetBytes(key);
        logger.LogInformation("Staffelleitung erreichbar unter {Path}", PathPrefix + key);
    }

    public string Key { get; }

    public bool IsValid(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), _keyBytes);
    }

    private static string LoadOrCreateKey(string path)
    {
        if (File.Exists(path))
        {
            var stored = File.ReadAllText(path).Trim();
            if (stored.Length >= MinKeyLength && IsUrlSafe(stored))
            {
                return stored;
            }
        }

        var created = Base64UrlEncode(RandomNumberGenerator.GetBytes(18));
        File.WriteAllText(path, created);
        return created;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool IsUrlSafe(string value) =>
        value.All(ch => ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');
}
