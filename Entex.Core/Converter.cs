using System.Net;

namespace Entex.Shared
{
    /// <summary>
    /// Converts a data type to another data type. This class cannot be inherited.
    /// </summary>
    public static class Converter
    {
        /// <summary>
        /// Converts a <see cref="TimeSpan"/> to milliseconds.
        /// </summary>
        /// <returns>The corresponding milliseconds.</returns>
        public static long ToInt64(TimeSpan timeSpan)
        {
            return (long)timeSpan.TotalMilliseconds;
        }

        /// <summary>
        /// Converts a <see cref="DateTime"/> to epoch milliseconds.
        /// </summary>
        /// <returns>The corresponding epoch milliseconds.</returns>
        public static long ToInt64(DateTime dateTime)
        {
            return (long)dateTime.ToUniversalTime().Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        /// <summary>
        /// Converts milliseconds to a <see cref="TimeSpan"/>.
        /// </summary>
        /// <param name="ms">The corresponding milliseconds.</param>
        public static TimeSpan ToTimeSpan(long ms)
        {
            return TimeSpan.FromMilliseconds(ms);
        }

        /// <summary>
        /// Converts a string of milliseconds to a <see cref="DateTime"/>.
        /// </summary>
        /// <param name="ms">The corresponding epoch milliseconds.</param>
        public static DateTime ToDateTime(string ms)
        {
            return ToDateTime(Convert.ToInt64(ms));
        }

        /// <summary>
        /// Converts epoch milliseconds to a localized <see cref="DateTime"/>.
        /// </summary>
        /// <returns>The corresponding <see cref="DateTime"/>.</returns>
        public static DateTime ToDateTime(long ms)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0).AddMilliseconds(ms).ToLocalTime();
        }

        /// <summary>
        /// Converts a int32 into a boolean.
        /// </summary>
        /// <param name="value">The int32 value.</param>
        /// <returns>If 1 true, false otherwise.</returns>
        public static bool ToBool(double value)
        {
            if (value.Equals(1))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Converts a boolean into a int32.
        /// </summary>
        /// <param name="value">The int32 value.</param>
        /// <returns>If 1 true, false otherwise.</returns>
        public static int ToInt32(bool value)
        {
            if (value)
            {
                return 1;
            }

            return 0;
        }

        /// <summary>
        /// Converts two double values into a percent.
        /// </summary>
        /// <param name="part"></param>
        /// <param name="whole"></param>
        public static double ToPercent(double part, double whole)
        {
            return part * 100 / whole;
        }

        /// <summary>
        /// Converts an <see cref="EndPoint"/> to 
        /// </summary>
        /// <param name="endPoint"></param>
        /// <returns></returns>
        public static string ToString(EndPoint endPoint)
        {
            string value = endPoint.ToString();
            return value[..value.IndexOf(':')];
        }
    }
}
