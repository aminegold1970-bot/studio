using System;
using System.Threading;

namespace AssetStudio
{
    public static class Progress
    {
        public static IProgress<int> Default = new Progress<int>();
        private static int preValue;

        public static void Reset()
        {
            Interlocked.Exchange(ref preValue, 0);
            Default.Report(0);
        }

        public static void Report(int current, int total)
        {
            var value = total <= 0
                ? 100
                : (int)Math.Max(0, Math.Min(100, (long)current * 100 / total));
            Report(value);
        }

        private static void Report(int value)
        {
            value = Math.Max(0, Math.Min(100, value));
            while (true)
            {
                var previous = Volatile.Read(ref preValue);
                if (value <= previous)
                    return;
                if (Interlocked.CompareExchange(ref preValue, value, previous) != previous)
                    continue;
                Default.Report(value);
                return;
            }
        }
    }
}
