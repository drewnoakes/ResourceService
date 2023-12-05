using System.Threading.Channels;
using Aspire.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace ResourceService.Services;

public class DashboardService : Aspire.V1.DashboardService.DashboardServiceBase
{
    public override async Task WatchItems(
        WatchItemsRequest request,
        IServerStreamWriter<WatchItemsUpdate> responseStream,
        ServerCallContext context)
    {
        var channel = Channel.CreateUnbounded<WatchItemsUpdate>();

        // Send data
        _ = Task.Run(async () =>
        {
            // Initial snapshot
            var initialSnapshot = new WatchItemsUpdate
            {
                InitialSnapshot = new WatchItemsSnapshot
                {
                    Items =
                    {
                        CreateRandomItemSnapshot("One"),
                        CreateRandomItemSnapshot("Two")
                    },
                    Types_ =
                    {
                        new ItemType { UniqueName = "test", DisplayName = "Test", Commands = { } }
                    }
                }
            };

            await channel.Writer.WriteAsync(initialSnapshot);

            // Send random updates
            while (true)
            {
                await Task.Delay(3000);
                await channel.Writer.WriteAsync(new WatchItemsUpdate { Changes = new WatchItemsChanges { Value = { new WatchItemsChange { Upsert = CreateRandomItemSnapshot("One") } } } });
            }
        });

        // Send heartbeats
        _ = Task.Run(async () =>
        {
            const int HeartbeatIntervalMillis = 5_000;

            while (true)
            {
                await Task.Delay(HeartbeatIntervalMillis);
                await channel.Writer.WriteAsync(new WatchItemsUpdate { Heartbeat = new Heartbeat { IntervalMilliseconds = HeartbeatIntervalMillis } });
            }
        });

        await foreach (var update in channel.Reader.ReadAllAsync(context.CancellationToken))
        {
            await responseStream.WriteAsync(update, context.CancellationToken);
        }
    }

    private static ItemSnapshot CreateRandomItemSnapshot(string id)
    {
        return new()
        {
            ItemId = new()
            {
                Uid = id,
                ItemType = "test"
            },
            DisplayName = id,
            State = "Running",
            CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow.Date),
            ExpectedEndpointsCount = 2,
            Endpoints =
            {
                new Aspire.V1.Endpoint { Name = "endpoint1", HttpAddress = "http://endpoint1" },
                new Aspire.V1.Endpoint { Name = "endpoint1", AllocatedAddress = "endpoint1", AllocatedPort = 1234 }
            },
            Environment = 
            {
                new EnvironmentVariable { Name = "key", Value = "value" }
            },
            AdditionalData =
            {
                new AdditionalData { Namespace = "testing", Name = "dummy1", Value = "foo" },
                new AdditionalData { Namespace = "testing", Name = "dummy2", Values = new() { Values = { "foo", "bar" } } },
            }
        };
    }
}
