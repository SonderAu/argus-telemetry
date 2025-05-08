using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json.Serialization;


namespace Telemetry.Shared
{
    public class PerformanceSnapshot
    {
        public DateTime Timestamp { get; set; }
        public float CpuUsage { get; set; }
        public float MemoryUsedPercent { get; set; }
        public float DiskReadBps { get; set; }
        public float DiskWriteBps { get; set; }
        public Dictionary<string, float> NetInBpsByInterface { get; set; } = new();
        public Dictionary<string, float> NetOutBpsByInterface { get; set; } = new();

    }

}
