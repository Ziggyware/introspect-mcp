using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace IntrospectMcp.Extended;

public sealed class HandleEntry
{
    public string Id { get; } = "h_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(18)).ToLowerInvariant();
    public object Value { get; }
    public Type DeclaredType { get; }
    public string ProducingCallId { get; }
    public DateTimeOffset LastUsed { get; internal set; }
    internal int Pins;
    internal bool Retired;
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal List<HandleLease> Dependencies { get; } = new();
    internal HandleEntry(object value, Type declaredType, string callId, DateTimeOffset now) => (Value, DeclaredType, ProducingCallId, LastUsed) = (value, declaredType, callId, now);
}

public sealed class HandleLease : IAsyncDisposable
{
    private readonly HandleStore _store;
    private bool _disposed;
    private bool _locked;
    public HandleEntry Entry { get; }
    internal HandleLease(HandleStore store, HandleEntry entry) => (_store, Entry) = (store, entry);
    public async Task LockAsync(CancellationToken cancellation) { await Entry.Gate.WaitAsync(cancellation); _locked = true; }
    public ValueTask DisposeAsync()
    {
        if (!_disposed) { _disposed = true; if (_locked) Entry.Gate.Release(); _store.Unpin(Entry); }
        return ValueTask.CompletedTask;
    }
}

