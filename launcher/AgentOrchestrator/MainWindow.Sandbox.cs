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
    private void AnswerApproval(JsonElement ev)
    {
        var (kind, detail, reason) = (Str(ev, "kind"), Str(ev, "detail"), Str(ev, "reason"));
        var (title, what, yes, no) = kind switch
        {
            "network" => ("Allow network access?", "The model wants to use the network (internet) for this command:", "Allow once", "Deny"),
            "git" => ("Keep this git repository?", "A command made a git repository that git could run programs from, as you:", "Keep it", "Quarantine it"),
            _ => ("Allow writing in other folders?", "The model wants to write in these folders for this command " +
                "(programs or settings kept there could later run with your rights):", "Allow once", "Deny"),
        };
        var after = kind == "git" ? "Quarantine renames its .git, so git ignores it; you can inspect it."
            : "Allow it once? The command still runs in the sandbox; only this access is added, for this command only.";
        var message = $"{what}\n\n{detail}" + (reason.Length > 0 ? $"\n\nReason given: {reason}" : "") + $"\n\n{after}";
        var allow = Choose(title, message, (yes, "allow"), (no, "deny")) == "allow";
        Log($"Sandbox: {(allow ? "allowed" : "denied")} {kind} for: {detail.Split('\n').LastOrDefault()}");
        try
        {
            _chatProcess?.StandardInput.WriteLine(JsonSerializer.Serialize(new { id = Str(ev, "id"), allow }));
            _chatProcess?.StandardInput.Flush();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException) { } // the turn already ended
    }

    private void InitSandbox()
    {
        SandboxNetworkToggle.IsChecked = _settings.SandboxNetwork;
        SandboxSetupStatus.Text = "Needed before commands run: hides your environment variables (API keys) from the sandbox. " +
            "Then, as administrator once, lets it list drive roots (PowerShell needs it) and read installed tools such as Node.js and Python.";
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
