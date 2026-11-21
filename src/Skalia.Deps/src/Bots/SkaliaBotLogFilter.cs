using System;
using Microsoft.Extensions.Logging;

namespace SkaliaDeps
{
    // 0.1.0-y17.30 (the launcher's "Log dos bots", Developer > Bot logs, off
    // by default): serverBot:botLogs in config.yaml. Off (or missing), the
    // bots' Information and Debug lines are dropped and Warning and Error
    // still go out. Every bot logger comes from the one the authentication
    // service is given (the simulations, the movement and life diagnostics,
    // the map builders), so wrapping that one covers them all.
    internal sealed class SkaliaBotLogFilter<T> : ILogger<T>
    {
        private readonly ILogger<T> m_Inner;
        private readonly LogLevel m_Minimum;

        public SkaliaBotLogFilter(ILogger<T> inner, LogLevel minimum)
        {
            m_Inner = inner;
            m_Minimum = minimum;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return m_Inner.BeginScope(state);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= m_Minimum && m_Inner.IsEnabled(logLevel);
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel < m_Minimum)
            {
                return;
            }
            m_Inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
