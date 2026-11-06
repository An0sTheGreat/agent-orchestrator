using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace AgentOrchestrator;

// The command sandbox (orchestrator.py run_command, sandbox.py): every model command runs in a Windows AppContainer the
// engine sets up. The launcher answers the engine's approval requests (a dialog; the answer goes back on the turn's stdin),
// keeps the network setting, and runs the setup: hiding HKCU\Environment from containers (as the user), then letting them
// list drive roots and read installed tools (Node.js, Python, ...) (as administrator).
public partial class MainWindow
{
    // An "approval" event: the model asked for more than the sandbox gives (network, writing in other folders), or a
    // command made a git repository whose hooks or config could run programs. No answer (closing the dialog) is a no.
    // Allow similar: per conversation (its id), the (kind, key) pairs the user allowed for the rest of this app session;
    // a matching request in that conversation is answered yes without asking. The engine's key is the command's program
    // (command, network) or the folders (write); a risky git repository is always asked about.
    private readonly Dictionary<string, HashSet<(string Kind, string Key)>> _similarAllowed = new();

    private void AnswerApproval(JsonElement ev)
    {
        var (kind, detail, reason, key) = (Str(ev, "kind"), Str(ev, "detail"), Str(ev, "reason"), Str(ev, "key"));
        var conversation = RunningId ?? "";
        var similar = kind != "git" && key.Length > 0;
        bool allow;
        if (similar && _similarAllowed.TryGetValue(conversation, out var rules) && rules.Contains((kind, key)))
        {
            allow = true;
            Log($"Sandbox: allowed {kind} for {key} (similar allowed earlier in this chat): {detail.Split('\n').LastOrDefault()}");
        }
        else
        {
            var (title, what, yes, no) = kind switch
            {
                "command" => ("Allow this command?", "The model wants to run this command (in the sandbox):", "Allow once", "Deny"),
                "network" => ("Allow network access?", "The model wants to use the network (internet) for this command:", "Allow once", "Deny"),
                "unsandboxed" => ("Run this command OUTSIDE the sandbox?", "The model wants to run this command as you, with your full rights and no sandbox " +
                    "(it can read and change anything you can):", "Run it", "Deny"),
                "git" => ("Keep this git repository?", "A command made a git repository that git could run programs from, as you:", "Keep it", "Quarantine it"),
                _ => ("Allow writing in other folders?", "The model wants to write in these folders for this command " +
                    "(programs or settings kept there could later run with your rights):", "Allow once", "Deny"),
            };
            var after = kind == "git" ? "Quarantine renames its .git, so git ignores it; you can inspect it."
                : kind == "unsandboxed" ? "Run it once? Only API keys and similar secret-named variables are left out of its environment."
                : "Allow it once? The command still runs in the sandbox; only this access is added, for this command only.";
            if (similar)
                after += "\n\nAllow similar commands for this session: " + (kind == "write" ? "writing in these folders" : $"{kind switch
                    { "network" => "network access for", _ => "running" }} \"{key}\" commands") + " is allowed in this chat without asking again, until the app closes.";
            var message = $"{what}\n\n{detail}" + (reason.Length > 0 ? $"\n\nReason given: {reason}" : "") + $"\n\n{after}";
            // the remote page may answer it in the conversation that asked (none running: the PC only)
            var answer = similar ? Choose(remote: RunningId, title, message, (yes, "allow"), ("Allow similar commands for this session", "similar"), (no, "deny"))
                : Choose(remote: RunningId, title, message, (yes, "allow"), (no, "deny"));
            if (answer == "similar")
            {
                if (!_similarAllowed.TryGetValue(conversation, out var set))
                    _similarAllowed[conversation] = set = new();
                set.Add((kind, key));
            }
            allow = answer is "allow" or "similar";
            Log($"Sandbox: {(allow ? "allowed" : "denied")} {kind}{(answer == "similar" ? $" (and similar: {key})" : "")} for: {detail.Split('\n').LastOrDefault()}");
        }
        // the process that asked: a chat/Code turn's, else a project run's (one of the two runs at a time)
        ToTurn(_chatProcess ?? _process, JsonSerializer.Serialize(new { id = Str(ev, "id"), allow }));
    }

    private void InitSandbox()
    {
        SandboxNetworkToggle.IsChecked = _settings.SandboxNetwork;
        FullAccessToggle.IsChecked = _settings.FullAccess;
        SandboxSetupStatus.Text = "Needed before commands run: hides your environment variables (API keys) from the sandbox. " +
            "Then, as administrator once, lets it list drive roots (PowerShell needs it) and read installed tools such as Node.js and Python.";
    }

    private void FullAccess_Click(object sender, RoutedEventArgs e)
    {
        _settings.FullAccess = FullAccessToggle.IsChecked == true;
        _settings.Save();
    }

    private void SandboxNetwork_Click(object sender, RoutedEventArgs e)
    {
        _settings.SandboxNetwork = SandboxNetworkToggle.IsChecked == true;
        _settings.Save();
    }

    // Two steps, each reporting to the log: closing HKCU\Environment runs as the user (an administrator's HKCU may be
    // another account's), then `sandbox.py --setup` elevated (a UAC prompt).
    private async void SandboxSetup_Click(object sender, RoutedEventArgs e)
    {
        if (_workflowRoot is null)
            return;
        SandboxSetupButton.IsEnabled = false;
        try
        {
            SandboxSetupStatus.Text = "Hiding your environment variables from sandboxed commands…";
            var (code, report) = await RunSandboxPy(elevated: false, "--protect-environment");
            Log($"Sandbox setup, environment variables (exit {code}):\n{report}");
            if (code != 0)
            {
                SandboxSetupStatus.Text = "Setup failed; see the log.";
                return;
            }
            SandboxSetupStatus.Text = "Waiting for administrator approval…";
            (code, report) = await RunSandboxPy(elevated: true, "--setup");
            Log($"Sandbox setup, drive roots and tools (exit {code}):\n{report}");
            SandboxSetupStatus.Text = code == 0 ? "Set up. Details are in the log."
                : "Environment variables are hidden, so commands can run; the administrator part failed (see the log), so they use cmd and may not find tools.";
        }
        catch (Exception x) when (x is System.ComponentModel.Win32Exception or IOException)
        {
            SandboxSetupStatus.Text = "Environment variables are hidden, so commands can run; the administrator part was cancelled, so they use cmd and may not find tools.";
            Log($"Sandbox setup not finished: {x.Message}");
        }
        finally
        {
            SandboxSetupButton.IsEnabled = true;
        }
    }

    private async Task<(int Code, string Report)> RunSandboxPy(bool elevated, string action)
    {
        var log = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-sandbox-setup-{Guid.NewGuid():N}.txt");
        var info = new ProcessStartInfo(LauncherLogic.FindPython()) { UseShellExecute = elevated, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (elevated)
            info.Verb = "runas";
        foreach (var arg in new[] { Path.Combine(_workflowRoot!, "sandbox.py"), action, "--log", log, "--state", LauncherLogic.SandboxHome })
            info.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(info)!;
            await process.WaitForExitAsync();
            return (process.ExitCode, File.Exists(log) ? File.ReadAllText(log).Trim() : "");
        }
        finally
        {
            try { File.Delete(log); } catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
        }
    }
}
