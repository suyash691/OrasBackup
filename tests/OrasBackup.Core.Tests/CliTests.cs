using OrasBackup.Cli;
using OrasBackup.Core.Config;
using Xunit;

namespace OrasBackup.Core.Tests;

public class FileProfileStoreTests : IDisposable
{
    private readonly string _profileDir = Path.Combine(Path.GetTempPath(), $"profiles-{Guid.NewGuid():N}");
    private readonly FileProfileStore _sut;

    public FileProfileStoreTests() => _sut = new FileProfileStore(_profileDir);
    public void Dispose() { try { Directory.Delete(_profileDir, true); } catch { } }

    [Fact]
    public void SaveLoad_RoundTrip()
    {
        var profile = new BackupProfile
        {
            Name = "test",
            SourcePaths = ["/data", "/photos"],
            Registry = "ghcr.io/user/backups",
            Encryption = new EncryptionConfig { Enabled = true, Pbkdf2Iterations = 100_000 }
        };
        _sut.Save(profile);
        var loaded = _sut.Load("test");

        Assert.Equal("test", loaded.Name);
        Assert.Equal(2, loaded.SourcePaths.Count);
        Assert.Equal("ghcr.io/user/backups", loaded.Registry);
        Assert.True(loaded.Encryption.Enabled);
    }

    [Fact]
    public void Load_SetsProfileNameOnEncryptionConfig()
    {
        _sut.Save(new BackupProfile { Name = "myprof", Registry = "reg" });
        var loaded = _sut.Load("myprof");
        Assert.Equal("myprof", loaded.Encryption.ProfileName);
    }

    [Fact]
    public void Load_MissingProfile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => _sut.Load("nonexistent"));
    }

    [Fact]
    public void ListProfiles_ReturnsNames()
    {
        _sut.Save(new BackupProfile { Name = "alpha", Registry = "r" });
        _sut.Save(new BackupProfile { Name = "beta", Registry = "r" });

        var profiles = _sut.ListProfiles().ToList();
        Assert.Contains("alpha", profiles);
        Assert.Contains("beta", profiles);
    }

    [Fact]
    public void ListProfiles_EmptyDir_ReturnsEmpty()
    {
        Assert.Empty(_sut.ListProfiles());
    }

    [Fact]
    public void GetProfilePath_ReturnsExpectedPath()
    {
        var path = _sut.GetProfilePath("test");
        Assert.EndsWith("test.json", path);
    }

    [Fact]
    public void InvalidProfileName_Throws()
    {
        Assert.Throws<ArgumentException>(() => _sut.GetProfilePath("../../evil"));
        Assert.Throws<ArgumentException>(() => _sut.Load("../hack"));
    }
}

public class KeyHelperTests
{
    [Fact]
    public void Resolve_EncryptionDisabled_ReturnsPlaceholder()
    {
        var key = KeyHelper.Resolve(null, null, new EncryptionConfig { Enabled = false });
        Assert.Equal(32, key.Length);
    }

    [Fact]
    public void Resolve_KeyFile_ReadsFile()
    {
        var keyFile = Path.Combine(Path.GetTempPath(), $"key-{Guid.NewGuid():N}.bin");
        try
        {
            var keyBytes = new byte[32];
            Random.Shared.NextBytes(keyBytes);
            File.WriteAllBytes(keyFile, keyBytes);

            var key = KeyHelper.Resolve(null, keyFile, new EncryptionConfig { Enabled = true });
            Assert.Equal(keyBytes, key);
        }
        finally { File.Delete(keyFile); }
    }

    [Fact]
    public void Resolve_KeyFileWrongSize_Throws()
    {
        var keyFile = Path.Combine(Path.GetTempPath(), $"key-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(keyFile, new byte[16]); // wrong size
            Assert.Throws<InvalidOperationException>(() =>
                KeyHelper.Resolve(null, keyFile, new EncryptionConfig { Enabled = true }));
        }
        finally { File.Delete(keyFile); }
    }

