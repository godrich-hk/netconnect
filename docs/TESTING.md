# Testing

## Environment observed

The development sandbox is Debian Linux and has no `dotnet`, MAUI workload, Java, Android SDK/adb, Windows SDK, Android device, or `iperf3`. `dotnet --info` and `dotnet workload list` both failed with `command not found`; a request to the official .NET SDK download host failed because outbound network access is restricted. Thus neither the C# build nor the tests below were executed here. The tests are a dependency-free executable rather than xUnit so that the portable core needs no third-party NuGet dependencies. The GitHub Actions workflow is configured but has not been run or verified.

On a machine with a stable .NET 10 SDK:

```sh
dotnet --info
dotnet workload list
dotnet build tests/HamResan.Core.Tests/HamResan.Core.Tests.csproj -c Release
dotnet run --project tests/HamResan.Core.Tests/HamResan.Core.Tests.csproj -c Release --no-build
```

The harness contains checks for protocol framing/version, bounds, filenames/collisions, mutual TLS loopback streaming, zero bytes and SHA-256, rejection before opening input, wrong pin, cancellation/deadline, invalid digest, early EOF cleanup, and a destination write error. These are **written, not verified**. Real full disk, large-file memory behavior, platform TLS implementations and network change behavior have not been measured.

## Real-device acceptance still required

Once the missing app is implemented, use a private LAN with client-to-client traffic permitted, install on at least two Android devices and two Windows PCs, and test Android→Android, Android→Windows, Windows→Android and Windows→Windows. Confirm pairing codes on both sides, reject a changed identity and a request, send to two receivers with independent acceptance and cancellation, test simultaneous incoming connections, and compare hashes for empty, duplicate-named, hundreds-of-MiB and ≥1 GiB files. Test a disconnected network, timeouts and disk-full handling. Confirm no content precedes acceptance. Record memory use, LAN conditions and measured throughput against `iperf3`; no throughput measurements have been made. Test discovery retry and manual IP on networks where multicast is blocked. None of these platform tests have been performed.

The unexecuted harness also contains invitation format, invalid input, expiry and concurrent single-use redemption tests. The invitation is not integrated with the TLS engine, so these checks do not test an end-to-end connection-code pairing flow.
