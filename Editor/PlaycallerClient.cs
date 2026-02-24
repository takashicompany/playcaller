using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json;
using Playcaller.Editor.Models;

namespace Playcaller.Editor
{
	[InitializeOnLoad]
	public static class PlaycallerClient
	{
		private const string PortFileName = "Playcaller.port";

		private static TcpClient _client;
		private static NetworkStream _stream;
		private static CancellationTokenSource _cts;
		private static Task _receiveTask;

		private static readonly Queue<PlaycallerCommand> _commandQueue = new Queue<PlaycallerCommand>();
		private static readonly object _queueLock = new object();
		private static bool _isProcessing;

		// 再接続スケジュール (秒)
		private static readonly float[] ReconnectSchedule = { 0f, 1f, 3f, 5f, 10f };
		private static int _reconnectAttempt;
		private static double _nextReconnectTime;

		private static string GetPortFilePath()
		{
			var projectRoot = System.IO.Path.GetDirectoryName(Application.dataPath);
			return System.IO.Path.Combine(projectRoot, "Library", "Playcaller", PortFileName);
		}

		private static int ReadPortFile()
		{
			var path = GetPortFilePath();
			if (System.IO.File.Exists(path))
			{
				var text = System.IO.File.ReadAllText(path).Trim();
				if (int.TryParse(text, out var port) && port > 0)
					return port;
			}
			return 0;
		}

		static PlaycallerClient()
		{
			if (ReadPortFile() > 0)
				Debug.Log("[Playcaller] MCP サーバーを検出しました。接続しています...");
			else
				Debug.Log("[Playcaller] MCP サーバーが見つかりません。Claude Code で playcaller MCP を設定してください。");
			EditorApplication.update += Update;
			EditorApplication.quitting += OnQuitting;
			AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
			_reconnectAttempt = 0;
			_nextReconnectTime = EditorApplication.timeSinceStartup;
		}

		private static void Update()
		{
			ProcessCommandQueue();

			// ReceiveLoop が終了していれば接続が切れている → クリーンアップ
			if (_receiveTask != null && _receiveTask.IsCompleted)
			{
				CleanupConnection();
				_receiveTask = null;
				_cts = null;
			}

			if (_client == null || !_client.Connected)
			{
				TryReconnect();
			}
		}

		private static void TryReconnect()
		{
			if (EditorApplication.timeSinceStartup < _nextReconnectTime) return;

			int port = ReadPortFile();
			if (port <= 0)
			{
				// ポートファイルがない → Python サーバー未起動
				ScheduleNextReconnect();
				return;
			}

			try
			{
				_client = new TcpClient();
				_client.Connect(IPAddress.Loopback, port);
				_stream = _client.GetStream();
				_cts = new CancellationTokenSource();
				_receiveTask = Task.Run(() => ReceiveLoop(_cts.Token));
				_reconnectAttempt = 0;
				Debug.Log($"[Playcaller] Connected to Python server at 127.0.0.1:{port}");
			}
			catch (Exception ex)
			{
				Debug.Log($"[Playcaller] Connection failed: {ex.Message}");
				CleanupConnection();
				ScheduleNextReconnect();
			}
		}

		private static void ScheduleNextReconnect()
		{
			int idx = Mathf.Min(_reconnectAttempt, ReconnectSchedule.Length - 1);
			float delay = ReconnectSchedule[idx];
			_nextReconnectTime = EditorApplication.timeSinceStartup + delay;
			_reconnectAttempt++;
		}