public sealed class HandleStore : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HandleEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<object, HandleEntry> _objects = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, (string Reason, string CallId)> _retired = new(StringComparer.Ordinal);
    private readonly Queue<string> _retiredOrder = new();
    private readonly ConcurrentQueue<HandleEntry> _disposals = new();
    private readonly ConcurrentQueue<string> _disposalErrors = new();
    private readonly SemaphoreSlim _drain = new(1, 1);
    private readonly ServiceOptions _options;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sweeper;
    private bool _closed;
    public CancellationToken Lifetime => _stop.Token;
    public HandleStore(ServiceOptions options, TimeProvider? clock = null)
    {
        (_options, _clock) = (options, clock ?? TimeProvider.System);
        _sweeper = SweepLoop();
    }
    private async Task SweepLoop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(_options.HandleTtl.TotalSeconds / 2, 1, 30)));
        try { while (await timer.WaitForNextTickAsync(_stop.Token)) await SweepAsync(); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public HandleLease Register(object value, Type declared, string callId)
    {
        lock (_gate)
        {
            if (_closed) throw new BindingException("session_closed", "The handle session is closed.");
            Expire();
            if (_objects.TryGetValue(value, out var entry)) return PinEntry(entry);
            while (_entries.Count >= _options.MaxHandles)
            {
                var victim = _entries.Values.Where(e => e.Pins == 0).OrderBy(e => e.LastUsed).FirstOrDefault();
                if (victim is null) throw new BindingException("handle_capacity", "All handle slots are in use. Release handles, shorten the pipeline, or increase --max-handles.");
                Retire(victim, "evicted");
            }
            entry = new(value, declared, callId, _clock.GetUtcNow());
            _entries.Add(entry.Id, entry); _objects.Add(value, entry);
            return PinEntry(entry);
        }
    }
    internal void Depend(HandleEntry owner, string dependencyId)
    {
        lock (_gate)
        {
            if (owner.Id == dependencyId || owner.Dependencies.Any(d => d.Entry.Id == dependencyId)) return;
            if (!_entries.TryGetValue(dependencyId, out var dependency)) throw Missing(dependencyId);
            bool Reaches(HandleEntry current, HashSet<string> seen) => current.Id == owner.Id || seen.Add(current.Id) && current.Dependencies.Any(d => Reaches(d.Entry, seen));
            if (!Reaches(dependency, new(StringComparer.Ordinal))) owner.Dependencies.Add(PinEntry(dependency));
        }
    }
    public string? IdOf(object? value) { lock (_gate) return value is not null && _objects.TryGetValue(value, out var entry) ? entry.Id : null; }
    public HandleLease Pin(string id)
    {
        lock (_gate)
        {
            Expire();
            if (!_entries.TryGetValue(id, out var entry)) throw Missing(id);
            return PinEntry(entry);
        }
    }
    private HandleLease PinEntry(HandleEntry entry) { entry.Pins++; entry.LastUsed = _clock.GetUtcNow(); return new(this, entry); }
    internal void Unpin(HandleEntry entry)
    {
        lock (_gate)
        {
            entry.Pins--; entry.LastUsed = _clock.GetUtcNow();
            if (entry.Retired && entry.Pins == 0) _disposals.Enqueue(entry);
        }
    }
    private BindingException Missing(string id)
    {
        var known = _retired.TryGetValue(id, out var retired);
        return new BindingException("invalid_handle", $"Handle is {(known ? retired.Reason : "unknown or belongs to another session")}.", "$handle",
            new() { ["handle"] = id, ["reason"] = known ? retired.Reason : "unknown_or_foreign", ["producingCallId"] = known ? retired.CallId : null });
    }
    private void Expire()
    {
        var now = _clock.GetUtcNow();
        foreach (var entry in _entries.Values.Where(e => e.Pins == 0 && now - e.LastUsed >= _options.HandleTtl).ToArray()) Retire(entry, "expired");
    }
    private void Retire(HandleEntry entry, string reason)
    {
        _entries.Remove(entry.Id); _objects.Remove(entry.Value); entry.Retired = true;
        _retired[entry.Id] = (reason, entry.ProducingCallId); _retiredOrder.Enqueue(entry.Id);
        while (_retiredOrder.Count > Math.Max(1024, _options.MaxHandles * 4)) _retired.Remove(_retiredOrder.Dequeue());
        if (entry.Pins == 0) _disposals.Enqueue(entry);
    }
    public async Task ReleaseAsync(string id, string reason = "released")
    {
        lock (_gate)
        {
            Expire();
            if (!_entries.TryGetValue(id, out var entry)) throw Missing(id);
            if (entry.Pins != 0) throw new BindingException("handle_busy", "Handle is in use by a call, cursor, or delegate. Retry after that resource is finished/released.", "$handle");
            Retire(entry, reason);
        }
        await DrainAsync();
    }
    internal void RetireLeased(HandleEntry entry, string reason)
    { lock (_gate) { if (!entry.Retired) Retire(entry, reason); } }
    public async Task SweepAsync() { lock (_gate) Expire(); await DrainAsync(); }
    public async ValueTask DrainAsync()
    {
        await _drain.WaitAsync();
        try
        {
            while (_disposals.TryDequeue(out var entry))
            {
                try
                {
                    if (entry.Value is IAsyncDisposable a) await a.DisposeAsync(); else if (entry.Value is IDisposable d) d.Dispose();
                }
                catch (Exception e)
                {
                    string error = $"{entry.Id} ({entry.ProducingCallId}): {e.GetType().Name}: {e.Message}";
                    Console.Error.WriteLine("[dispose] " + error); _disposalErrors.Enqueue(error);
                    while (_disposalErrors.Count > 16) _disposalErrors.TryDequeue(out _);
                }
                foreach (var dependency in entry.Dependencies) await dependency.DisposeAsync();
                entry.Dependencies.Clear();
            }
        }
        finally { _drain.Release(); }
    }
    public JsonObject List(int offset = 0, int limit = 50)
    {
        lock (_gate)
        {
            Expire();
            return new() { ["count"] = _entries.Count, ["capacity"] = _options.MaxHandles, ["ttlSeconds"] = _options.HandleTtl.TotalSeconds,
                ["nextOffset"] = (long)offset + limit < _entries.Count ? offset + limit : null,
                ["handles"] = new JsonArray(_entries.Values.OrderBy(e => e.Id, StringComparer.Ordinal).Skip(offset).Take(limit).Select(e => (JsonNode)Describe(e)).ToArray()), ["disposalErrors"] = JsonSupport.Node(_disposalErrors.ToArray()) };
        }
    }
    public static JsonObject Describe(HandleEntry entry)
    {
        var t = entry.Value.GetType(); var cheap = new JsonObject();
        // Never run user ToString/getters just to display a handle.
        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(f => f.IsPublic || f.Name.Contains("k__BackingField", StringComparison.Ordinal)).Take(16))
        {
            if (!TypeModel.IsScalar(f.FieldType)) continue;
            try
            {
                string name = f.Name.StartsWith('<') ? f.Name[1..f.Name.IndexOf('>')] : f.Name;
                object? value = f.GetValue(entry.Value);
                // Declared scalar adapter types can contain user subclasses with arbitrary getters.
                if (value is not null && (!TypeModel.IsScalar(value.GetType()) || value is System.Text.Encoding or System.Globalization.CultureInfo)) continue;
                if (value is string text) value = text[..Math.Min(text.Length, 160)];
                if (value is byte[]) continue;
                cheap[JsonSupport.Key(name)] = JsonSupport.Node(value);
                if (cheap.Count >= 6) break;
            }
            catch { /* A preview is best-effort and never prevents the handle being used. */ }
        }
        string summary = t.Name;
        return new() { ["$handle"] = entry.Id, ["$type"] = JsonSupport.TypeName(t), ["declaredType"] = JsonSupport.TypeName(entry.DeclaredType),
            ["producingCallId"] = entry.ProducingCallId, ["summary"] = summary[..Math.Min(summary.Length, 120)], ["members"] = cheap };
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); await _sweeper;
        lock (_gate) { _closed = true; foreach (var entry in _entries.Values.ToArray()) Retire(entry, "session_closed"); }
        await DrainAsync(); _stop.Dispose();
    }
}
