using System.Threading.Channels;

namespace MGRScreen.Core.Networking;

/// <summary>Single-consumer queue bounded by bytes and the age of the oldest pending item.</summary>
public sealed class MediaQueue(TimeSpan maxAge, long maxBytes)
{
    private readonly object _sync = new();
    private readonly Queue<(byte[] Data, long EnqueuedAt)> _items = new();
    private readonly Channel<bool> _changed = Channel.CreateBounded<bool>(1);
    private long _bytes;
    private bool _completed;

    public TimeSpan Lag
    {
        get
        {
            lock (_sync)
                return _items.TryPeek(out var head)
                    ? TimeSpan.FromMilliseconds(Environment.TickCount64 - head.EnqueuedAt)
                    : TimeSpan.Zero;
        }
    }

    public bool TryEnqueue(byte[] data)
    {
        lock (_sync)
        {
            var now = Environment.TickCount64;
            if (_completed || _bytes + data.Length > maxBytes ||
                (_items.TryPeek(out var head) && now - head.EnqueuedAt > maxAge.TotalMilliseconds))
                return false;

            _items.Enqueue((data, now));
            _bytes += data.Length;
            _changed.Writer.TryWrite(true);
            return true;
        }
    }

    public async ValueTask<byte[]?> DequeueAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_items.TryDequeue(out var item))
                {
                    _bytes -= item.Data.Length;
                    return item.Data;
                }
                if (_completed)
                    return null;
            }

            if (await _changed.Reader.WaitToReadAsync(token))
                _changed.Reader.TryRead(out _);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _items.Clear();
            _bytes = 0;
        }
    }

    public void Complete()
    {
        lock (_sync)
        {
            _completed = true;
            _changed.Writer.TryComplete();
        }
    }
}
