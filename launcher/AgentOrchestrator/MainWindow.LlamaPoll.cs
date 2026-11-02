using System.IO;
using System.Text.Json;

namespace AgentOrchestrator;

// Live progress from the local llama-server (llama.cpp runtime) while a reply or run uses it, from its /slots: how much of
// the prompt it has read (a long prompt or an image can take minutes with no output) and its measured speed. The worker
// card shows both; a reply with nothing on screen yet shows the reading progress in its status line.
public partial class MainWindow
{
    private (int Slot, long Processed, long Decoded, long Ms)? _llamaLast; // the previous poll, for the speeds

    private async Task PollLlamaAsync(RoleCard card)
    {
        if (LlamaPort() is not { } port)
            return;
        using var response = await _http.GetAsync($"http://127.0.0.1:{port}/slots");
        if (!response.IsSuccessStatusCode)
            return;
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        if (doc.RootElement.ValueKind != JsonValueKind.Array || (_process ?? _chatProcess) is null)
            return;
        foreach (var slot in doc.RootElement.EnumerateArray())
        {
            if (!(slot.TryGetProperty("is_processing", out var busy) && busy.ValueKind == JsonValueKind.True))
                continue;
            var (id, prompt, processed) = ((int)Number(slot, "id"), Number(slot, "n_prompt_tokens"), Number(slot, "n_prompt_tokens_processed"));
            var decoded = slot.TryGetProperty("next_token", out var next) && next.ValueKind == JsonValueKind.Array && next.GetArrayLength() > 0
                ? Number(next[0], "n_decoded") : 0;
            var now = Environment.TickCount64;
            double? promptTps = null, decodeTps = null;
            if (_llamaLast is { } last && last.Slot == id && now > last.Ms)
            {
                var seconds = (now - last.Ms) / 1000.0;
                if (processed > last.Processed)
                    promptTps = (processed - last.Processed) / seconds;
                if (decoded > last.Decoded)
                    decodeTps = (decoded - last.Decoded) / seconds;
            }
            _llamaLast = (id, processed, decoded, now);
            card.SetLive(prompt, decoded, decodeTps, promptTps);
            if (decoded == 0 && prompt > 0)
            {
                card.Status = $"Reading prompt {processed:N0} / {prompt:N0}";
                if (_chatBusy && _reply?.Status.IsVisible == true)
                    SetChatStatus($"{ChatName(card, card.Role)} is reading the conversation: {processed:N0} / {prompt:N0} tokens…");
            }
            return;
        }

        static long Number(JsonElement el, string name) => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
    }

    // --ui-smoke: a fake llama-server's /slots, read twice: the card shows the prompt progress and the reading speed.
    private async Task<bool> LlamaPollSmoke()
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: llama poll: {what}"); return false; }
        var listener = new System.Net.HttpListener();
        var port = 0;
        for (var tries = 0; tries < 5 && !listener.IsListening; tries++)
        {
            port = Random.Shared.Next(20000, 60000);
            listener.Prefixes.Clear();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { listener.Start(); } catch (System.Net.HttpListenerException) { }
        }
        if (!listener.IsListening)
            return Fail("no port");
        var processed = 0;
        var serve = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                var context = await listener.GetContextAsync();
                processed += 500;
                var body = System.Text.Encoding.UTF8.GetBytes(
                    $$"""[{"id":0,"is_processing":true,"n_prompt_tokens":2000,"n_prompt_tokens_processed":{{processed}},"next_token":[{"n_decoded":0}]}]""");
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        });
        var (state, chat, runtime, last) = (_llamaStateFile, _chatProcess, _settings.Runtime, _llamaLast);
        var card = CardFor("planner")!;
        var status = card.Status;
        try
        {
            _llamaStateFile = Path.Combine(AppSettings.DataRoot, "llama-poll.json");
            File.WriteAllText(_llamaStateFile, $$"""{"pid": 1, "port": {{port}}}""");
            _chatProcess = System.Diagnostics.Process.GetCurrentProcess();
            _llamaLast = null;
            await PollLlamaAsync(card);
            await Task.Delay(200);
            await PollLlamaAsync(card);
            if (card.Status != "Reading prompt 1,000 / 2,000" || !card.TokensPerSec.StartsWith("Prompt:"))
                return Fail($"{card.Status} | {card.TokensPerSec}");
            return true;
        }
        finally
        {
            listener.Stop();
            (_llamaStateFile, _chatProcess, _settings.Runtime, _llamaLast, card.Status) = (state, chat, runtime, last, status);
        }
    }

    // The engine's record of the llama-server it started: {"port": ...}.
    private int? LlamaPort()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_llamaStateFile));
            return doc.RootElement.TryGetProperty("port", out var port) && port.ValueKind == JsonValueKind.Number ? port.GetInt32() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
