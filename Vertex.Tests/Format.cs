// Copyright 2026 Entex Interactive

using System.Globalization;

namespace Vertex.Tests
{
    /// <summary>
    /// Number formatting for reports.
    /// </summary>
    public static class Format
    {
        public static string Bytes(double bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            int unit = 0;
            while (Math.Abs(bytes) >= 1024 && unit < units.Length - 1)
            {
                bytes /= 1024;
                unit++;
            }

            return unit == 0 ? $"{bytes:N0} B" : $"{bytes:N1} {units[unit]}";
        }

        public static string Rate(double bytes, TimeSpan elapsed)
        {
            return elapsed <= TimeSpan.Zero ? "-" : $"{bytes / (1024.0 * 1024) / elapsed.TotalSeconds:N1} MB/s";
        }

        public static string PerSecond(double count, TimeSpan elapsed, string unit)
        {
            return elapsed <= TimeSpan.Zero ? "-" : $"{count / elapsed.TotalSeconds:N1} {unit}/s";
        }

        public static string Duration(TimeSpan span)
        {
            if (span.TotalSeconds < 1) return $"{span.TotalMilliseconds:N0} ms";
            if (span.TotalMinutes < 1) return $"{span.TotalSeconds:N2} s";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m {span.Seconds}s";
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        public static string Milliseconds(double ms)
        {
            return ms >= 100 ? ms.ToString("N0", CultureInfo.CurrentCulture) : ms.ToString("N1", CultureInfo.CurrentCulture);
        }

        public static string Percent(double part, double whole)
        {
            return whole <= 0 ? "-" : $"{part / whole * 100:N1}%";
        }
    }
}
