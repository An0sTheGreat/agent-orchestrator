using System.IO;
using System.Text.Json;

namespace AgentOrchestrator;

// Live progress from the local llama-server (llama.cpp runtime) while a reply or run uses it, from its /slots: how much of
// the prompt it has read (a long prompt or an image can take minutes with no output) and its measured speed. The worker
// card shows both; a reply with nothing on screen yet shows the reading progress in its status line.
public partial class MainWindow
{
    // Each card's previous poll, for the speeds (cards on different servers have their own slot 0).
    private Dictionary<string, (int Slot, long Processed, long Decoded, long Ms)> _llamaLast = new();
    // The port of each card's own llama-server, from the engine's loading / model events; cleared when the servers stop.
    private Dictionary<string, int> _llamaPorts = new();

    private void RecordLlamaPort(RoleCard? card, JsonElement ev)
    {
        if (card is not null && ev.TryGetProperty("port", out var port) && port.ValueKind == JsonValueKind.Number && port.TryGetInt32(out var number))
            _llamaPorts[card.Role] = number;
    }

    private async Task PollLlamaAsync(RoleCard card)
    {
        if (LlamaPort(card.Role) is not { } port)
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
            if (_llamaLast.TryGetValue(card.Role, out var last) && last.Slot == id && now > last.Ms)
            {
                var seconds = (now - last.Ms) / 1000.0;
                if (processed > last.Processed)
                    promptTps = (processed - last.Processed) / seconds;
                if (decoded > last.Decoded)
                    decodeTps = (decoded - last.Decoded) / seconds;
            }
            _llamaLast[card.Role] = (id, processed, decoded, now);
            card.SetLive(prompt, decoded, decodeTps, promptTps);
            if (decoded == 0 && prompt > 0)
                card.Status = $"Reading prompt {processed:N0} / {prompt:N0}";
            // a streaming tool call's own line ("Writing index.html… 7,743 characters") is kept: the two would alternate
            if (prompt > 0 && _chatBusy && _toolArgs.Length == 0 && _reply?.Status.Visibility == System.Windows.Visibility.Visible)
                SetChatStatus(LauncherLogic.LlamaProgressLine(ChatName(card, card.Role), prompt, processed, decoded));
            return;
        }

        static long Number(JsonElement el, string name) => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
    }

    // --ui-smoke: a fake llama-server's /slots, read three times: the card shows the prompt progress and the reading speed,
    // and an empty reply's status line follows the reading, then the writing (a quiet call such as /compact's summary).
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
        var (processed, hits) = (0, 0);
        var serve = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                var context = await listener.GetContextAsync();
                Interlocked.Increment(ref hits);
                processed = Math.Min(processed + 500, 2000);
                var decoded = processed < 2000 ? 0 : 300;
                var body = System.Text.Encoding.UTF8.GetBytes(
                    $$"""[{"id":0,"is_processing":true,"n_prompt_tokens":2000,"n_prompt_tokens_processed":{{processed}},"next_token":[{"n_decoded":{{decoded}}}]}]""");
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        });
        var (state, chat, last, ports, reply, busy) = (_llamaStateFile, _chatProcess, _llamaLast, _llamaPorts, _reply, _chatBusy);
        var card = CardFor("planner")!;
        var status = card.Status;
        try
        {
            _llamaStateFile = Path.Combine(AppSettings.DataRoot, "llama-poll.json");
            File.WriteAllText(_llamaStateFile, $$"""{"pid": 1, "port": {{port}}}"""); // the older single-server record
            _chatProcess = System.Diagnostics.Process.GetCurrentProcess();
            (_llamaLast, _llamaPorts) = (new(), new());
            (_reply, _chatBusy) = (new ChatReply { Status = new System.Windows.Controls.TextBlock() }, true);
            await PollLlamaAsync(card);
            await Task.Delay(200);
            await PollLlamaAsync(card);
            if (card.Status != "Reading prompt 1,000 / 2,000" || !card.TokensPerSec.StartsWith("Prompt:") ||
                !_reply.Status.Text.EndsWith("is reading the conversation: 1,000 / 2,000 tokens…"))
                return Fail($"{card.Status} | {card.TokensPerSec} | {_reply.Status.Text}");
            await PollLlamaAsync(card);
            await PollLlamaAsync(card);
            if (!_reply.Status.Text.EndsWith("is writing: 300 tokens so far…"))
                return Fail(_reply.Status.Text);
            _toolArgs.Append("{\"path\": \"index.html\"");
            SetChatStatus("Writing index.html… 22 characters");
            await PollLlamaAsync(card);
            if (_reply.Status.Text != "Writing index.html… 22 characters")
                return Fail("a streaming tool call's line was replaced: " + _reply.Status.Text);
            // Several servers: a card polls its own (the port its model event named, RecordLlamaPort), another card the first
            // recorded one, as does a card whose server is no longer recorded.
            File.WriteAllText(_llamaStateFile, $$"""{"servers": [{"pid": 1, "port": 1}, {"pid": 2, "port": {{port}}}]}""");
            (_llamaPorts["planner"], _llamaPorts["reviewer"]) = (port, 999);
            if (LlamaPort("planner") != port || LlamaPort("reviewer") != 1 || LlamaPort("implementer") != 1)
                return Fail($"ports by role: {LlamaPort("planner")} {LlamaPort("reviewer")}");
            var before = hits;
            try { await PollLlamaAsync(card); }
            catch (System.Net.Http.HttpRequestException) { return Fail("polled the first server, not the card's own"); }
            if (hits != before + 1)
                return Fail("the card's own server was not polled");
            return true;
        }
        finally
        {
            listener.Stop();
            _toolArgs.Clear();
            (_llamaStateFile, _chatProcess, _llamaLast, _llamaPorts, card.Status, _reply, _chatBusy) = (state, chat, last, ports, status, reply, busy);
        }
    }

    // The port of the card's own llama-server while the engine still records it, else the first recorded server's.
    // The state file is {"servers": [{"port", ...}, ...]} or an older engine's single {"port", ...}.
    private int? LlamaPort(string role)
    {
        var ports = new List<int>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_llamaStateFile));
            var root = doc.RootElement;
            var servers = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("servers", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().ToList() : [root];
            foreach (var server in servers)
                if (server.ValueKind == JsonValueKind.Object && server.TryGetProperty("port", out var port) && port.ValueKind == JsonValueKind.Number && port.TryGetInt32(out var number))
                    ports.Add(number);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
        return _llamaPorts.TryGetValue(role, out var mine) && ports.Contains(mine) ? mine : ports.Count > 0 ? ports[0] : null;
    }
}
