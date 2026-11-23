using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Ytp.Cache;

namespace Ytp.Download;

public enum EnqueueResult { Queued, AlreadyQueued, QueueFull }

/// <summary>Bounded queue of video IDs to download; an ID is queued at most once until processed.</summary>
public sealed class DownloadQueue
{
    private readonly Channel<string> channel;
    private readonly ConcurrentDictionary<string, byte> pending = new();

    public DownloadQueue(IOptions<CacheOptions> options)
    {
        channel = Channel.CreateBounded<string>(new BoundedChannelOptions(options.Value.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait // TryWrite returns false instead of dropping items
        });
    }

    public int PendingCount => pending.Count;

    public ChannelReader<string> Reader => channel.Reader;

    public EnqueueResult TryEnqueue(string videoId)
    {
        if (!pending.TryAdd(videoId, 0))
        {
            return EnqueueResult.AlreadyQueued;
        }
        if (!channel.Writer.TryWrite(videoId))
        {
            pending.TryRemove(videoId, out _);
            return EnqueueResult.QueueFull;
        }
        return EnqueueResult.Queued;
    }

    /// <summary>Called by the worker when an ID is done (cached or failed), so it can be queued again.</summary>
    public void MarkDone(string videoId) => pending.TryRemove(videoId, out _);
}
