using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentOrchestrator;

// API keys typed into the app (Remote API page): kept per variable name, several per variable under names the user gives
// them (one of them active), encrypted for this Windows user (DPAPI) in <data>\keys.json, and the active one put in this
// process's environment under the variable's name, so the engine it starts, and the page's own requests, read it as they
// read any key variable. Nothing is written to the user's own environment, and a key is only ever sent where the page
// already sends it (the selected service's host). Names are shown in the app; keys never are.
public static class ApiKeys
{
    private static string FilePath => Path.Combine(AppSettings.DataRoot, "keys.json");

    // keys.json: {"version":2,"vars":{"NAME":{"active":"Personal","keys":{"Personal":"<b64>","Work":"<b64>"}}}}.
    // Version 1 was {"NAME":"<b64>"}: read as one key named Default.
    private sealed class Var
    {
        public string Active { get; set; } = "";
        public Dictionary<string, string> Keys { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Only names that look like a secret: they are then hidden from sandboxed commands and removed from command output
    // (sandbox.py SECRET_ENV), and a typed name cannot overwrite PATH or another setting.
    public static bool ValidName(string? name) =>
        name is { Length: > 0 and <= 80 } && Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$") && Regex.IsMatch(name, "KEY|TOKEN|SECRET|PASSWORD", RegexOptions.IgnoreCase);

    // A key's name: trimmed, 1-40 characters, no control characters.
    private static string Label(string? label)
    {
        label = label?.Trim() ?? "";
        if (label.Length is 0 or > 40 || label.Any(char.IsControl))
            throw new ArgumentException("A key's name must be 1 to 40 characters.");
        return label;
    }

    private static Dictionary<string, Var> Load()
    {
        try
        {
            if (!File.Exists(FilePath) || JsonNode.Parse(File.ReadAllText(FilePath)) is not JsonObject root)
                return new();
            if (root["vars"] is JsonObject vars)
                return vars.ToDictionary(v => v.Key, v => v.Value.Deserialize<Var>(Json) is { Active: not null, Keys: not null } ok ? ok : throw new JsonException());
            return root.ToDictionary(v => v.Key, v => new Var { Active = "Default", Keys = { ["Default"] = v.Value?.GetValue<string>() ?? throw new JsonException() } });
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return new(); // damaged: no keys
        }
    }

    private static void Write(Dictionary<string, Var> vars)
    {
        Directory.CreateDirectory(AppSettings.DataRoot);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new { version = 2, vars }, Json));
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

    private static string? ActiveKey(Var? v) => v is not null && v.Keys.TryGetValue(v.Active, out var stored) ? Decrypt(stored) : null;

    // The variable gets its active key, or else the user's own environment value, if it has one.
    private static void Apply(string name, Var? v) =>
        Environment.SetEnvironmentVariable(name, ActiveKey(v) is { Length: > 0 } key ? key : Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User));

    public static IReadOnlyList<string> Labels(string name) =>
        Load().TryGetValue(name, out var v) ? [.. v.Keys.Keys.Order(StringComparer.OrdinalIgnoreCase)] : [];

    public static string? Active(string name) => Load().TryGetValue(name, out var v) && v.Keys.ContainsKey(v.Active) ? v.Active : null;

    public static bool Has(string name) => Active(name) is not null;

    // At startup: every variable's active key into the process's environment (a saved key wins over a variable of the same name).
    public static void ApplyAll()
    {
        foreach (var (name, v) in Load())
            if (ValidName(name) && ActiveKey(v) is { Length: > 0 } key)
                Environment.SetEnvironmentVariable(name, key);
    }

    // Adds the key under that name (or replaces the key of that name) and makes it the active one.
    public static void Save(string name, string label, string key)
    {
        if (!ValidName(name))
            throw new ArgumentException("The variable name must contain KEY, TOKEN, SECRET or PASSWORD, e.g. OPENROUTER_API_KEY.");
        label = Label(label);
        key = key.Trim();
        if (key.Length == 0)
            throw new ArgumentException("The key is empty.");
        var vars = Load();
        var v = vars.TryGetValue(name, out var had) ? had : vars[name] = new Var();
        v.Keys[label] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
        v.Active = label;
        Write(vars);
        Environment.SetEnvironmentVariable(name, key);
    }

    private static Var Saved(Dictionary<string, Var> vars, string name, string label) =>
        vars.TryGetValue(name, out var v) && v.Keys.ContainsKey(label) ? v : throw new ArgumentException($"No key named \"{label}\" is saved for {name}.");

    // Makes that key the active one: the variable gets its value.
    public static void Select(string name, string label)
    {
        var vars = Load();
        var v = Saved(vars, name, label);
        v.Active = label;
        Write(vars);
        Apply(name, v);
    }

    public static void Rename(string name, string label, string newLabel)
    {
        var vars = Load();
        var v = Saved(vars, name, label);
        newLabel = Label(newLabel);
        if (newLabel == label)
            return;
        if (v.Keys.ContainsKey(newLabel))
            throw new ArgumentException($"A key named \"{newLabel}\" is already saved for {name}.");
        v.Keys[newLabel] = v.Keys[label];
        v.Keys.Remove(label);
        if (v.Active == label)
            v.Active = newLabel;
        Write(vars);
    }

