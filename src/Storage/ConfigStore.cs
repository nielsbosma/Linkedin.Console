namespace Linkedin.Console.Storage;

/// <summary>Where the secret store keeps its files, and how anything private gets written to disk.</summary>
public static class ConfigStore
{
    public static string ConfigDir
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("LINKEDIN_CONFIG_DIR");
            if (!string.IsNullOrWhiteSpace(overridden)) return overridden;

            if (OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "linkedin-cli");

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (OperatingSystem.IsMacOS())
                return Path.Combine(home, "Library", "Application Support", "linkedin-cli");

            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            return Path.Combine(string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".config") : xdg, "linkedin-cli");
        }
    }

    public static void EnsureDir() => EnsurePrivateDir(ConfigDir);

    /// <summary>Exports hold messages and phone numbers, so they get the same treatment as the config.</summary>
    public static void EnsurePrivateDir(string dir)
    {
        if (Directory.Exists(dir)) return;
        Directory.CreateDirectory(dir);
        RestrictToOwner(dir);
    }

    /// <summary>Write to a temp file in the same directory, then replace - never a partial file.</summary>
    public static void AtomicWrite(string path, string contents)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        RestrictToOwner(temp);
        File.Move(temp, path, overwrite: true);
    }

    public static void AtomicWrite(string path, byte[] contents)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, contents);
        RestrictToOwner(temp);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>0600 on Unix. On Windows files inherit the user profile's or the chosen folder's ACL.</summary>
    public static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var mode = Directory.Exists(path)
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception)
        {
            // Best effort - a filesystem that cannot express permissions is not a reason to fail.
        }
    }
}
