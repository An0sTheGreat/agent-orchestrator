using System.Buffers.Binary;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace AgentOrchestrator;

// The phone app's dictation (MainWindow.WebApp.cs routes /api/dictate): the phone records and posts raw PCM (16-bit little-endian,
// mono, 16 kHz); the PC transcribes it with the shared recognizer through a DictationSession, off the UI thread, and answers
// with the phrases joined by Speech.Insert's rules. Nothing is kept: the audio lives only for the request.
public partial class MainWindow
{
    private const int WebDictateMax = 10 * 60 * Speech.Rate * 2; // 10 minutes: 19,200,000 bytes
    private const string WebDictateNotReady = "Download the speech model on the PC (Settings > Voice)";

    // The smoke's stand-in for Speech.Recognizer(), so a phrase comes back without the models (null: the real recognizer).
    private Func<float[], string>? _webTranscribe;

    private (int, object) WebDictateState() => (200, new { ready = _webTranscribe is not null || SpeechModels.Ready });

    private async Task<(int, object)> WebDictate(byte[] body)
    {
        if (body.Length > WebDictateMax)
            return (413, new { error = "Too long (10 minutes at most)" });
        if (body.Length == 0 || body.Length % 2 != 0)
            return (400, new { error = "Expected 16-bit PCM, mono, 16 kHz" });
        var stand = _webTranscribe;
        if (stand is null && !SpeechModels.Ready)
            return (409, new { error = WebDictateNotReady });
        return await Task.Run<(int, object)>(() =>
        {
            Func<float[], string> transcribe;
            try { transcribe = stand ?? Speech.Recognizer(); }
            catch (InvalidOperationException) { return (409, new { error = WebDictateNotReady }); } // the models went since the check
            try { return (200, new { text = WebTranscribe(transcribe, body) }); }
            catch (Exception exception) { return (500, new { error = "Transcription failed: " + exception.Message }); }
        });
    }

    // Fed in 100 ms chunks, as the desktop's microphone feeds it. A session ends on its own after 2 s of silence or 10 s with
    // no speech; a recording that goes on after that (the user tapped stop later) continues in a fresh session.
    private static string WebTranscribe(Func<float[], string> transcribe, byte[] pcm)
    {
        const int chunk = Speech.Rate / 10;
        var text = "";
        var ended = false;
        DictationSession? session = null;
        void Say(string phrase) => text = Speech.Insert(text, text.Length, phrase).text;
        try
        {
            for (var at = 0; at < pcm.Length; at += chunk * 2)
            {
                if (session is null)
                {
                    session = new DictationSession(transcribe);
                    session.Text += Say;
                    session.Ended += _ => ended = true;
                }
                var samples = new float[Math.Min(chunk * 2, pcm.Length - at) / 2];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(at + 2 * i)) / 32768f;
                session.Feed(samples);
                if (ended)
                {
                    session.Dispose();
                    (session, ended) = (null, false);
                }
            }
            session?.Stop(); // the phrase in progress
        }
        finally { session?.Dispose(); }
        return text;
    }

    // /api/dictate (WebAppSmoke, while on), on the smoke's data folder, where the speech models are absent.
    private async Task<bool> WebVoiceSmoke(int port)
    {
        static bool Fail(string what) { Console.Error.WriteLine($"webapp-smoke: voice: {what}"); return false; }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        async Task<(int Status, JsonNode? Body)> Send(byte[]? pcm, string? login = "smoke@example.com")
        {
            using var request = new HttpRequestMessage(pcm is null ? HttpMethod.Get : HttpMethod.Post, $"http://127.0.0.1:{port}/api/dictate");
            if (pcm is not null)
                request.Content = new ByteArrayContent(pcm) { Headers = { ContentType = new("application/octet-stream") } };
            if (login is not null)
                request.Headers.Add("Tailscale-User-Login", login);
            using var response = await http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            try { return ((int)response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null); }
            catch (System.Text.Json.JsonException) { return ((int)response.StatusCode, null); }
        }
        // `loud` seconds of a -6 dB square-ish level (speech to the energy gate) then `quiet` seconds of silence.
        static byte[] Pcm(double loud, double quiet)
        {
            var bytes = new byte[2 * (int)((loud + quiet) * Speech.Rate)];
            for (var i = 0; i < (int)(loud * Speech.Rate); i++)
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(2 * i), (short)(i % 2 == 0 ? 16384 : -16384));
            return bytes;
        }
        try
        {
            if ((await Send(null, null)).Status != 403 || (await Send([0, 0], null)).Status != 403)
                return Fail("no owner is not 403");
            if (await Send(null) is not { Status: 200, Body: { } state } || state["ready"]?.GetValueKind() != System.Text.Json.JsonValueKind.False)
                return Fail($"GET: {(await Send(null)).Body}");
            if ((await Send([1, 2, 3])).Status != 400 || (await Send([])).Status != 400)
                return Fail("an odd-length or empty body is not 400");
            if ((await Send(new byte[WebDictateMax + 2])).Status != 413)
                return Fail("over 10 minutes is not 413");
            if (await Send(Pcm(0, 0.5)) is not { Status: 409, Body: { } absent } || absent["error"]?.ToString() != WebDictateNotReady)
                return Fail($"no models: {(await Send(Pcm(0, 0.5))).Body}");

            _webTranscribe = _ => "hello";
            if (await Send(null) is not { Body: { } ready } || ready["ready"]?.GetValue<bool>() != true)
                return Fail("GET with a recognizer is not ready");
            if (await Send(Pcm(1, 0.6)) is not { Status: 200, Body: { } said } || said["text"]?.ToString() != "Hello")
                return Fail($"a phrase: {(await Send(Pcm(1, 0.6))).Body}");
            // a pause past a session's 2 s end, then more speech: both phrases, joined
            if (await Send([.. Pcm(1, 2.5), .. Pcm(1, 0.2)]) is not { Status: 200, Body: { } two } || two["text"]?.ToString() != "Hello hello")
                return Fail("two phrases across a session's end");
            return true;
        }
        finally { _webTranscribe = null; }
    }
}
