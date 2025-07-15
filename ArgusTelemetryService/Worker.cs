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
        private Timer? _flushTimer;

        private static readonly HttpClient _httpClient = new HttpClient();

        private readonly Dictionary<string, Gauge.Child> _cpuGaugeMap = new();
        private readonly Dictionary<string, Gauge.Child> _memGaugeMap = new();
        private readonly Dictionary<string, Gauge.Child> _diskReadGaugeMap = new();
        private readonly Dictionary<string, Gauge.Child> _diskWriteGaugeMap = new();
        private readonly Dictionary<string, Gauge.Child> _netInGaugeMap = new();
        private readonly Dictionary<string, Gauge.Child> _netOutGaugeMap = new();

        private static readonly Gauge CpuGauge = Metrics.CreateGauge("telemetry_cpu_usage_percent", "CPU usage in percent", new[] { "client", "region", "env", "instance" });
        private static readonly Gauge MemGauge = Metrics.CreateGauge("telemetry_memory_used_percent", "Memory usage in percent", new[] { "client", "region", "env", "instance" });
        private static readonly Gauge DiskReadGauge = Metrics.CreateGauge("telemetry_disk_read_bps", "Disk read in bytes/sec", new[] { "client", "region", "env", "instance" });
        private static readonly Gauge DiskWriteGauge = Metrics.CreateGauge("telemetry_disk_write_bps", "Disk write in bytes/sec", new[] { "client", "region", "env", "instance" });
        private static readonly Gauge NetInGauge = Metrics.CreateGauge("telemetry_net_in_bps", "Bytes received per second", new[] { "interface", "client", "region", "env", "instance" });
        private static readonly Gauge NetOutGauge = Metrics.CreateGauge("telemetry_net_out_bps", "Bytes sent per second", new[] { "interface", "client", "region", "env", "instance" });

        private AppConfig _config = new();

        private void InitializeMetricLabelSets()
        {
            string[] labels = { _config.Client, _config.Region, _config.Environment, Environment.MachineName };
            string labelKey = string.Join("|", labels);

            _cpuGaugeMap[labelKey] = CpuGauge.WithLabels(labels);
            _memGaugeMap[labelKey] = MemGauge.WithLabels(labels);
            _diskReadGaugeMap[labelKey] = DiskReadGauge.WithLabels(labels);
            _diskWriteGaugeMap[labelKey] = DiskWriteGauge.WithLabels(labels);
        }

        private async Task PushToLoki(string logLine)
        {
            if (string.IsNullOrWhiteSpace(_config.LokiUrl))
            {
                Log.Warning("Loki URL not configured, skipping log push.");
                return;
            }

            var timestampNs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;

            var payload = new
            {
                streams = new[]
                {
            new
            {
                stream = new
                {
                    job = _config.Component,
                    host = Environment.MachineName,
                    client = _config.Client,
                    region = _config.Region,
                    env = _config.Environment,
                    component = _config.Component,
                    log_type = _config.LogType
                },
                values = new[] { new[] { timestampNs.ToString(), logLine } }
            }
        }
            };

            try
            {
                var response = await _httpClient.PostAsJsonAsync(_config.LokiUrl, payload);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error sending telemetry to Loki.");
            }
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

                string configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
                if (!File.Exists(configPath))
                {
                    configPath = Path.Combine(AppContext.BaseDirectory, "config.local.json");
                }

                if (File.Exists(configPath))
                {
                    try
                    {
                        var configJson = await File.ReadAllTextAsync(configPath, stoppingToken);
                        var parsed = JsonSerializer.Deserialize<AppConfig>(configJson);
                        if (parsed != null)
                        {
                            _config = parsed;
                            Log.Information("Loaded configuration from config.json");
                            InitializeMetricLabelSets(); // <- YOU NEED THIS
                        }

                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to load config.json, using defaults.");
                    }
                }
                else
                {
                    Log.Warning("No config.json found, using default values.");
                }
                InitializeNetworkCounters();
                Log.Information("Initialized network counters for all interfaces.");

                try
                {
                    if (_config.HyperV)
                    {
                        _cpuCounter = new PerformanceCounter("Hyper-V Hypervisor Logical Processor", "% Total Run Time", "_Total");
                        Log.Information("Using Hyper-V counter.");
                    }
                    else
                    {
                        _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                        Log.Information("Using standard CPU counter.");
                    }
                    _cpuCounter.NextValue();
                    await Task.Delay(1000, stoppingToken);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to initialize CPU counter. Defaulting to standard processor counter.");
                    _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                }

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

                //  Update metrics using labeled .WithLabels() calls
                CpuGauge
                    .WithLabels(_config.Client, _config.Region, _config.Environment, Environment.MachineName)
                    .Set(snap.CpuUsage);
                MemGauge
                    .WithLabels(_config.Client, _config.Region, _config.Environment, Environment.MachineName)
                    .Set(snap.MemoryUsedPercent);
                DiskReadGauge
                    .WithLabels(_config.Client, _config.Region, _config.Environment, Environment.MachineName)
                    .Set(snap.DiskReadBps);
                DiskWriteGauge
                    .WithLabels(_config.Client, _config.Region, _config.Environment, Environment.MachineName)
                    .Set(snap.DiskWriteBps);

                foreach (var kv in snap.NetInBpsByInterface)
                {
                    NetInGauge
                        .WithLabels(kv.Key, _config.Client, _config.Region, _config.Environment, Environment.MachineName)
                        .Set(kv.Value);
                }

                foreach (var kv in snap.NetOutBpsByInterface)
                {
                    NetOutGauge
                        .WithLabels(kv.Key, _config.Client, _config.Region, _config.Environment, Environment.MachineName)
                        .Set(kv.Value);
                }
            }

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

            await PushMetricsToGateway();

            try
            {
                File.WriteAllText(tempPath, string.Empty);
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
            string job = _config.Component ?? "telemetry_worker";
            string instance = Environment.MachineName;

            if (string.IsNullOrWhiteSpace(_config.PushGatewayUrlBase))
            {
                Log.Warning("PushGateway URL base not set in config.");
                return;
            }

            // Only job and instance
            string url = $"{_config.PushGatewayUrlBase}/job/{Uri.EscapeDataString(job)}/instance/{Uri.EscapeDataString(instance)}";

            using var httpClient = new HttpClient();
            using var stream = new MemoryStream();

            await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream);
            stream.Position = 0;

            var content = new StreamContent(stream);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");

            try
            {
                var response = await httpClient.PostAsync(url, content);
                if (!response.IsSuccessStatusCode)
                {
                    Log.Warning("PushGateway push failed: {Status}", response.StatusCode);
                }
                else
                {
                    Log.Information("Metrics pushed to {Url}", url);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error pushing metrics to PushGateway.");
            }
        }



        private PerformanceSnapshot CollectSnapshot()
        {
            try
            {
                float cpu = _cpuCounter?.NextValue() ?? 0;
                float memFreeMb = _memAvailable?.NextValue() ?? 0;
                float memUsedPct = 100 - ((memFreeMb / _totalPhysicalMemoryMb) * 100);
                float diskRead = _diskRead?.NextValue() ?? 0;
                float diskWrite = _diskWrite?.NextValue() ?? 0;

                string[] baseLabels = { _config.Client, _config.Region, _config.Environment, Environment.MachineName };
                string baseKey = string.Join("|", baseLabels);

                _cpuGaugeMap[baseKey].Set(cpu);
                _memGaugeMap[baseKey].Set(memUsedPct);
                _diskReadGaugeMap[baseKey].Set(diskRead);
                _diskWriteGaugeMap[baseKey].Set(diskWrite);

                var netIn = new Dictionary<string, float>();
                var netOut = new Dictionary<string, float>();

                foreach (var counter in _netInCounters)
                {
                    var name = _instanceToFriendlyName.TryGetValue(counter.InstanceName, out var friendly) ? friendly : counter.InstanceName;
                    var value = (float)Math.Round(counter.NextValue(), 2);
                    netIn[name] = value;

                    string netKey = string.Join("|", name, baseKey);
                    if (!_netInGaugeMap.ContainsKey(netKey))
                        _netInGaugeMap[netKey] = NetInGauge.WithLabels(name, _config.Client, _config.Region, _config.Environment, Environment.MachineName);

                    _netInGaugeMap[netKey].Set(value);
                }

                foreach (var counter in _netOutCounters)
                {
                    var name = _instanceToFriendlyName.TryGetValue(counter.InstanceName, out var friendly) ? friendly : counter.InstanceName;
                    var value = (float)Math.Round(counter.NextValue(), 2);
                    netOut[name] = value;

                    string netKey = string.Join("|", name, baseKey);
                    if (!_netOutGaugeMap.ContainsKey(netKey))
                        _netOutGaugeMap[netKey] = NetOutGauge.WithLabels(name, _config.Client, _config.Region, _config.Environment, Environment.MachineName);

                    _netOutGaugeMap[netKey].Set(value);
                }

                return new PerformanceSnapshot
                {
                    Timestamp = DateTime.UtcNow,
                    CpuUsage = cpu,
                    MemoryUsedPercent = memUsedPct,
                    DiskReadBps = diskRead,
                    DiskWriteBps = diskWrite,
                    NetInBpsByInterface = netIn,
                    NetOutBpsByInterface = netOut
                };
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
    public class AppConfig
    {
        public string Client { get; set; } = "default";
        public string Region { get; set; } = "default-region";
        public string Environment { get; set; } = "dev";
        public string Component { get; set; } = "worker";
        public string LogType { get; set; } = "metrics_raw";
        public string LokiUrl { get; set; } = "";
        public string PushGatewayUrlBase { get; set; } = "";
        public bool HyperV { get; set; } = false;
    }
}
