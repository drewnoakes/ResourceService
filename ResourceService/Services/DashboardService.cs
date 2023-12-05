using System.Threading.Channels;
using Aspire.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace ResourceService.Services;

public class DashboardService : Aspire.V1.DashboardService.DashboardServiceBase
{
    public override async Task WatchResources(
        WatchResourcesRequest request,
        IServerStreamWriter<WatchResourcesUpdate> responseStream,
        ServerCallContext context)
    {
        var channel = Channel.CreateUnbounded<WatchResourcesUpdate>();

        // Send data
        _ = Task.Run(async () =>
        {
            // Initial snapshot
            var initialSnapshot = new WatchResourcesUpdate
            {
                InitialSnapshot = new WatchResourcesSnapshot
                {
                    Resources =
                    {
                        CreateRandomResourceSnapshot("One"),
                        CreateRandomResourceSnapshot("Two")
                    },
                    Types_ =
                    {
                        new ResourceType { UniqueName = "test", DisplayName = "Test", Commands = { } }
                    }
                }
            };

            await channel.Writer.WriteAsync(initialSnapshot);

            // Send random updates
            while (true)
            {
                await Task.Delay(3000);
                await channel.Writer.WriteAsync(new WatchResourcesUpdate { Changes = new WatchResourcesChanges { Value = { new WatchResourcesChange { Upsert = CreateRandomResourceSnapshot("One") } } } });
            }
        });

        // Send heartbeats
        _ = Task.Run(async () =>
        {
            const int HeartbeatIntervalMillis = 5_000;

            while (true)
            {
                await Task.Delay(HeartbeatIntervalMillis);
                await channel.Writer.WriteAsync(new WatchResourcesUpdate { Heartbeat = new Heartbeat { IntervalMilliseconds = HeartbeatIntervalMillis } });
            }
        });

        await foreach (var update in channel.Reader.ReadAllAsync(context.CancellationToken))
        {
            await responseStream.WriteAsync(update, context.CancellationToken);
        }
    }

    private static ResourceSnapshot CreateRandomResourceSnapshot(string id)
    {
        return new()
        {
            ResourceId = new()
            {
                Uid = id,
                ResourceType = "test"
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
