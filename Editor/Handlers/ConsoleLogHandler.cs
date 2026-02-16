using System;
using System.Collections.Generic;
using UnityEngine;
using PlayCaller.Editor.Models;

namespace PlayCaller.Editor.Handlers
{
    public static class ConsoleLogHandler
    {
        private static readonly List<LogEntry> _logs = new List<LogEntry>();
        private static readonly object _logLock = new object();
        private static bool _registered;

        private class LogEntry
        {
            public string level;
            public string message;
            public string timestamp;
        }

        [UnityEditor.InitializeOnLoadMethod]
        private static void Register()
        {
            if (_registered) return;
            Application.logMessageReceived += OnLogMessage;
            _registered = true;
        }

        private static void OnLogMessage(string message, string stackTrace, LogType type)
        {
            var entry = new LogEntry
            {
                message = message,
                timestamp = DateTime.UtcNow.ToString("o")
            };

            switch (type)
            {
                case LogType.Error:
                case LogType.Exception:
                case LogType.Assert:
                    entry.level = "error";
                    break;
                case LogType.Warning:
                    entry.level = "warning";
                    break;
                default:
                    entry.level = "log";
                    break;
            }

            lock (_logLock)
            {
                _logs.Add(entry);
                // Keep max 1000 entries
                while (_logs.Count > 1000)
                    _logs.RemoveAt(0);
            }
        }

        public static string Handle(PlayCallerCommand command)
        {
            try
            {
                int count = command.Params?["count"]?.ToObject<int>() ?? 50;
                string level = command.Params?["level"]?.ToString() ?? "all";
                bool clear = command.Params?["clear"]?.ToObject<bool>() ?? false;

                List<LogEntry> result;
                int totalCount;

                lock (_logLock)
                {
                    List<LogEntry> filtered;
                    if (level == "all")
                    {
                        filtered = new List<LogEntry>(_logs);
                    }
                    else
                    {
                        filtered = _logs.FindAll(e => e.level == level);
                    }

                    totalCount = filtered.Count;

                    // Take last N entries
                    int start = Math.Max(0, filtered.Count - count);
                    result = filtered.GetRange(start, filtered.Count - start);

                    if (clear)
                        _logs.Clear();
                }

                return PlayCallerResponse.Success(command.Id, new
                {
                    logs = result,
                    totalCount = totalCount
                });
            }
            catch (Exception ex)
            {
                return PlayCallerResponse.Error(command.Id,
                    $"Console log failed: {ex.Message}", "CONSOLE_LOG_ERROR");
            }
        }
    }
}
