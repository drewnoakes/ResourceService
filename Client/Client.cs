using Aspire.V1;
using Grpc.Core;
using Grpc.Net.Client;

var resourceById = new Dictionary<ResourceId, ResourceSnapshot>();

using CancellationTokenSource cts = new();

Task task = WatchResourcesAsync("https://localhost:7143", cts.Token);

Console.ReadKey();

cts.Cancel();

await task;

async Task WatchResourcesAsync(string address, CancellationToken cancellationToken)
{
    var (channel, client) = CreateChannel(address);

    static (GrpcChannel, DashboardService.DashboardServiceClient) CreateChannel(string address)
    {
        var httpHandler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(20),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            PooledConnectionIdleTimeout = TimeSpan.FromHours(2)
        };

        var channel = GrpcChannel.ForAddress(
            address,
            channelOptions: new() { HttpHandler = httpHandler });

        DashboardService.DashboardServiceClient client = new(channel);

        return (channel, client);
    }

    var errorCount = 0;

    await channel.ConnectAsync(cancellationToken);

    while (!cancellationToken.IsCancellationRequested)
    {
        if (channel.State == ConnectivityState.Shutdown)
        {
            Console.WriteLine("Channel has shut down. Recreating connection.");

            channel.Dispose();

            (channel, client) = CreateChannel(address);
        }

        if (errorCount > 0)
        {
            // Exponential backoff (2^(n-1)) up to a maximum.
            TimeSpan delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, errorCount - 1), 15));

            await Task.Delay(delay, cancellationToken);
        }

        try
        {
            Console.WriteLine("Starting watch");

            var call = client.WatchResources(new WatchResourcesRequest { IsReconnect = false }, cancellationToken: cancellationToken);

            await foreach (var response in call.ResponseStream.ReadAllAsync(cancellationToken: cancellationToken))
            {
                Console.WriteLine($"Response type: {response.KindCase}");

                // The most reliable way to check that a streaming call succeeded is to successfully read a response.
                if (errorCount > 0)
                {
                    resourceById.Clear();
                    errorCount = 0;
                }

                if (response.KindCase == WatchResourcesUpdate.KindOneofCase.InitialSnapshot)
                {
                    // Copy initial snapshot into model.
                    foreach (var resource in response.InitialSnapshot.Resources)
                    {
                        resourceById[resource.ResourceId] = resource;
                    }
                }
                else if (response.KindCase == WatchResourcesUpdate.KindOneofCase.Changes)
                {
                    // Apply changes to the model.
                    foreach (var change in response.Changes.Value)
                    {
                        if (change.KindCase == WatchResourcesChange.KindOneofCase.Upsert)
                        {
                            // Upsert (i.e. add or replace)
                            resourceById[change.Upsert.ResourceId] = change.Upsert;
                        }
                        else if (change.KindCase == WatchResourcesChange.KindOneofCase.Delete)
                        {
                            // Remove
                            resourceById.Remove(change.Delete.ResourceId);
                        }
                    }
                }
                else
                {
                    throw new FormatException("Unsupported response kind: " + response.KindCase);
                }

                Console.WriteLine($"Current resource count: {resourceById.Count}");
            }
        }
        catch (RpcException ex)
        {
            errorCount++;
            Console.WriteLine($"Error {errorCount} watching resources: {ex.Message}");
        }
    }

    Console.WriteLine("Stopping resource watch");

    channel.Dispose();
}
