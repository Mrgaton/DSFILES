using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace DSFiles_Server.Routes
{
    internal static class WebSocketHandler
    {
        private static ConcurrentDictionary<Int128, List<WebSocket>> clients = new();

        public static async void HandleWebSocket(HttpListenerWebSocketContext context)
        {
            using HMACMD5 hmacMd5 = new HMACMD5([85, 214, 56, 99, 11, 252, 114, 201, 11, 89, 211, 226, 219, 113, 129, 104, 232, 155, 165, 63, 106, 217, 143, 207, 46, 150, 254, 60, 152, 32, 153, 6, 181, 3, 102, 141, 168, 198, 142, 179, 39, 231, 110, 172, 252, 153, 246, 1, 245, 230, 22, 202, 219, 100, 214, 162, 3, 228, 197, 41, 158, 229, 215, 136, 86, 201, 122, 138, 214, 45, 141, 154, 198, 55, 38, 166, 231, 154, 177, 45, 194, 48, 232, 64, 95, 209, 96, 144, 177, 244, 11, 3, 175, 199, 79, 202, 31, 31, 99, 171, 176, 96, 151, 114, 182, 251, 183, 68, 9, 25, 231, 30, 42, 122, 73, 140, 228, 49, 145, 22, 178, 230, 218, 28, 250, 131, 97, 67, 216]);
            var poolKey = BitConverter.ToInt128(hmacMd5.ComputeHash(Encoding.UTF8.GetBytes(context.RequestUri.PathAndQuery)), 0);

            Console.WriteLine($"Client connected to pool: {poolKey}");

            if (!clients.ContainsKey(poolKey))
            {
                clients[poolKey] = new List<WebSocket>();
            }

            var socket = context.WebSocket;

            //DisableUtf8Validation(socket);

            var pool = clients[poolKey];

            pool.Add(socket);

            foreach (var client in pool)
            {
                try
                {
                    if (client.State != WebSocketState.Open && client.State != WebSocketState.Connecting)
                    {
                        pool.Remove(client);
                    }
                }
                catch { }
            }

            _ = HandleClient(socket, poolKey);

        }

        public static void DisableUtf8Validation(WebSocket webSocket)
        {
            var validateUtf8Field = webSocket.GetType().GetField("_validateUtf8", BindingFlags.NonPublic | BindingFlags.Instance);

            if (validateUtf8Field != null)
            {
                validateUtf8Field.SetValue(webSocket, false);
            }
            else
            {
                throw new InvalidOperationException("No se encontró el campo '_validateUtf8'.");
            }
        }

        private static async Task HandleClient(WebSocket socket, Int128 poolKey)
        {
            byte[]? pool = ArrayPool<byte>.Shared.Rent(ushort.MaxValue);

            try
            {
                while(socket.State == WebSocketState.Connecting)
                {
                    await Task.Delay(500);
                }

                while (socket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result = null;

                    try
                    {
                        while (socket.State == WebSocketState.Open && (result == null || !result.EndOfMessage))
                        {
                            result = await socket.ReceiveAsync(pool, CancellationToken.None);

                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                clients[poolKey].Remove(socket);

                                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", CancellationToken.None);
                                Console.WriteLine($"Client disconnected from pool: {poolKey}");
                                break;
                            }
                            else
                            {
                                foreach (var client in clients[poolKey])
                                {
                                    if (client.State == WebSocketState.Open) // client != sender &&
                                    {
                                        try
                                        {
                                            await client.SendAsync(new ArraySegment<byte>(pool, 0, result.Count), result.MessageType, result.EndOfMessage, CancellationToken.None);
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.Error.WriteLine(ex.ToString());
                                        }
                                    }
                                }
                            }

                        }
                    }
                    catch (Exception ex)
                    {
                        if (socket.State == WebSocketState.Aborted)
                            break;

                        Console.Error.WriteLine(ex.ToString());
                    }
                }
            }
            catch (WebSocketException ex)
            {
                clients[poolKey].Remove(socket);

                if (clients[poolKey].Count <= 0)
                {
                    clients.TryRemove(poolKey, out _);
                }

                await socket.CloseAsync(WebSocketCloseStatus.InternalServerError, "Error occurred:\n\n" + ex.ToString() , CancellationToken.None);

                Console.WriteLine("WebSocket Exception: " + ex.ToString());
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pool);
            }
        }
    }
}