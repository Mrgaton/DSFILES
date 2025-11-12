using Microsoft.AspNetCore.Http;
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

        public static async void HandleWebSocket(HttpContext ctx, WebSocket ws)
        { 
            var poolKey = BitConverter.ToInt128(MD5.HashData(Encoding.UTF8.GetBytes(ctx.Request.Path)), 0);

            Console.WriteLine($"Client connected to pool: {poolKey}");

            if (!clients.ContainsKey(poolKey))
            {
                clients[poolKey] = new List<WebSocket>();
            }

            //DisableUtf8Validation(socket);

            var pool = clients[poolKey];

            pool.Add(ws);

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

            _ = HandleClient(ws, poolKey);
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
                while (socket.State == WebSocketState.Connecting)
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

                await socket.CloseAsync(WebSocketCloseStatus.InternalServerError, "Error occurred:\n\n" + ex.ToString(), CancellationToken.None);

                Console.WriteLine("WebSocket Exception: " + ex.ToString());
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pool);
            }
        }
    }
}