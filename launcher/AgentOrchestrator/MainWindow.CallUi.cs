using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AgentOrchestrator;

// Call mode's face in the sidebar: the call button above the profile row (disabled with CallReady's reason), the mute toggle
// beside it during a call, and the visualizer with its state text, which grows in above the button (250 ms, scale from 0.85 and
// fade, the list giving room) and leaves the same way.
public partial class MainWindow
{
    private const double CallPanelHeight = 216; // the visualizer (185), the state text and their margins

    private void InitCallUi()
    {
        CallStateChanged += ShowCallState;
        CallLevels += CallVisual.Feed;
        Activated += (_, _) => RefreshCallButton(); // the models may have been downloaded meanwhile
        RefreshCallButton();
    }

    private void RefreshCallButton()
    {
        var ready = CallReady(out var reason);
        CallButton.IsEnabled = InCall || ready;
        var tip = InCall ? "End the call" : ready ? "Start a call" : reason;
        CallButton.ToolTip = tip;
        AutomationProperties.SetName(CallButton, InCall ? "End the call" : "Start a call");
    }

    private void CallButton_Click(object sender, RoutedEventArgs e)
    {
        if (InCall)
            EndCall();
        else
            StartCall();
    }

    private void CallMute_Click(object sender, RoutedEventArgs e) => ToggleCallMute();

    private void CallVisual_MouseUp(object sender, MouseButtonEventArgs e) => InterruptCall();

