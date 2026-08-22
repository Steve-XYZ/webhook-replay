using System.Collections.Concurrent;
using System.Threading.Channels;

namespace WebhookReplay.Api.Features.Webhooks;

public static class LiveFeed
{
    private const int ChannelCapacity = 64;

    private static readonly ConcurrentDictionary<Guid, LiveFeedGroup> Groups = new();

    public readonly record struct Subscription(Guid EndpointId, Guid Id, Channel<string> Channel);

    public static Subscription Subscribe(Guid endpointId)
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        var group = Groups.GetOrAdd(endpointId, _ => new LiveFeedGroup());
        group.Add(channel);

        return new Subscription(endpointId, Guid.CreateVersion7(), channel);
    }

    public static void Unsubscribe(Subscription subscription)
    {
        if (!Groups.TryGetValue(subscription.EndpointId, out var group))
        {
            return;
        }

        if (group.Remove(subscription.Channel) && group.IsEmpty && Groups.TryRemove(
                new KeyValuePair<Guid, LiveFeedGroup>(subscription.EndpointId, group)))
        {
            group.Complete();
        }
    }

    public static void Publish(Guid endpointId, string sseFrame)
    {
        if (!Groups.TryGetValue(endpointId, out var group))
        {
            return;
        }

        group.Broadcast(sseFrame);
    }

    private sealed class LiveFeedGroup
    {
        private readonly object _gate = new();
        private readonly List<Channel<string>> _channels = [];

        public bool IsEmpty { get { lock (_gate) return _channels.Count == 0; } }

        public void Add(Channel<string> channel)
        {
            lock (_gate)
            {
                _channels.Add(channel);
            }
        }

        public bool Remove(Channel<string> channel)
        {
            lock (_gate)
            {
                return _channels.Remove(channel);
            }
        }

        public void Broadcast(string frame)
        {
            Channel<string>[] snapshot;
            lock (_gate)
            {
                if (_channels.Count == 0)
                {
                    return;
                }
                snapshot = [.. _channels];
            }

            foreach (var channel in snapshot)
            {
                channel.Writer.TryWrite(frame);
            }
        }

        public void Complete()
        {
            lock (_gate)
            {
                foreach (var channel in _channels)
                {
                    channel.Writer.TryComplete();
                }
            }
        }
    }
}
