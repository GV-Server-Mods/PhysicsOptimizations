using System;
using NLog;

namespace PhysicsOptimizer.Utils
{
    /// <summary>
    /// Central logging facade. One NLog logger ("PhysicsOptimizer") so Torch prints a uniform
    /// `PhysicsOptimizer:` prefix; the feature tag renders as `[Tag] message`.
    /// Tag must be the declaring class's LogSource constant - never inline ad-hoc tags.
    /// </summary>
    public static class Log
    {
        private static readonly ILogger Logger = LogManager.GetLogger("PhysicsOptimizer");

        private static string Fmt(string source, string message) => $"[{source}] {message}";

        public static void Info(string source, string message) => Logger.Info(Fmt(source, message));
        public static void Warn(string source, string message) => Logger.Warn(Fmt(source, message));
        public static void Warn(Exception ex, string source, string message) => Logger.Warn(ex, Fmt(source, message));
        public static void Error(string source, string message) => Logger.Error(Fmt(source, message));
        public static void Error(Exception ex, string source, string message) => Logger.Error(ex, Fmt(source, message));
        public static void Debug(string source, string message) => Logger.Debug(Fmt(source, message));
    }
}
