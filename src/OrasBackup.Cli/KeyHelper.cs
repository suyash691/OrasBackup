using OrasBackup.Core.Config;
using OrasBackup.Core.Crypto;

namespace OrasBackup.Cli;

/// <summary>
/// Resolves the encryption key from key file, password argument, environment variable, or interactive prompt.
/// <para>
/// Security notes:
/// - Passwords are held in managed strings which cannot be zeroed and may persist in GC memory.
///   This is a .NET platform limitation. For higher security, use --key-file with a 32-byte key.
/// - When encryption is disabled, returns a zero-filled placeholder. All encryption code paths
///   check config.Enabled before using the key, so the placeholder is never used for encryption.
/// </para>
/// </summary>
internal static class KeyHelper
{
    public static byte[] Resolve(string? password, string? keyFile, EncryptionConfig config,
        Func<string>? passwordPrompt = null)
    {
        if (!config.Enabled)
            // Safe: all encryption call sites (BackupEngine, ChunkEngine, RestoreEngine) guard on
            // profile.Encryption.Enabled / the `encrypt` bool before calling Encrypt/EncryptFile.
            // This placeholder is never used for actual encryption — it just satisfies the byte[] signature.
            return new byte[32];

        // Key file takes priority
        if (!string.IsNullOrEmpty(keyFile))
        {
            var bytes = File.ReadAllBytes(keyFile);
            if (bytes.Length != 32)
                throw new InvalidOperationException($"Key file must be exactly 32 bytes, got {bytes.Length}");
            return bytes;
        }

        // Password from argument or environment
        password ??= Environment.GetEnvironmentVariable("ORASBACKUP_PASSWORD");
        if (string.IsNullOrEmpty(password))
        {
            password = (passwordPrompt ?? ReadPasswordMasked)();
            if (string.IsNullOrEmpty(password))
                throw new InvalidOperationException("Password is required when encryption is enabled");
        }

        var encryptor = new AesEncryptor(config.Pbkdf2Iterations);
        var salt = GetOrCreateSalt(config.ProfileName);
        return encryptor.DeriveKey(password, salt);
    }

    private static byte[] GetOrCreateSalt(string? profileName)
    {
        var name = string.IsNullOrEmpty(profileName) ? "default" : profileName;
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[\w\-]+$"))
            throw new ArgumentException($"Invalid profile name for salt: {name}");
        var saltPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".orasbackup", "salts", $"{name}.salt");
        if (File.Exists(saltPath))
            return File.ReadAllBytes(saltPath);

        var salt = new AesEncryptor().GenerateSalt();
        Directory.CreateDirectory(Path.GetDirectoryName(saltPath)!);
        File.WriteAllBytes(saltPath, salt);
        // Restrict permissions on salt file (defense-in-depth)
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(saltPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return salt;
    }

    private static string ReadPasswordMasked()
    {
        var password = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace && password.Length > 0)
                password.Length--;
            else if (key.Key != ConsoleKey.Backspace)
                password.Append(key.KeyChar);
        }
        Console.WriteLine();
        return password.ToString();
    }
}
