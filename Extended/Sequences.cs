using System.Collections;

namespace IntrospectMcp.Extended;

internal abstract class SequenceCursor : IAsyncDisposable
{
    public string SourceId { get; }
    public Type ElementType { get; }
    private bool _ended;
    protected SequenceCursor(string sourceId, Type elementType) => (SourceId, ElementType) = (sourceId, elementType);
    protected abstract ValueTask<bool> MoveNext();
    protected abstract object? Current { get; }
    protected virtual IDisposable? RegisterCancellation(CancellationToken cancellation) => null;
    public async Task<(List<object?> Items, bool More)> Page(int size, CancellationToken cancellation)
    {
        using var registration = RegisterCancellation(cancellation);
        var items = new List<object?>();
        while (items.Count < size && !_ended)
        {
            cancellation.ThrowIfCancellationRequested();
            if (await MoveNext()) items.Add(Current);
            else _ended = true;
        }
        // Never prefetch a blocking (possibly live) async source. A full final page
        // can therefore be followed by one empty terminal page.
        return (items, !_ended);
    }
    public abstract ValueTask DisposeAsync();
    public static SequenceCursor Create(object value, string sourceId, CancellationToken lifetime)
    {
        var asyncType = TypeModel.Generic(value.GetType(), typeof(IAsyncEnumerable<>));
        if (asyncType is not null) return (SequenceCursor)Activator.CreateInstance(typeof(AsyncCursor<>).MakeGenericType(asyncType.GetGenericArguments()), value, sourceId, lifetime)!;
        if (value is IEnumerable enumerable) return new SyncCursor(enumerable.GetEnumerator(), sourceId, TypeModel.Generic(value.GetType(), typeof(IEnumerable<>))?.GetGenericArguments()[0] ?? typeof(object));
        throw new BindingException("not_a_sequence", "The handle does not contain an enumerable sequence.");
    }
    private sealed class SyncCursor : SequenceCursor
    {
        private readonly IEnumerator _enumerator;
        public SyncCursor(IEnumerator enumerator, string id, Type element) : base(id, element) => _enumerator = enumerator;
        protected override ValueTask<bool> MoveNext() => ValueTask.FromResult(_enumerator.MoveNext());
        protected override object? Current => _enumerator.Current;
        public override ValueTask DisposeAsync() { (_enumerator as IDisposable)?.Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class AsyncCursor<T> : SequenceCursor
    {
        private readonly IAsyncEnumerator<T> _enumerator;
        private readonly CancellationTokenSource _cancellation;
        public AsyncCursor(IAsyncEnumerable<T> enumerable, string id, CancellationToken lifetime) : base(id, typeof(T))
        {
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            try { _enumerator = enumerable.GetAsyncEnumerator(_cancellation.Token); }
            catch { _cancellation.Dispose(); throw; }
        }
        protected override IDisposable RegisterCancellation(CancellationToken cancellation) => cancellation.Register(() => _cancellation.Cancel());
        protected override ValueTask<bool> MoveNext() => _enumerator.MoveNextAsync();
        protected override object? Current => _enumerator.Current;
        public override async ValueTask DisposeAsync()
        {
            try { await _enumerator.DisposeAsync(); }
            finally { _cancellation.Dispose(); }
        }
    }
}