    [Fact]
    public void Resolve_PasswordFromEnv_DerivesKey()
    {
        var prev = Environment.GetEnvironmentVariable("ORASBACKUP_PASSWORD");
        try
        {
            Environment.SetEnvironmentVariable("ORASBACKUP_PASSWORD", "test-password-123");
            var key = KeyHelper.Resolve(null, null, new EncryptionConfig { Enabled = true, Pbkdf2Iterations = 1000, ProfileName = "envtest" });
            Assert.Equal(32, key.Length);
            Assert.NotEqual(new byte[32], key); // not all zeros
        }
        finally { Environment.SetEnvironmentVariable("ORASBACKUP_PASSWORD", prev); }
    }

    [Fact]
    public void Resolve_PasswordArg_DerivesKey()
    {
        var key = KeyHelper.Resolve("my-password", null, new EncryptionConfig { Enabled = true, Pbkdf2Iterations = 1000, ProfileName = "argtest" });
        Assert.Equal(32, key.Length);
    }

    [Fact]
    public void Resolve_PromptReturnsPassword_DerivesKey()
    {
        var key = KeyHelper.Resolve(null, null,
            new EncryptionConfig { Enabled = true, Pbkdf2Iterations = 1000, ProfileName = "prompttest" },
            passwordPrompt: () => "prompted-password");
        Assert.Equal(32, key.Length);
        Assert.NotEqual(new byte[32], key);
    }

    [Fact]
    public void Resolve_PromptReturnsEmpty_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            KeyHelper.Resolve(null, null,
                new EncryptionConfig { Enabled = true, Pbkdf2Iterations = 1000, ProfileName = "emptytest" },
                passwordPrompt: () => ""));
    }

    [Fact]
    public void Resolve_NoPasswordNoEnv_UsesPrompt()
    {
        var prev = Environment.GetEnvironmentVariable("ORASBACKUP_PASSWORD");
        try
        {
            Environment.SetEnvironmentVariable("ORASBACKUP_PASSWORD", null);
            // Use the injectable prompt to simulate user input
            var key = KeyHelper.Resolve(null, null,
                new EncryptionConfig { Enabled = true, Pbkdf2Iterations = 1000, ProfileName = "promptcov" },
                passwordPrompt: () => "from-prompt");
            Assert.Equal(32, key.Length);
            Assert.NotEqual(new byte[32], key);
        }
        finally { Environment.SetEnvironmentVariable("ORASBACKUP_PASSWORD", prev); }
    }

    [Fact]
    public void Resolve_NoPasswordNoEnv_EmptyPrompt_Throws()
    {
        var prev = Environment.GetEnvironmentVariable("ORASBACKUP_PASSWORD");
        try
        {
            Environment.SetEnvironmentVariable("ORASBACKUP_PASSWORD", null);
            Assert.Throws<InvalidOperationException>(() =>
                KeyHelper.Resolve(null, null,
                    new EncryptionConfig { Enabled = true, Pbkdf2Iterations = 1000, ProfileName = "emptyprompt" },
                    passwordPrompt: () => ""));
        }
        finally { Environment.SetEnvironmentVariable("ORASBACKUP_PASSWORD", prev); }
    }

    [Fact]
    public void Resolve_InvalidProfileName_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            KeyHelper.Resolve("password", null,
                new EncryptionConfig { Enabled = true, Pbkdf2Iterations = 1000, ProfileName = "../../evil" }));
    }

    [Fact]
    public void Resolve_CreatesSaltFile_WhenNotExists()
    {
        // Use a unique profile name that won't have a salt file yet
        var uniqueName = $"salttest-{Guid.NewGuid():N}"[..20];
        var key = KeyHelper.Resolve("testpass", null,
            new EncryptionConfig { Enabled = true, Pbkdf2Iterations = 1000, ProfileName = uniqueName });
        Assert.Equal(32, key.Length);

        // Verify salt file was created
        var saltPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".orasbackup", "salts", $"{uniqueName}.salt");
        Assert.True(File.Exists(saltPath));
        File.Delete(saltPath); // cleanup
    }
}
