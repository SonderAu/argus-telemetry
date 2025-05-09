<p align="center">
  <img src="https://raw.githubusercontent.com/SonderAu/private-assets/refs/heads/main/logo.png" alt="PSG Argus Logo" width="200"/>
</p>


## 📚 Glossary

- [Telemetry Service](#️-telemetry-service)
- [Installer (MSI)](#-installer-msi)
- [Control Panel UI](#️-control-panel-ui)
- [Security & Privacy](#-security--privacy)
- [Architecture Overview](#-architecture-overview)

---

## 🛠️ Requirements

- Windows 10/11 or Server 2016+
- .NET 8 Runtime
- Administrator privileges to install the MSI


## 🛰️ Telemetry Service

The **TelemetryWorkerService** is a .NET 8 Windows service that collects, buffers, and exports system performance metrics.

### Key Features

- **Metric Collection**:
  - CPU usage
  - Memory usage (available, used percent)
  - Disk I/O (read/write in B/s)
  - Network I/O per interface (B/s)

- **Prometheus Integration**:
  - Pushes metrics to a Prometheus PushGateway
  - Includes labeled gauges using Prometheus.NET

- **Loki Logging**:
  - Sends serialized JSON snapshots to Grafana Loki
  - Includes metadata such as host, region, and component for filtering

- **Buffered & Resilient**:
  - Stores telemetry snapshots in a memory buffer
  - Flushes to disk every 10 seconds or 20 entries
  - Uploads flushed data to remote targets, then securely deletes the local file

- **Named Pipe Server**:
  - Streams live telemetry via a `TelemetryPipe` to a UI client (like the Control Panel)

---

## 📦 Installer (MSI)

The **Argus Telemetry Installer** is built using WiX v4 and provides a silent, secure deployment of the service.

### Structure

- Installs to:  
  `C:\Program Files\PSG\ArgusTelemetry\`
- Creates a `Logs` folder for buffered output:  
  `C:\Program Files\PSG\ArgusTelemetry\Logs\`

### Service Registration

- Registers the service as:  
  `PSG - Argus Telemetry`
- Starts automatically on install and stops/removes on uninstall

### Certificate Installation

- Installs `r3.crt` into the trusted CA store using `certutil`
- Executes via a WiX custom action (`CAQuietExec`)

### Upgrade Logic

- Uses `UpgradeCode` for proper versioning
- Prevents downgrades with a clear message

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
```

---

## 🖥️ Control Panel UI

The **TelemetryControlPanel** is a WPF desktop application that allows users to visualize and control the telemetry service in real-time.

### Key Features

- **Service Control**:
  - Start and stop the telemetry service using `ServiceController`
  - Polls service status every 5 seconds and updates UI

- **Live Telemetry View**:
  - Connects to `TelemetryPipe` and listens for real-time JSON snapshots
  - Displays CPU, memory, disk, and per-interface network stats

- **Log Viewer**:
  - Loads the latest log file from `C:\Logs\`
  - Automatically appends pipe activity and deserialization events to a log textbox

- **Fail-Safe Handling**:
  - Handles disconnection from the pipe and retries connection
  - Catches all exceptions and logs them visibly for troubleshooting

---

## 🔐 Security & Privacy

- **Minimal Identifiers:**:
  - Hostnames (Environment.MachineName) are included for correlation but can be hashed or anonymized if needed
- **Encrypted Transmission**:
  - Loki and PushGateway endpoints are accessed via HTTPS
- **Transient Local Storage**:
  - Snapshots flushed to disk are securely deleted after upload
- **No Usernames or PII**:
  - The telemetry contains no user accounts, file paths, or identifiable data beyond hostname


---

## 📡 Architecture Overview

```text
[Device Performance Counters] → [TelemetryWorkerService]
     ↘ Named Pipe (UI)
     ↘ Disk Buffer (jsonl)
     ↘ Prometheus PushGateway
     ↘ Grafana Loki (structured logs)
```

---

