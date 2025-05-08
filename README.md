# PSG Argus Telemetry

A modular system for collecting and managing Windows-based system telemetry in real-time. Built with scalability, secure deployment, and observability in mind.

---

## 📚 Glossary

- [Telemetry Service](#telemetry-service)
- [Installer (MSI)](#installer-msi)
- [Control Panel](#control-panel-ui-coming-soon)

---

## 🛰️ Telemetry Service

The **TelemetryWorkerService** is a .NET 8 Windows service that collects, buffers, and exports system resource metrics.

### Key Features

- **Metric Collection**: Uses `PerformanceCounter` to gather:
  - CPU usage
  - Memory usage (available, used percent)
  - Disk I/O (read/write in B/s)
  - Network I/O (per interface, in B/s)

- **Prometheus Support**:
  - Metrics exported to PushGateway via HTTP
  - Prometheus-compliant `Gauge` metrics with labels

- **Loki Logging**:
  - JSON snapshots pushed to Grafana Loki for historical logs
  - Label-enriched streams (e.g., job, region, component)

- **Buffering & Resilience**:
  - Snapshots collected every 2 seconds
  - Flushed to disk and remote endpoints every 10 seconds or 20 items
  - Secure file wipe and cleanup

- **IPC for UI Clients**:
  - Named pipe server (`TelemetryPipe`) streams snapshots in real time to a WPF or Electron frontend

---

## 📦 Installer (MSI)

The **Argus Telemetry Installer** is a WiX v4-based MSI installer that deploys the telemetry service with minimal user interaction.

### Structure

- Installs to: C:\Program Files\PSG\ArgusTelemetry\
- - Creates a writable `Logs` subfolder for buffered snapshots

### Service Registration

- Registers as `PSG - Argus Telemetry` running as a standalone service (`ownProcess`)
- Automatically starts post-install and stops/removes cleanly on uninstall

### Custom Actions

- Installs a root certificate (`r3.crt`) into the local machine CA store using `certutil`
- Executed silently during installation via `CAQuietExec`

### Upgrade Logic

- Uses `UpgradeCode` to ensure clean version upgrades
- Prevents downgrades with a clear error message

### MSI XML Snippet (Excerpt)

```xml
<ServiceInstall
Id="ArgusTelemetryServiceInstaller"
Name="PSG - Argus Telemetry"
DisplayName="PSG - Argus Telemetry"
Description="Monitors and reports telemetry metrics from client systems"
Start="auto"
Type="ownProcess"
ErrorControl="normal" />

