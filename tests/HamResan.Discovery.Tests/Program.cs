using Microsoft.Extensions.DependencyInjection;
using Shiny;
using Shiny.Net.Discovery;

// Exercises two independent DNS-SD managers on the same host's multicast interfaces.
// This cannot substitute for Android NsdManager or cross-device LAN testing.
using ServiceProvider advertiser = new ServiceCollection().AddLogging().AddMdns().BuildServiceProvider();
using ServiceProvider browser = new ServiceCollection().AddLogging().AddMdns().BuildServiceProvider();
IMdnsManager publisher = advertiser.GetRequiredService<IMdnsManager>();
IMdnsManager finder = browser.GetRequiredService<IMdnsManager>();
string name = "hamresan-ci-" + Guid.NewGuid().ToString("N")[..8];
var found = new TaskCompletionSource<MdnsService>(TaskCreationOptions.RunContinuationsAsynchronously);
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
Task scan = Task.Run(async () =>
{
    try
    {
        await foreach (MdnsBrowseResult item in finder.Browse(new MdnsBrowseConfig("_hamresan._tcp"), stop.Token))
            if (item.Status == MdnsBrowseStatus.Found && item.Service.InstanceName == name && item.Service.IsResolved)
                found.TrySetResult(item.Service);
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
});
try
{
    await Task.Delay(1000, stop.Token);
    await using IMdnsPublication registration = await publisher.Publish(new MdnsServiceRegistration(name, "_hamresan._tcp", 42123)
    {
        TxtRecords = new Dictionary<string, string> { ["os"] = "Windows", ["version"] = "1" }
    }, stop.Token);
    MdnsService service = await found.Task.WaitAsync(stop.Token);
    if (service.Port != 42123 || !service.TxtRecords.TryGetValue("version", out string? version) || version != "1")
        throw new Exception("Resolved service metadata does not match the published service.");
    Console.WriteLine("PASS: mDNS publication discovered and resolved by a second manager");
}
finally
{
    stop.Cancel();
    await scan;
}