    // Space or Esc in a call interrupts the speech (not while typing: a text box keeps its keys).
    private bool CallKey(KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Escape) || Keyboard.Modifiers != ModifierKeys.None || CallNow is not (CallState.Speaking or CallState.Thinking)
            || Keyboard.FocusedElement is TextBoxBase or PasswordBox or ICSharpCode.AvalonEdit.Editing.TextArea)
            return false;
        InterruptCall();
        return true;
    }

    private void ShowCallState(CallState state)
    {
        CallVisual.State = state;
        CallStateText.Text = state switch
        {
            CallState.Thinking when _callPreparing => "Getting the voice ready…", // the phrase waits for the warm-up (CallWhenReady)
            CallState.Thinking => "Thinking…",
            CallState.Speaking => "Speaking",
            CallState.Paused => "Paused",
            CallState.Listening when _callPreparing => "Getting ready…", // the call's model and voice (PrepareCallAsync); speaking is already heard
            _ => "Listening…",
        };
        CallButton.Tag = state == CallState.Idle ? "" : "on";
        CallGlyph.Text = state == CallState.Idle ? "" : ""; // phone, hang up
        CallMuteButton.Visibility = state == CallState.Idle ? Visibility.Collapsed : Visibility.Visible;
        var muted = _callMuted;
        CallMuteGlyph.Text = muted ? "" : ""; // mic off, mic
        CallMuteButton.ToolTip = muted ? "Unmute the microphone" : "Mute the microphone";
        AutomationProperties.SetName(CallMuteButton, (string)CallMuteButton.ToolTip);
        RefreshCallButton();
        ShowCallPanel(state != CallState.Idle);
    }

    private bool _callPanelShown;

    private void ShowCallPanel(bool show)
    {
        if (show == _callPanelShown)
            return;
        _callPanelShown = show;
        var ease = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn };
        var time = TimeSpan.FromMilliseconds(250);
        DoubleAnimation To(double value) => new(value, time) { EasingFunction = ease };
        if (show)
            CallPanel.Visibility = Visibility.Visible;
        var height = To(show ? CallPanelHeight : 0);
        if (!show)
            height.Completed += (_, _) =>
            {
                if (!_callPanelShown)
                    CallPanel.Visibility = Visibility.Collapsed; // stops the visualizer's frames
            };
        CallPanel.BeginAnimation(HeightProperty, height);
        CallPanel.BeginAnimation(OpacityProperty, To(show ? 1 : 0));
        CallPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, To(show ? 1 : 0.85));
        CallPanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, To(show ? 1 : 0.85));
    }

    // --ui-smoke: the button above the profile row, disabled with the reason without the models; in a call the visualizer shows
    // at ~185 px and draws frames, the state text follows the state, Space interrupts, the mute toggle mutes, a voice that starts
    // while the model speaks interrupts it when Interrupt by talking is on, End hides it all after the animation, opening another
    // chat ends a call, and the microphone failing mid-call ends it. Stubs as in CallSmoke: never the real mic, GPU or a model.
    private async Task<bool> CallUiSmoke()
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: call ui: {what}"); return false; }
        async Task<bool> Until(Func<bool> done)
        {
            for (var i = 0; i < 100 && !done(); i++)
                await Task.Delay(40);
            return done();
        }
        var (player, test, bargeIn) = (_speechPlayer, _speechTest, _settings.Tts.CallBargeIn);
        Action<float[]>? feed = null;
        var reply = "";
        var spoken = new List<string>();
        string[] Spoken() { lock (spoken) return spoken.ToArray(); }
        float[] Stub(string text, VoiceChoice voice, float speed, CancellationToken ct)
        {
            lock (spoken)
                spoken.Add(text);
            if (text.StartsWith("Slow"))
                ct.WaitHandle.WaitOne(10_000);
            return Enumerable.Repeat(0.3f, Tts.Rate / 2).ToArray();
        }
        void Phrase()
        {
            for (var i = 0; i < 10; i++)
                feed!(Enumerable.Repeat(0.5f, 1600).ToArray());
            for (var i = 0; i < 8; i++)
                feed!(new float[1600]);
        }
        string Ev(string json) => LauncherLogic.EventPrefix + json;
        try
        {
            ShowView(chat: true);
            if (CallButton.Parent is not Grid row || row.Parent is not StackPanel bottom || ProfileButton.Parent is not DockPanel profile
                || bottom.Children.IndexOf(row) >= bottom.Children.IndexOf(profile) || bottom.Children.IndexOf(CallPanel) >= bottom.Children.IndexOf(row))
                return Fail("the call button is not right above the profile row, or the visualizer not above it");
            RefreshCallButton();
            if (CallReady(out var why) || CallButton.IsEnabled || CallButton.ToolTip as string != why || !why.Contains("Settings > Voice"))
                return Fail($"without the models: enabled {CallButton.IsEnabled}, tooltip {CallButton.ToolTip}");
            _dictationFeed = f => feed = f;
            _dictationTranscribe = _ => "Hello there";
            _speechPlayer = new SpeechPlayer(Stub, () => new Tts.FakeOutput());
            _speechTest = true;
            _callTurnStub = text =>
            {
                StartChatTurn(text);
                foreach (var word in reply.Split(' '))
                    _lines.Enqueue(Ev($$"""{"e":"delta","role":"planner","kind":"text","text":"{{word}} "}"""));
                DrainLines();
                _lines.Enqueue(Ev($$"""{"e":"chat_reply","role":"planner","text":"{{reply}}"}"""));
                DrainLines();
                FinishChatTurn(0, false, null);
            };
            RefreshCallButton();
            if (!CallButton.IsEnabled)
                return Fail("ready with the stubs, yet disabled");
            SmokeClick(CallButton);
            var frames = CallVisual.RenderCount;
            await Task.Delay(400);
            if (!InCall || CallPanel.Visibility != Visibility.Visible || Math.Abs(CallPanel.ActualHeight - CallPanelHeight) > 1 || CallPanel.Opacity < 0.99
                || Math.Abs(CallVisual.ActualWidth - 185) > 1 || CallVisual.RenderCount < frames + 5 || CallStateText.Text != "Listening…"
                || (string)CallButton.Tag != "on" || CallGlyph.Text != "" || CallMuteButton.Visibility != Visibility.Visible || CallButton.ToolTip as string != "End the call")
                return Fail($"in a call: panel {CallPanel.Visibility} {CallPanel.ActualHeight:0}/{CallPanel.Opacity:0.00}, visualizer {CallVisual.ActualWidth:0} px, "
                    + $"{CallVisual.RenderCount - frames} frames, state \"{CallStateText.Text}\", tag {CallButton.Tag}");
            // a reply: Thinking, then Speaking; Space interrupts it
            reply = "Quick one. Slow sentence here. Never said.";
            await Task.Run(Phrase);
            if (!await Until(() => CallNow == CallState.Speaking && CallStateText.Text == "Speaking" && CallVisual.State == CallState.Speaking))
                return Fail($"speaking: {CallNow}, \"{CallStateText.Text}\"");
            Keyboard.ClearFocus();
            var space = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, 0, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            RaiseEvent(space);
            if (!space.Handled || CallNow != CallState.Listening || CallStateText.Text != "Listening…" || Spoken().Contains("Never said."))
                return Fail($"Space did not interrupt: {CallNow}, \"{CallStateText.Text}\"");
            // muted: Paused, the glyph and the text say so
            SmokeClick(CallMuteButton);
            if (CallNow != CallState.Paused || CallStateText.Text != "Paused" || CallMuteGlyph.Text != "")
                return Fail($"mute: {CallNow}, \"{CallStateText.Text}\"");
            SmokeClick(CallMuteButton);
            // Interrupt by talking: the user's voice while the model speaks stops it, and their phrase is sent
            _settings.Tts.CallBargeIn = true;
            await Task.Run(Phrase);
            if (!await Until(() => CallNow == CallState.Speaking))
                return Fail($"the second reply did not start speaking: {CallNow}");
            var users = _chatHistory.Count(m => m.Role == "user");
            reply = "Okay then.";
            await Task.Run(Phrase);
            if (!await Until(() => CallNow == CallState.Listening && Spoken().Contains("Okay then.")) || _chatHistory.Count(m => m.Role == "user") != users + 1 || Spoken().Contains("Never said."))
                return Fail($"talking over the speech did not interrupt it: {CallNow}, {_chatHistory.Count(m => m.Role == "user") - users} new messages, spoken [{string.Join("|", Spoken())}]");
            _settings.Tts.CallBargeIn = false;
            // End: the panel animates out, then collapses (its frames stop)
            SmokeClick(CallButton);
            if (InCall || (string)CallButton.Tag != "" || CallGlyph.Text != "" || CallMuteButton.Visibility != Visibility.Collapsed)
                return Fail("end: the button did not return");
            await Task.Delay(450);
            if (CallPanel.Visibility != Visibility.Collapsed || CallPanel.ActualHeight > 0.5)
                return Fail($"end: the panel is still {CallPanel.Visibility}, {CallPanel.ActualHeight:0} px");
            var stopped = CallVisual.RenderCount;
            await Task.Delay(150);
            if (CallVisual.RenderCount != stopped)
                return Fail("hidden, the visualizer still draws");
            // opening another chat ends the call (its toast as usual)
            var other = _store.Create("", "chat");
            StartCall();
            if (!InCall || other is null)
                return Fail("the second call did not start");
            OpenChatConversation(other.Id);
            if (InCall || ToastsOf("Call ended").Count == 0)
                return Fail("opening another chat did not end the call");
            _store.Delete(other.Id);
            // the microphone failing mid-call ends it
            StartCall();
            var mic = _callMic = new NAudio.Wave.WaveInEvent(); // never started: no device is opened
            MicrophoneStopped(mic, new InvalidOperationException("unplugged"));
            if (InCall || ToastsOf("The microphone stopped").Count == 0)
                return Fail("the microphone failing did not end the call");
            return true;
        }
        finally
        {
            EndCall();
            _settings.Tts.CallBargeIn = bargeIn;
            (_dictationFeed, _dictationTranscribe, _callTurnStub, _speechPlayer, _speechTest) = (null, null, null, player, test);
            NewChat_Click(this, new RoutedEventArgs());
        }
    }
}
