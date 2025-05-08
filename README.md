## PSG - Argus Telemetry

The Telemetry Worker Service is a Windows background service designed to collect, buffer, and forward detailed system performance telemetry to multiple destinations for monitoring and analysis.

### Key Features

- **Performance Metrics Collection**
  - Captures CPU usage, memory utilization, disk I/O (read/write), and network throughput per interface using `PerformanceCounter`.
  - Uses friendly interface names for clarity in metrics (e.g., "Ethernet" instead of raw adapter description).

- **Prometheus Integration**
  - Exposes metrics as Prometheus gauges for scraping or export.
  - Pushes collected metrics to a Prometheus PushGateway endpoint on a scheduled interval.

- **Loki Logging Integration**
  - Serializes each performance snapshot into JSON and sends it to Grafana Loki for long-term storage and querying.
  - Includes structured log labels like `job`, `host`, `region`, and `component`.

- **Buffered File-Based Persistence**
  - Snapshots are temporarily stored in a buffer and flushed to disk (`telemetry_buffered.jsonl`) periodically.
  - Ensures resilience in case of remote collector unavailability.

- **Named Pipe IPC**
  - Streams live telemetry snapshots via a named pipe (`TelemetryPipe`) to a connected WPF control panel or any compatible client.

- **Secure Deletion & Cleanup**
  - Ensures temporary files are cleared after upload to prevent local data accumulation.

### Architecture Overview

- Runs continuously as a `.NET 8` `BackgroundService`.
- Starts with a warm-up phase and initializes counters.
- Collects and enqueues snapshots every 2 seconds.
- Flushes data every 10 seconds or when a buffer threshold is met.

### Intended Use

Designed to be deployed as part of the **PSG Oversight** system for real-time visibility into device resource usage across managed environments.

