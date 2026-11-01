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

    // npm install of the pinned version into <data>\pi, without lifecycle scripts. Node.js 22.19+ must be on PATH.
    private async void PiInstall_Click(object sender, RoutedEventArgs e)
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
            var node = await Task.Run(() => Run("node", "--version"));
            if (node.Code != 0 || !LauncherLogic.NodeVersionOk(node.Output.Trim()))
                throw new InvalidOperationException(node.Code != 0 ? "Node.js was not found on PATH; install Node.js 22.19 or newer"
                    : $"Node.js {node.Output.Trim()} is too old; Pi needs 22.19 or newer");
            Directory.CreateDirectory(LauncherLogic.PiFolder);
            var npm = await Task.Run(() => Run("cmd.exe", "/d", "/c", "npm", "install", "--prefix", LauncherLogic.PiFolder, "--ignore-scripts",
                "--no-audit", "--no-fund", $"{LauncherLogic.PiPackage}@{LauncherLogic.PiVersion}"));
            if (npm.Code != 0 || !File.Exists(LauncherLogic.PiCli))
                throw new InvalidOperationException($"npm install failed (exit code {npm.Code}): {npm.Output.Trim().Split('\n').LastOrDefault()}");
            Log($"Pi {LauncherLogic.PiVersion} installed in {LauncherLogic.PiFolder}");
            Toast($"Pi {LauncherLogic.PiVersion} installed");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            Log($"Pi install failed: {exception.Message}");
            Toast("Pi install failed; see the log", "#FF667A");
        }
        finally
        {
            _piInstalling = false;
            RefreshPiStatus();
        }

        static (int Code, string Output) Run(string file, params string[] args)
        {
            var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args)
                info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output + error.Result);
        }
    }
}
