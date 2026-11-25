using System.Threading.Channels;

namespace AgentOrchestrator;

// The phone app's live events: each open events stream subscribes to one conversation and gets its own bounded queue of the
// engine's events (JSON texts). Publish never blocks (the UI thread calls it): a queue that is full drops its oldest event and
// gets one {"e":"resync"} at its end, so the phone refetches the conversation instead of showing a reply with a hole in it.
internal sealed class WebEvents
{
    public const int Capacity = 1000;
    public const string Resync = """{"e":"resync"}""";

    private sealed class Subscriber(string conversation)
    {
        public readonly string Conversation = conversation;
        public readonly Channel<string> Queue = Channel.CreateBounded<string>(new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        public long Written, ResyncAt; // events written so far; the resync's place among them (0: none yet)

        public void Write(string json)
        {
            Queue.Writer.TryWrite(json); // never false: a full queue drops its oldest
            Written++;
        }
    }

    private readonly List<Subscriber> _subscribers = []; // ponytail: one lock for all; per conversation if streams ever number hundreds

    public int Count { get { lock (_subscribers) return _subscribers.Count; } }

    // The conversations someone is subscribed to.
    public string[] Conversations() { lock (_subscribers) return _subscribers.Select(s => s.Conversation).Distinct().ToArray(); }

    // Ends (the reader completes) and unsubscribes when `ct` is cancelled.
    public ChannelReader<string> Subscribe(string conversationId, CancellationToken ct)
    {
        var subscriber = new Subscriber(conversationId);
        lock (_subscribers)
            _subscribers.Add(subscriber);
        ct.Register(() =>
        {
            lock (_subscribers)
                _subscribers.Remove(subscriber);
            subscriber.Queue.Writer.TryComplete();
        });
        return subscriber.Queue.Reader;
    }

    public void Publish(string conversationId, string json)
    {
        lock (_subscribers)
            foreach (var s in _subscribers)
            {
                if (s.Conversation != conversationId)
                    continue;
                var drops = s.Queue.Reader.Count >= Capacity; // only Publish adds, so a queue not full now has room
                s.Write(json);
                // read and dropped events leave from the front, so the resync is still queued while fewer than its place have left
                var resyncQueued = s.ResyncAt > 0 && s.Written - s.Queue.Reader.Count < s.ResyncAt;
                if (drops && !resyncQueued)
                {
                    s.Write(Resync); // what it drops is older than the resync, which covers it
                    s.ResyncAt = s.Written;
                }
            }
    }

    // --self-test: fan-out in order, per conversation; a cancelled subscription ends; a slow subscriber's full queue ends with
    // the newest event and one resync.
    internal static void SelfTest()
    {
        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException($"Web events self-test: {what}");
        }
        static List<string> Drain(ChannelReader<string> reader)
        {
            var items = new List<string>();
            while (reader.TryRead(out var item))
                items.Add(item);
            return items;
        }
        var events = new WebEvents();
        using var life = new CancellationTokenSource();
        var (one, two, other) = (events.Subscribe("c1", life.Token), events.Subscribe("c1", CancellationToken.None), events.Subscribe("c2", CancellationToken.None));
        events.Publish("c1", "a");
        events.Publish("c1", "b");
        Check(Drain(one).SequenceEqual(["a", "b"]) && Drain(two).SequenceEqual(["a", "b"]), "two subscribers get a then b");
        Check(Drain(other).Count == 0, "another conversation's subscriber gets nothing");
        Check(events.Count == 3 && events.Conversations().Order().SequenceEqual(["c1", "c2"]), "subscribers and conversations");
        life.Cancel();
        events.Publish("c1", "c");
        Check(Drain(one).Count == 0 && one.Completion.IsCompleted && events.Count == 2 && Drain(two).SequenceEqual(["c"]), "a cancelled subscription stops");

        var slow = events.Subscribe("slow", CancellationToken.None);
        for (var i = 1; i <= Capacity + 1; i++)
            events.Publish("slow", $"e{i}");
        var got = Drain(slow);
        Check(got.Count == Capacity && got[^2] == $"e{Capacity + 1}" && got[^1] == Resync && got.Count(e => e == Resync) == 1 && got[0] == "e3",
            $"a full queue: {got.Count} events, ends {string.Join(",", got.TakeLast(2))}");
        // a resync still queued is not repeated; once read, the next overflow gets its own
        for (var i = 0; i < Capacity + 5; i++)
            events.Publish("slow", "x");
        Check(Drain(slow).Count(e => e == Resync) == 1, "one resync per overflow while it is queued");
    }
}
