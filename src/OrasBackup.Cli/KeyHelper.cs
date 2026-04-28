using OrasBackup.Core.Config;
using OrasBackup.Core.Crypto;

namespace OrasBackup.Cli;

internal static class KeyHelper
{
    public static byte[] Resolve(string? password, string? keyFile, EncryptionConfig config)
    {
        if (!config.Enabled)
            return new byte[32]; // unused placeholder

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
            Console.Write("Encryption password: ");
            password = ReadPasswordMasked();
            if (string.IsNullOrEmpty(password))
                throw new InvalidOperationException("Password is required when encryption is enabled");
        }

        var encryptor = new AesEncryptor(config.Pbkdf2Iterations);
        var salt = GetOrCreateSalt();
        return encryptor.DeriveKey(password, salt);
    }

    private static byte[] GetOrCreateSalt()
    {
        var saltPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".orasbackup", "salt");
        if (File.Exists(saltPath))
            return File.ReadAllBytes(saltPath);

        var salt = new AesEncryptor().GenerateSalt();
        Directory.CreateDirectory(Path.GetDirectoryName(saltPath)!);
        File.WriteAllBytes(saltPath, salt);
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
