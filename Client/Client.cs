using Aspire.V1;
using Grpc.Core;
using Grpc.Net.Client;
using System.Diagnostics;

var itemById = new Dictionary<ItemId, ItemSnapshot>();

using CancellationTokenSource cts = new();

Task task = WatchItemsAsync("https://localhost:7143", cts.Token);

Console.ReadKey();

cts.Cancel();

await task;

async Task WatchItemsAsync(string address, CancellationToken cancellationToken)
{
    GrpcChannel channel = GrpcChannel.ForAddress(address);
    DashboardService.DashboardServiceClient client = new(channel);

    var errorCount = 0;
    
    // We set an initial timeout and adjust it based on received heartbeats.
    var timeout = TimeSpan.FromSeconds(15);
    var timer = Stopwatch.StartNew();

    _ = Task.Run(
        async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (timer.Elapsed > timeout)
                {
                    Console.WriteLine("TIMEOUT!!!");
                    timer.Reset();
                }

                await Task.Delay(1000);
            }
        },
        cancellationToken);

    while (!cancellationToken.IsCancellationRequested)
    {
        if (channel.State == ConnectivityState.Shutdown)
        {
            Console.WriteLine("Channel has shut down. Recreating connection.");

            channel.Dispose();

            channel = GrpcChannel.ForAddress(address);
            client = new(channel);
            timer.Restart();
        }

        if (errorCount > 0)
        {
            // We are in an error state. No need for timeout tracking now.
            timer.Reset();

            // Exponential backoff (2^(n-1)) up to a maximum.
            TimeSpan delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, errorCount - 1), 15));

            await Task.Delay(delay, cancellationToken);
        }

        try
        {
            Console.WriteLine("Starting watch");

            var call = client.WatchItems(new WatchItemsRequest { IsReconnect = false }, cancellationToken: cancellationToken);

            await foreach (var response in call.ResponseStream.ReadAllAsync(cancellationToken: cancellationToken))
            {
                Console.WriteLine($"Response type: {response.KindCase}");

                // Any message causes us to restart the timeout timer.
                timer.Restart();

                // The most reliable way to check that a streaming call succeeded is to successfully read a response.
                if (errorCount > 0)
                {
                    itemById.Clear();
                    errorCount = 0;
                }

                if (response.KindCase == WatchItemsUpdate.KindOneofCase.Heartbeat)
                {
                    // Integrate heartbeat.
                    timeout = TimeSpan.FromMilliseconds(response.Heartbeat.IntervalMilliseconds * 5);
                }
                else if (response.KindCase == WatchItemsUpdate.KindOneofCase.InitialSnapshot)
                {
                    // Copy initial snapshot into model.
                    foreach (var item in response.InitialSnapshot.Items)
                    {
                        itemById[item.ItemId] = item;
                    }
                }
                else if (response.KindCase == WatchItemsUpdate.KindOneofCase.Changes)
                {
                    // Apply changes to the model.
                    foreach (var change in response.Changes.Value)
                    {
                        if (change.KindCase == WatchItemsChange.KindOneofCase.Upsert)
                        {
                            // Upsert (i.e. add or replace)
                            itemById[change.Upsert.ItemId] = change.Upsert;
                        }
                        else if (change.KindCase ==WatchItemsChange.KindOneofCase.Delete)
                        {
                            // Remove
                            itemById.Remove(change.Delete.ItemId);
                        }
                    }
                }
                else
                {
                    throw new FormatException("Unsupported response kind: " + response.KindCase);
                }

                Console.WriteLine($"Current item count: {itemById.Count}");
            }
        }
        catch (RpcException ex)
        {
            errorCount++;
            Console.WriteLine($"Error {errorCount} watching items: {ex.Message}");
        }
    }

    Console.WriteLine("Stopping item watch");

    channel.Dispose();
}
