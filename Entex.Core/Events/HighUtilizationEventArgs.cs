using Entex.Shared.Diagnostics;

namespace Entex.Shared.Events
{
    public class HighUtilizationEventArgs : EventArgs
    {
        /// <summary>
        /// The duration of time that the current process has been running.
        /// </summary>
        public TimeSpan Uptime { get; }

        /// <summary>
        /// The amount of cpu currently being utilized by the current process.
        /// </summary>
        public double CpuUsage { get; }

        /// <summary>
        /// The amount of memory, in bytes, being used by the current process.
        /// </summary>
        public double RamUsage { get; }

        public HighUtilizationEventArgs(ProcessMonitor monitor)
        {
            CpuUsage = monitor.CpuUsage;
            RamUsage = monitor.RamUsage;
            Uptime = monitor.Uptime;
        }
    }
}