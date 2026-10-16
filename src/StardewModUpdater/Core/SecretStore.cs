using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace StardewModUpdater.Core;

/// <summary>Where the Nexus key is kept. The best place each system offers is used, and the app says which.</summary>
internal interface ISecretStore
{
    /// <summary>A short phrase for the UI, such as "encrypted for your Windows account".</summary>
    string Description { get; }

    string? Read();

    void Write(string? value);
}

internal static class SecretStores
{
    public static ISecretStore Create(SettingsData data, Action save, string dataDir)
    {
        if (OperatingSystem.IsWindows())
            return new DpapiSecretStore(data, save);

        var file = new FileSecretStore(Path.Combine(dataDir, "nexus.key"));
        if (Platform.Which("secret-tool") is not null)
            return new KeyringSecretStore(file);
        return file;
    }
}

/// <summary>Windows: encrypted with DPAPI for the current user and stored in the settings file.</summary>
[SupportedOSPlatform("windows")]
internal sealed class DpapiSecretStore(SettingsData data, Action save) : ISecretStore
{
    public string Description => "encrypted so only your Windows account can read it";

    public string? Read()
    {
        var protectedText = data.NexusKeyProtected;
        if (string.IsNullOrEmpty(protectedText))
            return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedText), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    public void Write(string? value)
    {
        data.NexusKeyProtected = string.IsNullOrWhiteSpace(value)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser));
        save();
    }
}

/// <summary>Linux and macOS fallback: a file only the current user can read (mode 600), like most command-line tools use.</summary>
internal sealed class FileSecretStore(string path) : ISecretStore
{
    public string Description => "kept in a file only your user account can read";

    public string? Read()
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() is { Length: > 0 } t ? t : null : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // create the file private first, then write, so the key is never readable by others
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var fs = new FileStream(path, options))
        using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
            w.Write(value.Trim());
    }
}

/// <summary>Linux with libsecret installed: the desktop keyring (GNOME Keyring, KWallet) through the secret-tool command. Falls back to the file when no keyring is running.</summary>
internal sealed class KeyringSecretStore(FileSecretStore fallback) : ISecretStore
{
    private static readonly string[] Attributes = { "service", "stardew-mod-updater", "account", "nexus" };

    private bool _fileInUse;

    public string Description => _fileInUse ? fallback.Description : "stored in your desktop keyring";

    public string? Read()
    {
        var fromKeyring = Platform.Capture("secret-tool", new[] { "lookup" }.Concat(Attributes).ToArray())?.Trim();
        if (!string.IsNullOrEmpty(fromKeyring))
            return fromKeyring;
        var fromFile = fallback.Read();
        _fileInUse = fromFile is not null;
        return fromFile;
    }

    public void Write(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Platform.Capture("secret-tool", new[] { "clear" }.Concat(Attributes).ToArray());
            fallback.Write(null);
            _fileInUse = false;
            return;
        }

        var stored = Platform.Capture("secret-tool", new[] { "store", "--label=Stardew Mod Updater Nexus key" }.Concat(Attributes).ToArray(), stdin: value.Trim());
        if (stored is not null && Platform.Capture("secret-tool", new[] { "lookup" }.Concat(Attributes).ToArray())?.Trim() == value.Trim())
        {
            fallback.Write(null);
            _fileInUse = false;
            return;
        }

        // no keyring is running (for example over SSH or in a minimal desktop), so use the private file
        fallback.Write(value);
        _fileInUse = true;
    }
}
