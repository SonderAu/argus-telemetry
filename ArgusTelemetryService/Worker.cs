using System.Diagnostics;
using System.Text;
using System.Collections.Concurrent;
using Telemetry.Shared;
using System.IO.Pipes;
using System.Text.Json;
using Serilog;
using System.Security.AccessControl;
using System.Management;
using System.Net.Http;
using System.Net.Http.Json;
using Prometheus;
using System.Net.NetworkInformation;






namespace TelemetryWorkerService
{
    public class Worker : BackgroundService
    {
        private readonly Dictionary<string, string> _instanceToFriendlyName = new();
        private List<PerformanceCounter> _netInCounters = new();
        private List<PerformanceCounter> _netOutCounters = new();
        private PerformanceCounter? _cpuCounter;
        private PerformanceCounter? _memAvailable;
        private PerformanceCounter? _memTotal;
        private PerformanceCounter? _diskRead;
        private PerformanceCounter? _diskWrite;
        private float _totalPhysicalMemoryMb;

        private readonly ConcurrentQueue<PerformanceSnapshot> _snapshotBuffer = new();
        private readonly TimeSpan _flushInterval = TimeSpan.FromSeconds(10);
        private readonly int _bufferThreshold = 20;
        private Timer? _flushTimer;

        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly Gauge CpuGauge = Metrics.CreateGauge("telemetry_cpu_usage_percent", "CPU usage in percent");
        private static readonly Gauge MemGauge = Metrics.CreateGauge("telemetry_memory_used_percent", "Memory usage in percent");
        private static readonly Gauge DiskReadGauge = Metrics.CreateGauge("telemetry_disk_read_bps", "Disk read in bytes/sec");
        private static readonly Gauge DiskWriteGauge = Metrics.CreateGauge("telemetry_disk_write_bps", "Disk write in bytes/sec");
        private static readonly Gauge NetInGauge = Metrics.CreateGauge("telemetry_net_in_bps", "Bytes received per second", new[] { "interface" });
        private static readonly Gauge NetOutGauge = Metrics.CreateGauge("telemetry_net_out_bps", "Bytes sent per second", new[] { "interface" });






        private async Task PushToLoki(string logLine)
        {
            var timestampNs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;

            var payload = new
            {
                streams = new[]
                {
            new
            {
                stream = new
                {
                    job = "telemetry",
                    host = Environment.MachineName,
                    client = "psg",           // <-- replace or parameterize as needed
                    region = "au-west",       // <-- replace or detect dynamically
                    env = "prod",             // "dev", "test", etc.
                    component = "worker",     // e.g., telemetry agent, sensor, forwarder
                    log_type = "metrics_raw"  // tag for downstream parsing
                },
                values = new[]
                {
                    new[]
                    {
                        timestampNs.ToString(),
                        logLine
                    }
                }
            }
        }
            };

            var response = await _httpClient.PostAsJsonAsync("https://loki.psg.net.au/loki/api/v1/push", payload);
            response.EnsureSuccessStatusCode(); // log otherwise
        }



        private void InitializeNetworkCounters()
        {
            var category = new PerformanceCounterCategory("Network Interface");
            var instanceNames = category.GetInstanceNames();

            
            var descriptionToFriendlyName = NetworkInterface.GetAllNetworkInterfaces()
                .ToDictionary(nic => nic.Description, nic => nic.Name);

            foreach (var instanceName in instanceNames)
            {
                if (instanceName.Contains("Loopback") || instanceName.Contains("isatap"))
                    continue;

                // Match on full description
                if (descriptionToFriendlyName.TryGetValue(instanceName, out var friendlyName))
                {
                    _netInCounters.Add(new PerformanceCounter("Network Interface", "Bytes Received/sec", instanceName)
                    {
                        MachineName = ".",
                        ReadOnly = true
                    });

                    _netOutCounters.Add(new PerformanceCounter("Network Interface", "Bytes Sent/sec", instanceName)
                    {
                        MachineName = ".",
                        ReadOnly = true
                    });

                    _instanceToFriendlyName[instanceName] = friendlyName;
                    Console.WriteLine($"Mapped {instanceName} => {friendlyName}");
                }
                else
                {
                    // Fallback if no matching interface found
                    _instanceToFriendlyName[instanceName] = instanceName;
                    Console.WriteLine($"Unmapped: {instanceName} (used raw name)");
                }
            }
        }


