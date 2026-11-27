using GtaStream.Studio;

// Studio host, first stage: connects to the EBS and plays the game side with a fake adapter.
//   GTASTREAM_EBS=wss://2-29-164-176.sslip.io/api/v1/studio GTASTREAM_STUDIO_TOKEN=… dotnet run --project studio/GtaStream.Studio
// Without GTASTREAM_EBS it connects to a local EBS (ws://127.0.0.1:5090/api/v1/studio).
var url = new Uri(Environment.GetEnvironmentVariable("GTASTREAM_EBS") ?? "ws://127.0.0.1:5090/api/v1/studio");
var token = Environment.GetEnvironmentVariable("GTASTREAM_STUDIO_TOKEN");
if (string.IsNullOrEmpty(token))
{
    Console.Error.WriteLine("GTASTREAM_STUDIO_TOKEN fehlt (auf dem Testserver: das Admin-Token).");
    return 1;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
await new EbsConnection(url, token, new FakeAdapter()).RunForever(stop.Token);
return 0;
