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
            var poolKey = BitConverter.ToInt128(MD5.HashData(Encoding.UTF8.GetBytes(context.RequestUri.PathAndQuery)), 0);

            Console.WriteLine($"Client connected to pool: {poolKey}");

            if (!clients.ContainsKey(poolKey))
            {
                clients[poolKey] = new List<WebSocket>();
            }

            var socket = context.WebSocket;

            //DisableUtf8Validation(socket);

            clients[poolKey].Add(socket);

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
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    using (MemoryStream recivedStream = new())
                    {
                        WebSocketReceiveResult result;

                        byte[] buffer = new byte[1024 * 4];

                        do
                        {
                            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                            await recivedStream.WriteAsync(buffer, 0, result.Count);
                        }
                        while (!result.EndOfMessage && recivedStream.Length < 1024 * 1024);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            clients[poolKey].Remove(socket);

                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", CancellationToken.None);

                            Console.WriteLine($"Client disconnected from pool: {poolKey}");
                        }
                        else
                        {
                            Console.WriteLine($"Message received in pool {poolKey}: {recivedStream.Length}");

                            await BroadcastMessage(poolKey, socket, result.MessageType, recivedStream.ToArray());
                        }
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

                await socket.CloseAsync(WebSocketCloseStatus.InternalServerError, "Error occurred", CancellationToken.None);

                Console.WriteLine("WebSocket Exception: " + ex.ToString());
            }
        }

        private static async Task BroadcastMessage(Int128 poolKey, WebSocket sender, WebSocketMessageType type, byte[] message)
        {
            foreach (var client in clients[poolKey])
            {
                if (client.State == WebSocketState.Open) // client != sender &&
                {
                    await client.SendAsync(message, type, true, CancellationToken.None);
                }
            }
        }
    }
}