		private static async Task ReceiveLoop(CancellationToken ct)
		{
			var buffer = new byte[4096];
			var messageBuffer = new List<byte>();

			try
			{
				while (!ct.IsCancellationRequested && _client != null && _client.Connected)
				{
					var bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, ct);
					if (bytesRead == 0) break;

					for (int i = 0; i < bytesRead; i++)
						messageBuffer.Add(buffer[i]);

					// Process complete framed messages
					while (messageBuffer.Count >= 4)
					{
						var lengthBytes = messageBuffer.GetRange(0, 4).ToArray();
						if (BitConverter.IsLittleEndian)
							Array.Reverse(lengthBytes);
						var messageLength = BitConverter.ToInt32(lengthBytes, 0);

						if (messageLength < 0 || messageLength > 1024 * 1024)
						{
							Debug.LogError($"[Playcaller] Invalid message length: {messageLength}");
							messageBuffer.Clear();
							break;
						}

						if (messageBuffer.Count >= 4 + messageLength)
						{
							var messageBytes = messageBuffer.GetRange(4, messageLength).ToArray();
							messageBuffer.RemoveRange(0, 4 + messageLength);

							var json = Encoding.UTF8.GetString(messageBytes);

							try
							{
								var command = JsonConvert.DeserializeObject<PlaycallerCommand>(json);
								if (command != null)
								{
									lock (_queueLock)
									{
										_commandQueue.Enqueue(command);
									}
								}
								else
								{
									var errResp = PlaycallerResponse.Error(null, "Invalid command format", "PARSE_ERROR");
									SendFramedMessage(errResp);
								}
							}
							catch (JsonException ex)
							{
								var errResp = PlaycallerResponse.Error(null, $"JSON parsing error: {ex.Message}", "JSON_ERROR");
								SendFramedMessage(errResp);
							}
						}
						else
						{
							break;
						}
					}
				}
			}
			catch (Exception ex)
			{
				if (!ct.IsCancellationRequested)
					Debug.LogError($"[Playcaller] Receive loop error: {ex}");
			}
			finally
			{
				Debug.Log("[Playcaller] Disconnected from Python server");
			}
		}

		private static void SendFramedMessage(string message)
		{
			try
			{
				if (_stream == null || _client == null || !_client.Connected) return;

				var messageBytes = Encoding.UTF8.GetBytes(message);
				var lengthBytes = BitConverter.GetBytes(messageBytes.Length);
				if (BitConverter.IsLittleEndian) Array.Reverse(lengthBytes);
				_stream.Write(lengthBytes, 0, 4);
				_stream.Write(messageBytes, 0, messageBytes.Length);
				_stream.Flush();
			}
			catch (Exception ex)
			{
				Debug.LogError($"[Playcaller] Send error: {ex}");
			}
		}

		private static void ProcessCommandQueue()
		{
			if (_isProcessing) return;

			PlaycallerCommand command;
			lock (_queueLock)
			{
				if (_commandQueue.Count == 0) return;
				command = _commandQueue.Dequeue();
				_isProcessing = true;
			}

			ProcessCommandAsync(command);
		}

		private const int CommandTimeoutMs = 30000;

		private static async void ProcessCommandAsync(PlaycallerCommand command)
		{
			try
			{
				var response = CommandRouter.Route(command);

				// If the handler returns a Task<string>, await it with timeout
				if (response is Task<string> asyncResponse)
				{
					var completed = await Task.WhenAny(asyncResponse, Task.Delay(CommandTimeoutMs));
					if (completed != asyncResponse)
					{
						Debug.LogWarning($"[Playcaller] Command {command?.Type} timed out after {CommandTimeoutMs}ms");
						var errResp = PlaycallerResponse.Error(command?.Id, "Command timed out", "TIMEOUT");
						SendFramedMessage(errResp);
						return;
					}
					var result = await asyncResponse;
					SendFramedMessage(result);
				}
				else if (response is string syncResponse)
				{
					SendFramedMessage(syncResponse);
				}
			}
			catch (Exception ex)
			{
				Debug.LogError($"[Playcaller] Error processing command {command?.Type}: {ex}");
				try
				{
					var errResp = PlaycallerResponse.Error(command?.Id, $"Internal error: {ex.Message}", "INTERNAL_ERROR");
					SendFramedMessage(errResp);
				}
				catch { }
			}
			finally
			{
				_isProcessing = false;
			}
		}

		private static void CleanupConnection()
		{
			_stream = null;
			try { _client?.Close(); } catch { }
			_client = null;
		}

		private static void Disconnect()
		{
			Debug.Log("[Playcaller] Disconnecting...");
			_cts?.Cancel();
			try { _receiveTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
			CleanupConnection();
			_cts = null;
			_receiveTask = null;
		}

		private static void OnBeforeAssemblyReload()
		{
			Debug.Log("[Playcaller] Assembly reload: disconnecting...");
			Disconnect();
			EditorApplication.update -= Update;
		}

		private static void OnQuitting()
		{
			Debug.Log("[Playcaller] Quitting: shutting down...");
			Disconnect();
			// ポートファイルは Python 側が管理するため、ここでは削除しない
			EditorApplication.update -= Update;
			EditorApplication.quitting -= OnQuitting;
		}
	}
}
