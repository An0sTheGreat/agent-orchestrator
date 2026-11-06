using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOrchestrator;

// API keys typed into the app (Remote API page): kept per variable name, encrypted for this Windows user (DPAPI) in
// <data>\keys.json, and put in this process's environment under that name, so the engine it starts, and the page's own
// requests, read them as they read any key variable. Nothing is written to the user's own environment, and a key is only
// ever sent where the page already sends it (the selected service's host).
public static class ApiKeys
{
    private static string FilePath => Path.Combine(AppSettings.DataRoot, "keys.json");

    // Only names that look like a secret: they are then hidden from sandboxed commands and removed from command output
    // (sandbox.py SECRET_ENV), and a typed name cannot overwrite PATH or another setting.
    public static bool ValidName(string? name) =>
        name is { Length: > 0 and <= 80 } && Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$") && Regex.IsMatch(name, "KEY|TOKEN|SECRET|PASSWORD", RegexOptions.IgnoreCase);

    private static Dictionary<string, string> Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    private static void Write(Dictionary<string, string> keys)
    {
        Directory.CreateDirectory(AppSettings.DataRoot);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(keys));
        File.Move(temp, FilePath, overwrite: true);
    }

    private static string? Decrypt(string stored)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception x) when (x is CryptographicException or FormatException)
        {
            return null; // another Windows user's, or damaged: ignored
        }
    }

    public static bool Has(string name) => Load().ContainsKey(name);

    // At startup: every saved key into the process's environment (a saved key wins over a variable of the same name).
    public static void ApplyAll()
    {
        foreach (var (name, stored) in Load())
            if (ValidName(name) && Decrypt(stored) is { Length: > 0 } key)
                Environment.SetEnvironmentVariable(name, key);
    }

    public static void Save(string name, string key)
    {
        if (!ValidName(name))
            throw new ArgumentException("The variable name must contain KEY, TOKEN, SECRET or PASSWORD, e.g. OPENROUTER_API_KEY.");
        key = key.Trim();
        if (key.Length == 0)
            throw new ArgumentException("The key is empty.");
        var keys = Load();
        keys[name] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
        Write(keys);
        Environment.SetEnvironmentVariable(name, key);
    }

    // Forgets the saved key; the variable falls back to the user's own environment, if it has one.
    public static void Remove(string name)
    {
        var keys = Load();
        if (keys.Remove(name))
            Write(keys);
        Environment.SetEnvironmentVariable(name, Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User));
    }

    public static void SelfTest()
    {
        var (dataBefore, keyBefore) = (Environment.GetEnvironmentVariable("AGENT_ORCHESTRATOR_DATA"), Environment.GetEnvironmentVariable("AO_SELFTEST_API_KEY"));
        var area = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-keys-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_ORCHESTRATOR_DATA", area);
            if (ValidName("PATH") || ValidName("") || ValidName("1_KEY") || ValidName("A KEY") || !ValidName("OPENROUTER_API_KEY") || !ValidName("my_token"))
                throw new InvalidOperationException("API key variable names failed.");
            foreach (var bad in new[] { ("PATH", "x"), ("AO_SELFTEST_API_KEY", "  ") })
                try { Save(bad.Item1, bad.Item2); throw new InvalidOperationException("A bad key was saved."); } catch (ArgumentException) { }
            Save("AO_SELFTEST_API_KEY", "  sk-test-value-123 ");
            if (File.ReadAllText(FilePath).Contains("sk-test-value-123") || !Has("AO_SELFTEST_API_KEY") ||
                Environment.GetEnvironmentVariable("AO_SELFTEST_API_KEY") != "sk-test-value-123")
                throw new InvalidOperationException("Saving an API key failed.");
            Environment.SetEnvironmentVariable("AO_SELFTEST_API_KEY", null);
            ApplyAll(); // a restart
            if (Environment.GetEnvironmentVariable("AO_SELFTEST_API_KEY") != "sk-test-value-123")
                throw new InvalidOperationException("A saved API key did not come back.");
            File.WriteAllText(FilePath, "{ not json"); // a damaged file is no keys, not a crash
            if (Has("AO_SELFTEST_API_KEY"))
                throw new InvalidOperationException("A damaged key file failed.");
            Save("AO_SELFTEST_API_KEY", "second");
            Remove("AO_SELFTEST_API_KEY");
            if (Has("AO_SELFTEST_API_KEY") || Environment.GetEnvironmentVariable("AO_SELFTEST_API_KEY") is not null)
                throw new InvalidOperationException("Removing an API key failed.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_ORCHESTRATOR_DATA", dataBefore);
            Environment.SetEnvironmentVariable("AO_SELFTEST_API_KEY", keyBefore);
            try { Directory.Delete(area, true); } catch (IOException) { }
        }
    }
}
