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
using PlayCaller.Editor.Models;

namespace PlayCaller.Editor
{
    [InitializeOnLoad]
    public static class PlayCallerServer
    {
        private const int Port = 6500;
        private static TcpListener _listener;
        private static CancellationTokenSource _cts;
        private static Task _listenerTask;

        private static readonly Queue<(PlayCallerCommand command, TcpClient client)> _commandQueue
            = new Queue<(PlayCallerCommand, TcpClient)>();
        private static readonly object _queueLock = new object();
        private static bool _isProcessing;

        static PlayCallerServer()
        {
            Debug.Log("[PlayCaller] Initializing...");
            EditorApplication.update += ProcessCommandQueue;
            EditorApplication.quitting += Shutdown;
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            StartListener();
        }

        private static void StartListener()
        {
            try
            {
                if (_listener != null) StopListener();

                _cts = new CancellationTokenSource();
                _listener = new TcpListener(IPAddress.Loopback, Port);
                _listener.Start();
                Debug.Log($"[PlayCaller] TCP listening on 127.0.0.1:{Port}");

                _listenerTask = Task.Run(() => AcceptConnectionsAsync(_cts.Token));
            }
            catch (SocketException ex)
            {
                Debug.LogError($"[PlayCaller] Failed to start TCP listener on port {Port}: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PlayCaller] Unexpected error starting TCP listener: {ex}");
            }
        }

        private static void StopListener()
        {
            try
            {
                _cts?.Cancel();
                _listener?.Stop();
                _listenerTask?.Wait(TimeSpan.FromSeconds(1));
                _listener = null;
                _cts = null;
                _listenerTask = null;
                Debug.Log("[PlayCaller] TCP listener stopped");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PlayCaller] Error stopping TCP listener: {ex}");
            }
        }

        private static async Task AcceptConnectionsAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await AcceptClientAsync(_listener, ct);
                    if (client != null)
                    {
                        Debug.Log($"[PlayCaller] Client connected from {client.Client.RemoteEndPoint}");
                        _ = Task.Run(() => HandleClientAsync(client, ct));
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                        Debug.LogError($"[PlayCaller] Error accepting connection: {ex}");
                }
            }
        }

        private static async Task<TcpClient> AcceptClientAsync(TcpListener listener, CancellationToken ct)
        {
            using (ct.Register(() => listener.Stop()))
            {
                try
                {
                    return await listener.AcceptTcpClientAsync();
                }
                catch (ObjectDisposedException) when (ct.IsCancellationRequested)
                {
                    return null;
                }
            }
        }

        private static async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            try
            {
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 30000;

                var buffer = new byte[4096];
                var stream = client.GetStream();
                var messageBuffer = new List<byte>();

                while (!ct.IsCancellationRequested && client.Connected)
                {
                    var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
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
                            Debug.LogError($"[PlayCaller] Invalid message length: {messageLength}");
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
                                var command = JsonConvert.DeserializeObject<PlayCallerCommand>(json);
                                if (command != null)
                                {
                                    lock (_queueLock)
                                    {
                                        _commandQueue.Enqueue((command, client));
                                    }
                                }
                                else
                                {
                                    var errResp = PlayCallerResponse.Error(null, "Invalid command format", "PARSE_ERROR");
                                    await SendFramedMessageAsync(stream, errResp, ct);
                                }
                            }
                            catch (JsonException ex)
                            {
                                var errResp = PlayCallerResponse.Error(null, $"JSON parsing error: {ex.Message}", "JSON_ERROR");
                                await SendFramedMessageAsync(stream, errResp, ct);
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
                    Debug.LogError($"[PlayCaller] Client handler error: {ex}");
            }
            finally
            {
                client?.Close();
                Debug.Log("[PlayCaller] Client disconnected");
            }
        }

        public static async Task SendFramedMessageAsync(NetworkStream stream, string message, CancellationToken ct)
        {
            try
            {
                var messageBytes = Encoding.UTF8.GetBytes(message);
                var lengthBytes = BitConverter.GetBytes(messageBytes.Length);
                if (BitConverter.IsLittleEndian) Array.Reverse(lengthBytes);
                await stream.WriteAsync(lengthBytes, 0, 4, ct);
                await stream.WriteAsync(messageBytes, 0, messageBytes.Length, ct);
                await stream.FlushAsync(ct);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PlayCaller] Send error: {ex}");
                throw;
            }
        }

        private static void ProcessCommandQueue()
        {
            if (_isProcessing) return;

            (PlayCallerCommand command, TcpClient client) item;
            lock (_queueLock)
            {
                if (_commandQueue.Count == 0) return;
                item = _commandQueue.Dequeue();
                _isProcessing = true;
            }

            ProcessCommandAsync(item.command, item.client);
        }

        private static async void ProcessCommandAsync(PlayCallerCommand command, TcpClient client)
        {
            try
            {
                var response = CommandRouter.Route(command);

                // If the handler returns a Task<string>, await it
                if (response is Task<string> asyncResponse)
                {
                    var result = await asyncResponse;
                    if (client.Connected)
                        await SendFramedMessageAsync(client.GetStream(), result, CancellationToken.None);
                }
                else if (response is string syncResponse)
                {
                    if (client.Connected)
                        await SendFramedMessageAsync(client.GetStream(), syncResponse, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PlayCaller] Error processing command {command?.Type}: {ex}");
                try
                {
                    if (client.Connected)
                    {
                        var errResp = PlayCallerResponse.Error(command?.Id, $"Internal error: {ex.Message}", "INTERNAL_ERROR");
                        await SendFramedMessageAsync(client.GetStream(), errResp, CancellationToken.None);
                    }
                }
                catch { }
            }
            finally
            {
                _isProcessing = false;
            }
        }

        private static void Shutdown()
        {
            Debug.Log("[PlayCaller] Shutting down...");
            StopListener();
            EditorApplication.update -= ProcessCommandQueue;
            EditorApplication.quitting -= Shutdown;
        }
    }
}
