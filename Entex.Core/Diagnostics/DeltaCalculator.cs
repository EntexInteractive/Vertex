namespace Entex.Shared.Diagnostics
{
    /// <summary>
    /// Represents the ability to calculate the time between executions.
    /// </summary>
    public class DeltaCalculator
    {
        private readonly List<double> _deltas = new();
        private DateTime _previous = DateTime.Now;
        private static int _size = 500;

        /// <summary>
        /// The allowed sized of the dataset.
        /// </summary>
        public int Length
        {
            get { return  _deltas.Count; }
            set { _size = value; }
        }

        /// <summary>
        /// Initializes a new <see cref="DeltaCalculator"/> instance with a set data size.
        /// </summary>
        public DeltaCalculator(int length)
        {
            _size = length;
        }

        /// <summary>
        /// Initializes a new <see cref="DeltaCalculator"/> instance, and starts calculating deltas.
        /// </summary>
        public static DeltaCalculator StartNew()
        {
            DeltaCalculator calculator = new(_size);
            calculator.Calculate();
            return calculator;
        }


        /// <summary>
        /// Returns the average delta time.
        /// </summary>
        /// <returns>The average time between deltas.</returns>
        public TimeSpan Average()
        {
            return TimeSpan.FromMilliseconds(_deltas.Average());
        }

        /// <summary>
        /// Calculates the next delta interval.
        /// </summary>
        /// <returns>The average time between deltas.</returns>
        public TimeSpan Calculate()
        {
            DateTime now = DateTime.Now;
            _deltas.Add((now - _previous).TotalMilliseconds);
            if (_deltas.Count > _size) _deltas.RemoveAt(0);
            _previous = now;

            return TimeSpan.FromMilliseconds(_deltas.Average());
        }

        /// <summary>
        /// Removes all delta times.
        /// </summary>
        public void Clear()
        {
            _deltas.Clear();
        }

        public double[] GetValues()
        {
            return _deltas.ToArray();
        }

        /// <summary>
        /// Returns the minimum delta time.
        /// </summary>
        /// <returns>The minimum delta time.</returns>
        public TimeSpan Min()
        {
            return TimeSpan.FromMilliseconds(_deltas.Min());
        }

        /// <summary>
        /// Returns the maximum delta time.
        /// </summary>
        /// <returns>The maximum delta time.</returns>
        public TimeSpan Max()
        {
            return TimeSpan.FromMilliseconds(_deltas.Max());
        }
    }
}