    // Forgets that key. If it was the active one another saved key takes over; with none left the variable falls back to
    // the user's own environment, if it has one.
    public static void Remove(string name, string label)
    {
        var vars = Load();
        if (vars.TryGetValue(name, out var v) && v.Keys.Remove(label))
        {
            if (v.Keys.Count == 0)
                vars.Remove(name);
            else if (!v.Keys.ContainsKey(v.Active))
                v.Active = v.Keys.Keys.Order(StringComparer.OrdinalIgnoreCase).First();
            Write(vars);
        }
        Apply(name, vars.GetValueOrDefault(name));
    }

    public static void SelfTest()
    {
        const string Name = "AO_SELFTEST_API_KEY";
        var (dataBefore, keyBefore) = (Environment.GetEnvironmentVariable("AGENT_ORCHESTRATOR_DATA"), Environment.GetEnvironmentVariable(Name));
        var area = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-keys-{Guid.NewGuid():N}");
        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException(what);
        }
        static string? Env() => Environment.GetEnvironmentVariable(Name);
        try
        {
            Environment.SetEnvironmentVariable("AGENT_ORCHESTRATOR_DATA", area);
            Check(!ValidName("PATH") && !ValidName("") && !ValidName("1_KEY") && !ValidName("A KEY") && ValidName("OPENROUTER_API_KEY") && ValidName("my_token"),
                "API key variable names failed.");
            foreach (var (name, label, key) in new[] { ("PATH", "Default", "x"), (Name, "Default", "  "), (Name, " ", "x"), (Name, new string('n', 41), "x"), (Name, "a\nb", "x") })
                try { Save(name, label, key); throw new InvalidOperationException("A bad key was saved."); } catch (ArgumentException) { }

            // A version 1 file: one key per variable, read as a key named Default and active.
            Directory.CreateDirectory(area);
            var legacy = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes("sk-legacy"), null, DataProtectionScope.CurrentUser));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Dictionary<string, string> { [Name] = legacy }));
            ApplyAll();
            Check(Has(Name) && Active(Name) == "Default" && Labels(Name).SequenceEqual(["Default"]) && Env() == "sk-legacy", "An old key file was not read.");

            // Two named keys: saving one makes it active; switching changes the variable; a restart applies the active one.
            Save(Name, " Personal ", "  sk-test-value-123 ");
            Save(Name, "Work", "sk-work-456");
            var file = File.ReadAllText(FilePath);
            Check(!file.Contains("sk-test-value-123") && !file.Contains("sk-work-456") && !file.Contains("sk-legacy") && file.Contains("\"version\":2"),
                "A key was written in plain text.");
            Check(Labels(Name).SequenceEqual(["Default", "Personal", "Work"]) && Active(Name) == "Work" && Env() == "sk-work-456", "Saving a second key failed.");
            Select(Name, "Personal");
            Check(Active(Name) == "Personal" && Env() == "sk-test-value-123", "Switching keys did not change the variable.");
            Environment.SetEnvironmentVariable(Name, null);
            ApplyAll(); // a restart
            Check(Env() == "sk-test-value-123", "The active key did not come back.");
            try { Select(Name, "Nope"); throw new InvalidOperationException("A missing key was selected."); } catch (ArgumentException) { }

            // Rename: the active one stays active; a name in use is refused.
            Rename(Name, "Personal", " Home ");
            Check(Active(Name) == "Home" && Labels(Name).SequenceEqual(["Default", "Home", "Work"]) && Env() == "sk-test-value-123", "Renaming a key failed.");
            try { Rename(Name, "Home", "Work"); throw new InvalidOperationException("A key was renamed to a name in use."); } catch (ArgumentException) { }
            Check(Labels(Name).SequenceEqual(["Default", "Home", "Work"]), "A refused rename changed the keys.");

            // Remove: the active one gives way to another; the last one leaves the variable to the user's own environment.
            Remove(Name, "Home");
            Check(Has(Name) && Active(Name) == "Default" && Env() == "sk-legacy", "Removing the active key left it active.");
            Remove(Name, "Default");
            Check(Active(Name) == "Work" && Env() == "sk-work-456", "Removing the active key did not switch to the other.");
            Remove(Name, "Work");
            Check(!Has(Name) && Labels(Name).Count == 0 && Env() is null, "Removing the last key failed.");

            File.WriteAllText(FilePath, "{ not json"); // a damaged file is no keys, not a crash
            Check(!Has(Name) && Labels(Name).Count == 0, "A damaged key file failed.");
            File.WriteAllText(FilePath, "{\"version\":2,\"vars\":{\"" + Name + "\":7}}");
            Check(!Has(Name), "A damaged key file failed.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_ORCHESTRATOR_DATA", dataBefore);
            Environment.SetEnvironmentVariable(Name, keyBefore);
            try { Directory.Delete(area, true); } catch (IOException) { }
        }
    }
}
