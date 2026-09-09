using System;
using System.Collections.Generic;
using NLog;

namespace PhysicsOptimizer.Utils
{
    /// <summary>
    /// Central logging facade. One NLog logger ("PhysicsOptimizer") so Torch prints a uniform
    /// `PhysicsOptimizer:` prefix; the feature tag renders as `[Tag] message`.
    /// Tag must be the declaring class's LogSource constant - never inline ad-hoc tags.
    /// Identical repeated lines are coalesced: first occurrence logs immediately, repeats are
    /// counted and flushed as one `(repeated xN)` summary by <see cref="FlushRepeats"/>.
    /// </summary>
    public static class Log
    {
        private static readonly ILogger Logger = LogManager.GetLogger("PhysicsOptimizer");
        private static readonly object LogLock = new object();

        private sealed class PendingEntry
        {
            public readonly LogLevel Level;
            public readonly Exception Exception;
            public int Count;

            public PendingEntry(LogLevel level, Exception ex)
            {
                Level = level;
                Exception = ex;
                Count = 1;
            }
        }

        private static readonly Dictionary<string, PendingEntry> PendingRepeats = new Dictionary<string, PendingEntry>();

        private static string Fmt(string source, string message) => $"[{source}] {message}";

        public static void Info(string source, string message) => Write(LogLevel.Info, null, source, message);
        public static void Warn(string source, string message) => Write(LogLevel.Warn, null, source, message);
        public static void Warn(Exception ex, string source, string message) => Write(LogLevel.Warn, ex, source, message);
        public static void Error(string source, string message) => Write(LogLevel.Error, null, source, message);
        public static void Error(Exception ex, string source, string message) => Write(LogLevel.Error, ex, source, message);
        public static void Debug(string source, string message) => Write(LogLevel.Debug, null, source, message);

        private static void Write(LogLevel level, Exception ex, string source, string message)
        {
            var formatted = Fmt(source, message);
            lock (LogLock)
            {
                if (PendingRepeats.TryGetValue(formatted, out var pending))
                {
                    pending.Count++;
                    return;
                }
                PendingRepeats[formatted] = new PendingEntry(level, ex);
            }
            Emit(level, ex, formatted);
        }

        /// <summary>
        /// Emits `(repeated xN)` summaries for coalesced lines and resets tracking. Call
        /// periodically (e.g. every 600 ticks from the plugin update loop) and on shutdown.
        /// </summary>
        public static void FlushRepeats()
        {
            if (PendingRepeats.Count == 0) return;

            List<KeyValuePair<string, PendingEntry>> flushed;
            lock (LogLock)
            {
                if (PendingRepeats.Count == 0) return;
                flushed = new List<KeyValuePair<string, PendingEntry>>(PendingRepeats.Count);
                foreach (var kv in PendingRepeats)
                {
                    if (kv.Value.Count > 1) flushed.Add(kv);
                }
                PendingRepeats.Clear();
            }

            for (int i = 0; i < flushed.Count; i++)
            {
                var entry = flushed[i];
                Emit(entry.Value.Level, null, $"{entry.Key} (repeated x{entry.Value.Count})");
            }
        }

        private static void Emit(LogLevel level, Exception ex, string formatted)
        {
            if (ex != null)
            {
                if (level == LogLevel.Error) Logger.Error(ex, formatted);
                else Logger.Warn(ex, formatted);
                return;
            }
            if (level == LogLevel.Error) Logger.Error(formatted);
            else if (level == LogLevel.Warn) Logger.Warn(formatted);
            else if (level == LogLevel.Debug) Logger.Debug(formatted);
            else Logger.Info(formatted);
        }
    }
}
