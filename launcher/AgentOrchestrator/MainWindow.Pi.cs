using System.Diagnostics;
using System.IO;
using System.Windows;

namespace AgentOrchestrator;

// Settings > Code engine: the built-in agent loop, or Pi (pi.dev) installed with npm into <data>\pi (LauncherLogic.PiFolder).
// The engine (orchestrator.py --code, request "engine": "pi") starts Pi itself; the launcher only installs it and says which.
public partial class MainWindow
{
    private bool _piInstalling;

    private bool PiEngine => _settings.CodeEngine == "pi";

    private void InitPi()
    {
        EnginePiRadio.IsChecked = PiEngine;
        EngineBuiltinRadio.IsChecked = !PiEngine;
        RefreshPiStatus();
    }

    // Refused while a reply or run is using the models (that turn already chose its engine).
    private void CodeEngine_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        var engine = EnginePiRadio.IsChecked == true ? "pi" : "";
        if (engine == _settings.CodeEngine)
            return;
        if (_process is not null || _chatBusy)
        {
            Toast("Wait for the reply or run to finish", "#F7C873");
            (PiEngine ? EnginePiRadio : EngineBuiltinRadio).IsChecked = true; // re-enters here with nothing to change
            return;
        }
        _settings.CodeEngine = engine;
        _settings.Save();
        RefreshPiStatus();
        RefreshPromptUsage();
    }

    private void RefreshPiStatus()
    {
        var version = LauncherLogic.InstalledPiVersion();
        PiStatus.Text = _piInstalling ? "Installing Pi…" : version is null ? (PiEngine ? "Pi not installed: Code turns are refused until it is" : "Pi not installed")
            : version == LauncherLogic.PiVersion ? $"Pi {version} installed" : $"Pi {version} installed ({LauncherLogic.PiVersion} is the tested version)";
        PiInstallButton.Content = version is null ? "Install Pi" : "Reinstall Pi";
        PiInstallButton.IsEnabled = !_piInstalling;
    }

    // False, with a toast, when the Pi engine is chosen but Pi is not installed (or is being installed).
    private bool CodeEngineReady()
    {
        if (!PiEngine)
            return true;
        if (!_piInstalling && File.Exists(LauncherLogic.PiCli))
            return true;
        Toast(_piInstalling ? "Pi is being installed" : "Install Pi in Settings first, or choose the Built-in Code engine", "#F7C873");
        return false;
    }

    // npm install of the pinned version into <data>\pi, without lifecycle scripts. Node.js 22.19+ must be on PATH: when it
    // is missing or too old, a popup offers to install Node.js LTS with winget first.
    private async void PiInstall_Click(object sender, RoutedEventArgs e) => await PiInstallAsync();

    internal async Task PiInstallAsync()
    {
        if (_piInstalling)
            return;
        if (_process is not null || _chatBusy)
        {
            Toast("Wait for the reply or run to finish", "#F7C873");
            return;
        }
        _piInstalling = true;
        RefreshPiStatus();
        try
        {
            await EnsureNodeAsync();
            Directory.CreateDirectory(LauncherLogic.PiFolder);
            var npm = await Task.Run(() => Run("cmd.exe", "/d", "/c", "npm", "install", "--prefix", LauncherLogic.PiFolder, "--ignore-scripts",
                "--no-audit", "--no-fund", $"{LauncherLogic.PiPackage}@{LauncherLogic.PiVersion}"));
            if (npm.Code != 0 || !File.Exists(LauncherLogic.PiCli))
                throw new InvalidOperationException($"npm install failed (exit code {npm.Code}): {npm.Output.Trim().Split('\n').LastOrDefault()}");
            Log($"Pi {LauncherLogic.PiVersion} installed in {LauncherLogic.PiFolder}");
            Toast($"Pi {LauncherLogic.PiVersion} installed");
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Log($"Pi install failed: {exception.Message}");
            Toast("Pi install failed; see the log", "#FF667A");
        }
        finally
        {
            _piInstalling = false;
            RefreshPiStatus();
        }
    }

    // Throws unless node on PATH is 22.19+. If it is not, asks to install Node.js LTS with winget (Windows asks for admin
    // rights), then re-reads PATH from the registry so this process and the engines it starts find the new node.
    private async Task EnsureNodeAsync()
    {
        var version = await Task.Run(NodeVersion);
        if (LauncherLogic.NodeVersionOk(version))
            return;
        var problem = version == "" ? "Node.js is not installed (node was not found on PATH)" : $"Node.js {version} is too old";
        if (SidebarAsk("Install Node.js", $"{problem}. Pi needs Node.js 22.19 or newer.\n\nInstall Node.js LTS now with winget? " +
                "This accepts the Node.js license, and Windows may ask for permission.", "Install") is null)
            throw new InvalidOperationException($"{problem}; install Node.js 22.19 or newer");
        PiStatus.Text = "Installing Node.js…";
        Log("Installing Node.js LTS with winget");
        var winget = await Task.Run(() => Run("winget", "install", "--id", "OpenJS.NodeJS.LTS", "--exact", "--silent",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"));
        if (winget.Code == -1)
            throw new InvalidOperationException("winget is not available; install Node.js 22.19 or newer from https://nodejs.org");
        Environment.SetEnvironmentVariable("PATH", LauncherLogic.MergePath(Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine), Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)));
        version = await Task.Run(NodeVersion);
        if (!LauncherLogic.NodeVersionOk(version))
            throw new InvalidOperationException($"Node.js install failed (winget exit code {winget.Code}: " +
                $"{winget.Output.Trim().Split('\n').LastOrDefault()?.Trim()}); node reports \"{version}\"");
        Log($"Node.js {version} installed");
        PiStatus.Text = "Installing Pi…";

        static string NodeVersion() => Run("node", "--version") is (0, var output) ? output.Trim() : "";
    }

    // Exit code -1 when the program is not found.
    private static (int Code, string Output) Run(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(info)!;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output + error.Result);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, $"{file} was not found");
        }
    }
}
