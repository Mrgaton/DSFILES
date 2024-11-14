using System.Net.WebSockets;

namespace DSFiles_Server.Routes
{
    internal class WebSocketHandler
    {
        private static Dictionary<string, List<WebSocket>> clients = new();

        public static async void HandleWebSocket(HttpListenerWebSocketContext context)
        {
            var poolKey = context.RequestUri.PathAndQuery;

            Console.WriteLine($"Client connected to pool: {poolKey}");

            if (!clients.ContainsKey(poolKey))
            {
                clients[poolKey] = new List<WebSocket>();
            }

            var socket = context.WebSocket;

            clients[poolKey].Add(socket);

            _ = HandleClient(socket, poolKey);
        }

        private static async Task HandleClient(WebSocket socket, string poolKey)
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
            catch (WebSocketException)
            {
                clients[poolKey].Remove(socket);

                await socket.CloseAsync(WebSocketCloseStatus.InternalServerError, "Error occurred", CancellationToken.None);
            }
        }

        private static async Task BroadcastMessage(string poolKey, WebSocket sender, WebSocketMessageType type, byte[] message)
        {
            foreach (var client in clients[poolKey])
            {
                if (client != sender && client.State == WebSocketState.Open)
                {
                    await client.SendAsync(new ArraySegment<byte>(message), type, true, CancellationToken.None);
                }
            }
        }
    }
}