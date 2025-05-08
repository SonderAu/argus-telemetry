using System;
using System.IO;
using System.IO.Pipes;
using System.ServiceProcess;
using System.Text.Json;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Telemetry.Shared;

namespace TelemetryControlPanel
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer _statusTimer;
        private ServiceController _service;

        public MainWindow()
        {
            InitializeComponent();
            _service = new ServiceController("TelemetryService");
            _statusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _statusTimer.Tick += (_, _) => UpdateServiceStatus();
            _statusTimer.Start();
            UpdateServiceStatus();
        }

        private void UpdateServiceStatus()
        {
            try
            {
                _service.Refresh();
                StatusText.Text = $"Service Status: {_service.Status}";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }
        private void StartService_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _service.Refresh();
                if (_service.Status == ServiceControllerStatus.Stopped || _service.Status == ServiceControllerStatus.Paused)
                {
                    _service.Start();
                    _service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start service:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            UpdateServiceStatus();
        }

        private void AppendLog(string message)
        {
            Dispatcher.Invoke(() => LogsTextBox.AppendText($"{DateTime.Now:T}: {message}\n"));
        }

        private void StopService_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _service.Refresh();
                if (_service.Status == ServiceControllerStatus.Running)
                {
                    _service.Stop();
                    _service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to stop service:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            UpdateServiceStatus();
        }
        private void RefreshLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string logPath = $"C:\\Logs\\telemetry{DateTime.Now:yyyyMMdd}.log";

                if (File.Exists(logPath))
                {
                    using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    LogsTextBox.Text = reader.ReadToEnd();
                }
                else
                {
                    LogsTextBox.Text = "No log file found.";
                }
            }
            catch (Exception ex)
            {
                LogsTextBox.Text = $"Error reading logs: {ex.Message}";
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            StartPipeClient();
            AppendLog("Window loaded");

        }


        private void StartPipeClient()
        {
            _ = Task.Run(async () =>
            {
                while (true)

                {
                    try
                    {
                        

                        using var client = new NamedPipeClientStream(".", "TelemetryPipe", PipeDirection.In);
                        await client.ConnectAsync(3000);
                        AppendLog("Connected to pipe.");


                        using var reader = new StreamReader(client, Encoding.UTF8);
                        while (client.IsConnected)
                        {
                            var line = await reader.ReadLineAsync();
                            if (!string.IsNullOrWhiteSpace(line))
                            {
                                var snapshot = JsonSerializer.Deserialize<PerformanceSnapshot>(line);
                                if (snapshot != null)
                                {
                                    Dispatcher.Invoke(() => UpdateUI(snapshot));
                                    AppendLog("Received: " + line);
                                }
                                else
                                {
                                    AppendLog("Warning: Deserialized snapshot is null.");
                                }


                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"Pipe error: {ex.Message}");
                        await Task.Delay(1000); // Retry after disconnection
                    }

                }
            });
        }
        private void UpdateUI(PerformanceSnapshot snapshot)
        {
            CpuLabel.Text = $"CPU: {snapshot.CpuUsage}%";
            MemLabel.Text = $"Memory: {snapshot.MemoryUsedPercent}%";
            DiskReadLabel.Text = $"Disk Read: {snapshot.DiskReadBps} B/s";
            DiskWriteLabel.Text = $"Disk Write: {snapshot.DiskWriteBps} B/s";

            // Combine network stats per interface
            var nicStats = snapshot.NetInBpsByInterface.Select(kvp => new NicStat
            {
                Interface = kvp.Key,
                In = kvp.Value,
                Out = snapshot.NetOutBpsByInterface.TryGetValue(kvp.Key, out var outVal) ? outVal : 0

            }).ToList();
            AppendLog($"NICs received: In={snapshot.NetInBpsByInterface?.Count}, Out={snapshot.NetOutBpsByInterface?.Count}");


            NicStatsListView.ItemsSource = nicStats;
        }

        public class NicStat
        {
            public string Interface { get; set; } = "";
            public float In { get; set; }
            public float Out { get; set; }
        }



    }



}
