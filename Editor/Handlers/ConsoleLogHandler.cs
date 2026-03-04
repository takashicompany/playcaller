using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using Playcaller.Editor.Models;

namespace Playcaller.Editor.Handlers
{
	public static class ConsoleLogHandler
	{
		// Reflection members for accessing internal LogEntries/LogEntry
		private static MethodInfo _startGettingEntries;
		private static MethodInfo _endGettingEntries;
		private static MethodInfo _getEntryInternal;
		private static MethodInfo _clear;
		private static PropertyInfo _consoleFlags;
		private static FieldInfo _modeField;
		private static FieldInfo _messageField;
		private static Type _logEntryType;
		private static bool _reflectionReady;

		// LogEntry.mode bits
		private const int kError = 1 << 0;
		private const int kWarning = 1 << 2;
		private const int kLog = 1 << 3;
		private const int kScriptingError = 1 << 9;
		private const int kScriptingWarning = 1 << 10;
		private const int kScriptingLog = 1 << 11;
		private const int kScriptCompileError = 1 << 12;
		private const int kScriptCompileWarning = 1 << 13;
		private const int kScriptingException = 1 << 18;

		// Console flag bits (for enabling all log type visibility)
		private const int kConsoleFlagLogLevelLog = 1 << 7;
		private const int kConsoleFlagLogLevelWarning = 1 << 8;
		private const int kConsoleFlagLogLevelError = 1 << 9;

		[UnityEditor.InitializeOnLoadMethod]
		private static void InitReflection()
		{
			try
			{
				var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
				var instFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

				Type logEntriesType = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntries");
				if (logEntriesType == null)
				{
					Debug.LogError("[Playcaller] Could not find UnityEditor.LogEntries");
					return;
				}

				_startGettingEntries = logEntriesType.GetMethod("StartGettingEntries", flags);
				_endGettingEntries = logEntriesType.GetMethod("EndGettingEntries", flags);
				_getEntryInternal = logEntriesType.GetMethod("GetEntryInternal", flags);
				_clear = logEntriesType.GetMethod("Clear", flags);
				_consoleFlags = logEntriesType.GetProperty("consoleFlags", flags);

				_logEntryType = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntry");
				if (_logEntryType == null)
				{
					Debug.LogError("[Playcaller] Could not find UnityEditor.LogEntry");
					return;
				}

				_modeField = _logEntryType.GetField("mode", instFlags);
				_messageField = _logEntryType.GetField("message", instFlags);

				if (_startGettingEntries == null || _endGettingEntries == null ||
				    _getEntryInternal == null || _clear == null ||
				    _consoleFlags == null ||
				    _modeField == null || _messageField == null)
				{
					Debug.LogError("[Playcaller] Failed to reflect some LogEntries members");
					return;
				}

				_reflectionReady = true;
			}
			catch (Exception ex)
			{
				Debug.LogError($"[Playcaller] LogEntries reflection failed: {ex.Message}");
			}
		}

		public static string Handle(PlaycallerCommand command)
		{
			if (!_reflectionReady)
			{
				return PlaycallerResponse.Error(command.Id,
					"LogEntries reflection not initialized (v3)", "REFLECTION_ERROR");
			}

			try
			{
				int count = command.Params?["count"]?.ToObject<int>() ?? 50;
				string level = command.Params?["level"]?.ToString() ?? "all";
				bool clear = command.Params?["clear"]?.ToObject<bool>() ?? false;

				var entries = ReadLogEntries(level, count);
				int totalCount = entries.Count;

				if (clear)
				{
					_clear.Invoke(null, null);
				}

				return PlaycallerResponse.Success(command.Id, new
				{
					logs = entries,
					totalCount = totalCount
				});
			}
			catch (Exception ex)
			{
				return PlaycallerResponse.Error(command.Id,
					$"Console log failed: {ex.Message}", "CONSOLE_LOG_ERROR");
			}
		}

		private static List<object> ReadLogEntries(string levelFilter, int maxCount)
		{
			var result = new List<object>();

			// Save current console flags and enable all log types
			int savedFlags = (int)_consoleFlags.GetValue(null);
			int allTypesFlags = savedFlags
				| kConsoleFlagLogLevelLog
				| kConsoleFlagLogLevelWarning
				| kConsoleFlagLogLevelError;
			_consoleFlags.SetValue(null, allTypesFlags);

			try
			{
				int total = (int)_startGettingEntries.Invoke(null, null);
				object logEntry = Activator.CreateInstance(_logEntryType);

				// Collect matching entries from end (most recent first)
				var matched = new List<object>();

				for (int i = total - 1; i >= 0 && matched.Count < maxCount; i--)
				{
					_getEntryInternal.Invoke(null, new object[] { i, logEntry });

					int mode = (int)_modeField.GetValue(logEntry);
					string message = (string)_messageField.GetValue(logEntry);

					if (string.IsNullOrEmpty(message)) continue;

					string entryLevel = ClassifyLevel(mode, message);

					if (levelFilter != "all" && entryLevel != levelFilter) continue;

					// Take first line only
					int newlineIdx = message.IndexOf('\n');
					string firstLine = newlineIdx >= 0 ? message.Substring(0, newlineIdx) : message;

					matched.Add(new { level = entryLevel, message = firstLine });
				}

				// Reverse to chronological order
				matched.Reverse();
				result = matched;
			}
			finally
			{
				try { _endGettingEntries.Invoke(null, null); }
				catch { }

				// Restore original console flags
				_consoleFlags.SetValue(null, savedFlags);
			}

			return result;
		}

		private static string ClassifyLevel(int mode, string message)
		{
			// Compiler diagnostics: message content is more reliable than mode bits
			if (message.IndexOf(": error CS", StringComparison.OrdinalIgnoreCase) >= 0)
				return "error";
			if (message.IndexOf(": warning CS", StringComparison.OrdinalIgnoreCase) >= 0)
				return "warning";

			// Mode bits for non-compiler messages
			if ((mode & (kScriptCompileError | kScriptingError | kError | kScriptingException)) != 0)
				return "error";
			if ((mode & (kScriptCompileWarning | kScriptingWarning | kWarning)) != 0)
				return "warning";
			if ((mode & (kScriptingLog | kLog)) != 0)
				return "log";

			return "log";
		}
	}
}