        private float GetPhysicalMemoryMb()
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
            foreach (var obj in searcher.Get())
            {
                if (obj["TotalVisibleMemorySize"] is ulong totalKb)
                {
                    return totalKb / 1024f;
                }
            }
            return 1; // fallback
        }


        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                var testPath = Path.Combine(AppContext.BaseDirectory, "Logs", "worker-test.log");
                Directory.CreateDirectory(Path.GetDirectoryName(testPath)!);

                File.AppendAllText(testPath, $"Worker started at {DateTime.Now}\n");

                InitializeNetworkCounters();
                Log.Information("Initialized network counters for all interfaces.");

                _cpuCounter = new("Processor", "% Processor Time", "_Total");
                _cpuCounter.NextValue(); // Warm-up
                await Task.Delay(1000, stoppingToken); // Give it time

                _memAvailable = new("Memory", "Available MBytes");
                _memTotal = new("Memory", "Commit Limit");
                _diskRead = new("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
                _diskWrite = new("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
                _totalPhysicalMemoryMb = GetPhysicalMemoryMb();



                Log.Information("All performance counters initialized");
                //var metricServer = new KestrelMetricServer(port: 9184);
                //metricServer.Start();
                _flushTimer = new Timer(async _ => await FlushBufferToDisk(), null, _flushInterval, _flushInterval);

                _ = Task.Run(async () =>
                {
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        try
                        {
                            var snapshot = CollectSnapshot();
                            _snapshotBuffer.Enqueue(snapshot);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Error collecting snapshot in background loop");
                        }

                        await Task.Delay(2000, stoppingToken); // match pipe interval
                    }
                }, stoppingToken);

                await StartNamedPipeServer(stoppingToken);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ExecuteAsync failed during service initialization");
            }
        }

        private NamedPipeServerStream CreatePipeServer()
        {
            return new NamedPipeServerStream(
                "TelemetryPipe",
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Message,
                PipeOptions.Asynchronous
            );
        }

        private async Task StartNamedPipeServer(CancellationToken stoppingToken)
        {
            Log.Information("About to start pipe server...");

            while (!stoppingToken.IsCancellationRequested)
            {
                Log.Information("Waiting for pipe client...");
                using var server = CreatePipeServer();
                await server.WaitForConnectionAsync(stoppingToken);
                Log.Information("Client connected to pipe.");

                using var writer = new StreamWriter(server, Encoding.UTF8) { AutoFlush = true };

                while (!stoppingToken.IsCancellationRequested && server.IsConnected)
                {
                    Log.Information("Pipe is connected, beginning telemetry loop...");

                    try
                    {
                        var snapshot = CollectSnapshot();
                        Log.Information("Snapshot: {@Snapshot}", snapshot);
                        _snapshotBuffer.Enqueue(snapshot);

                        var json = JsonSerializer.Serialize(snapshot);
                        await writer.WriteLineAsync(json);
                        await writer.FlushAsync();
                        Log.Information("Sent snapshot: {Json}", json);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error sending telemetry snapshot");
                    }

                    await Task.Delay(2000, stoppingToken);
                }

                Log.Warning("Client disconnected from pipe.");
            }
        }
        private async Task FlushBufferToDisk()
        {
            if (_snapshotBuffer.IsEmpty) return;

            var tempPath = Path.Combine(AppContext.BaseDirectory, "Logs", "telemetry_buffered.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);


            int count = 0;

            // Collect all current snapshots
            List<PerformanceSnapshot> snapshots = new();
            while (_snapshotBuffer.TryDequeue(out var snap))
            {
                snapshots.Add(snap);

                // Update metrics
                CpuGauge.Set(snap.CpuUsage);
                MemGauge.Set(snap.MemoryUsedPercent);
                DiskReadGauge.Set(snap.DiskReadBps);
                DiskWriteGauge.Set(snap.DiskWriteBps);

                foreach (var kv in snap.NetInBpsByInterface)
                    NetInGauge.WithLabels(kv.Key).Set(kv.Value);

                foreach (var kv in snap.NetOutBpsByInterface)
                    NetOutGauge.WithLabels(kv.Key).Set(kv.Value);
            }

            // Write all snapshots to file
            try
            {
                using var stream = new FileStream(tempPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, Encoding.UTF8);

                foreach (var snap in snapshots)
                {
                    var line = JsonSerializer.Serialize(snap);
                    await PushToLoki(line);
                    await writer.WriteLineAsync(line);
                    count++;
                }

                await writer.FlushAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to write telemetry to disk.");
                return;
            }

            Log.Information("Wrote {Count} snapshots to disk", count);

            // Upload metrics AFTER file is released
            await PushMetricsToGateway();

            // Secure file deletion
            try
            {
                // Optional: Zero out contents before deletion (paranoia mode)
                File.WriteAllText(tempPath, string.Empty);

                // Delete the now-empty file
                File.Delete(tempPath);
                Log.Information("Temp file securely deleted after flush.");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to securely delete temp file after flush.");
            }

        }

        private async Task PushMetricsToGateway()
        {
            var job = "telemetry_worker";
            var instance = Environment.MachineName;
            var url = $"https://pushgateway.psg.net.au/metrics/job/{job}/instance/{instance}";

            using var httpClient = new HttpClient();
            using var stream = new MemoryStream();

            // This writes directly to the Stream, not a StreamWriter
            await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream);
            stream.Position = 0;

            var content = new StreamContent(stream);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");

            var response = await httpClient.PostAsync(url, content);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("PushGateway push failed: {Status}", response.StatusCode);
            }
        }

        private PerformanceSnapshot CollectSnapshot()
        {
            try
            {
                float cpu = _cpuCounter?.NextValue() ?? 0;
                float memFreeMb = _memAvailable?.NextValue() ?? 0;
                float memUsedPct = 100 - ((memFreeMb / _totalPhysicalMemoryMb) * 100);
                float memTotalMb = (_memTotal?.NextValue() ?? 1) / 1024f;
                var netIn = new Dictionary<string, float>();
                var netOut = new Dictionary<string, float>();

                foreach (var counter in _netInCounters)
                {
                    var name = _instanceToFriendlyName.TryGetValue(counter.InstanceName, out var friendly)
                        ? friendly
                        : counter.InstanceName;

                    netIn[name] = (float)Math.Round(counter.NextValue(), 2);
                }

                foreach (var counter in _netOutCounters)
                {
                    var name = _instanceToFriendlyName.TryGetValue(counter.InstanceName, out var friendly)
                        ? friendly
                        : counter.InstanceName;

                    netOut[name] = (float)Math.Round(counter.NextValue(), 2);
                }


                var snapshot = new PerformanceSnapshot
                {
                    Timestamp = DateTime.UtcNow,
                    CpuUsage = (float)Math.Round(cpu, 2),
                    MemoryUsedPercent = (float)Math.Round(memUsedPct, 2),
                    DiskReadBps = (float)Math.Round(_diskRead?.NextValue() ?? 0, 2),
                    DiskWriteBps = (float)Math.Round(_diskWrite?.NextValue() ?? 0, 2),
                    NetInBpsByInterface = netIn,
                    NetOutBpsByInterface = netOut
                };
                CpuGauge.Set(snapshot.CpuUsage);
                MemGauge.Set(snapshot.MemoryUsedPercent);
                DiskReadGauge.Set(snapshot.DiskReadBps);
                DiskWriteGauge.Set(snapshot.DiskWriteBps);


                return snapshot;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to collect performance snapshot");
                return new PerformanceSnapshot
                {
                    Timestamp = DateTime.UtcNow,
                    CpuUsage = -1,
                    MemoryUsedPercent = -1,
                    DiskReadBps = -1,
                    DiskWriteBps = -1,
                    
                };
            }
        }


    }
}
