using Microsoft.AspNetCore.Mvc.TagHelpers.Cache;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Quic;
using System.Security.Cryptography;
using System.Text;

namespace DSFiles_Server
{
    internal class QuicServer
    {
        private const ushort CurrentVersion = 50001;
        private const int SignatureSize = 2420;

        private static readonly ConcurrentDictionary<UInt128, ConcurrentDictionary<QuicStream, Client>> _connections = new();
        
        public class Client
        {
            public QuicConnection Connection { get; set; }
            public UInt128 Id { get; set; }
            public MLDsa MLDsa { get; set; }
            public byte[] PublicKey { get; set; }
        }
        
        public static async Task AcceptLoopAsync(QuicListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var conn = await listener.AcceptConnectionAsync(ct);
                    Console.WriteLine($"Accepted {conn.RemoteEndPoint}");

                    var stream = await conn.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct);

                    await Verify(conn, stream, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"Accept error: {ex}");

                    await Task.Delay(1000, ct);
                }
            }
        }
        private static async Task Verify(QuicConnection qc, QuicStream qs, CancellationToken ct)
        {
            using (BinaryReader br = new(qs, Encoding.UTF8, leaveOpen: true))
            {
                ushort version = br.ReadUInt16();

                if (version != CurrentVersion)
                {
                    br.Close();

                    throw new Exception("Version mismatch");
                }

                var publicKey = br.ReadBytes(1312);                
                var mldsa = MLDsa.ImportSubjectPublicKeyInfo(publicKey);

                using (MemoryStream ms = new MemoryStream())
                {
                    byte[] buffer = br.ReadBytes(16);
                    UInt128 pool = UInt128.Parse(buffer);
                    ms.Write(buffer, 0, 16);

                    buffer = br.ReadBytes(16);
                    UInt128 id = UInt128.Parse(buffer);
                    ms.Write(buffer, 0, 16);

                    byte flags = br.ReadByte();
                    ms.WriteByte(flags);

                    for (int i = 0; i < flags; i++)
                    {
                        byte keySize = br.ReadByte();
                        ms.WriteByte(keySize);

                        var key = br.ReadBytes(keySize);
                        ms.Write(key);

                        buffer = br.ReadBytes(4);
                        ushort contentLength = Convert.ToUInt16(buffer);
                        ms.Write(buffer, 0, 4);

                        var value = br.ReadBytes(contentLength);
                        ms.Write(value);
                    }

                    var signature = br.ReadBytes(SignatureSize);

                    if (!mldsa.VerifyData(ms.ToArray(), signature))
                    {
                        br.Close();
                        qs.Close();

                        throw new Exception("Signature verification failed");
                    }

                    Client c = new Client()
                    {
                        Id = id,
                        MLDsa = mldsa,
                        PublicKey = publicKey,
                        Connection = qc
                    };

                    _connections[id].TryAdd(qs, c);
                    await Listen(qs, c, ct);
                }
            }
        }
        private static async Task Listen(QuicStream qs, Client c, CancellationToken ct)
        {
            using (BinaryReader br = new(qs, Encoding.UTF8, leaveOpen: false))
            {
                while (!ct.IsCancellationRequested)
                {
                    ushort version = br.ReadUInt16();

                    if (version != CurrentVersion)
                    {
                        br.Close();

                        throw new Exception("Version mismatch");
                    }

                    int contentLength = br.ReadInt32();
                    byte[] content = br.ReadBytes(contentLength);

                    var signature = br.ReadBytes(SignatureSize);

                    if (!c.MLDsa.VerifyData(content, signature))
                    {
                        br.Close();
                        qs.Close();

                        throw new Exception("Signature verification failed");
                    }
                }
            }
        }

        /*private static async Task BroadCast(byte[] data, CancellationToken ct)
        {
            foreach (var conn in _connections)
            {
                try
                {
                    await conn.Value.WriteAsync(data, ct);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Broadcast to {conn.Key.RemoteEndPoint} failed: {ex.Message}");
                }
            }
        }*/

        /*
        static async Task HandleConnectionAsync(QuicConnection conn, CancellationToken ct)
        {
            try
            {
                // Start a registration reader: try to obtain client id from the first client-initiated stream.
                _ = Task.Run(() => TryRegisterConnectionAsync(conn, ct));

                // Start control sender (your existing behavior)
                _ = Task.Run(() => ServerControlSenderAsync(conn, ct));

                await using (conn)
                {
                    while (!ct.IsCancellationRequested && conn.)
                    {
                        QuicStream stream = null;
                        try
                        {
                            stream = await conn.AcceptStreamAsync(ct);
                            // Handle each stream concurrently
                            _ = Task.Run(() => HandleStreamAsync(stream, ct));
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Stream accept error: {ex.Message}");
                            stream?.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Connection handler error: {ex}");
            }
            finally
            {
                // clean up mapping when connection gone
                _connections.TryRemove(conn, out _);
                try { conn.Dispose(); } catch { }
                Console.WriteLine($"Connection closed and removed: {conn.RemoteEndPoint}");
            }
        }

        // Try to read first 4 bytes from a client-initiated stream to become the client id.
        // If the client doesn't send a registration stream, the id remains 0.
        static async Task TryRegisterConnectionAsync(QuicConnection conn, CancellationToken ct)
        {
            try
            {
                // Wait for first client-initiated stream (timeout optional)
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10)); // small timeout so we don't hang forever

                var stream = await conn.AcceptStreamAsync(cts.Token);
                await using (stream)
                {
                    // Read exactly 4 bytes (big-endian uint32) for client id
                    Span<byte> buf = stackalloc byte[4];
                    int read = 0;
                    while (read < 4)
                    {
                        var r = await stream.ReadAsync(buf[read..], ct);
                        if (r == 0) throw new EndOfStreamException();
                        read += r;
                    }
                    uint clientId = BinaryPrimitives.ReadUInt32BigEndian(buf);

                    // store id (update existing mapping)
                    _connections.AddOrUpdate(conn, clientId, (k, old) => clientId);
                    Console.WriteLine($"Registered connection {conn.RemoteEndPoint} -> clientId={clientId}");

                    // Optionally close that stream (registration completed).
                    // If you want to keep it open, remove this Close.
                    try { await stream.ShutdownWriteCompletedAsync(ct); } catch { }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"Registration error for {conn.RemoteEndPoint}: {ex.Message}");
            }
        }

        // Broadcast helper:
        // - sourceId: who is sending (your server or the original client)
        // - targetId: 0 = broadcast to all; otherwise send to the specific client id
        // - payload: payload bytes
        public static async Task BroadcastAsync(uint sourceId, uint targetId, ReadOnlyMemory<byte> payload)
        {
            // Build message: [4 src][4 target][4 len][payload]
            var header = new byte[12];
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), sourceId);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), targetId);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8, 4), payload.Length);

            var message = new byte[12 + payload.Length];
            Buffer.BlockCopy(header, 0, message, 0, 12);
            payload.CopyTo(message.AsMemory(12));

            var sendTasks = new List<Task>();

            // If targetId==0 -> broadcast to all connections
            if (targetId == 0)
            {
                foreach (var kv in _connections)
                {
                    var conn = kv.Key;
                    // skip closed connections
                    if (!conn.Connected) continue;

                    sendTasks.Add(SendUnidirectionalAsync(conn, message));
                }
            }
            else
            {
                // send only to matching clients (there may be multiple connections with same id)
                foreach (var kv in _connections)
                {
                    var conn = kv.Key;
                    var id = kv.Value;
                    if (id == targetId && conn.Connected)
                    {
                        sendTasks.Add(SendUnidirectionalAsync(conn, message));
                    }
                }
            }

            // fire-and-forget parallel writes, await to observe errors
            try
            {
                await Task.WhenAll(sendTasks);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Broadcast errors: {ex.Message}");
            }
        }

        // send a byte[] in a server-initiated unidirectional stream
        static async Task SendUnidirectionalAsync(QuicConnection conn, byte[] message)
        {
            try
            {
                // guard against connection being disposed between enumeration and open
                if (!conn.Connected) return;

                await using var stream = await conn.OpenUnidirectionalStreamAsync();
                // write entire message (we don't rely on framing helpers here)
                await stream.WriteAsync(message);
                // optional: gracefully shutdown write side so client sees EOF
                try { await stream.ShutdownWriteCompletedAsync(); } catch { }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Send to {conn.RemoteEndPoint} failed: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------------
        // Your existing handlers (example skeletons) - keep them or replace
        // ---------------------------------------------------------------------

        // simple stream handler (you probably already have something like this)
        static async Task HandleStreamAsync(QuicStream stream, CancellationToken ct)
        {
            await using (stream)
            {
                try
                {
                    // Example: read incoming data and optionally broadcast it.
                    // Here we read all bytes, treat them as UTF8 text, and broadcast to all.
                    var rent = ArrayPool<byte>.Shared.Rent(64 * 1024);
                    int totalRead = 0;
                    while (true)
                    {
                        var r = await stream.ReadAsync(rent.AsMemory(totalRead, rent.Length - totalRead), ct);
                        if (r == 0) break;
                        totalRead += r;
                        // simplistic - if buffer fills, process what's there
                        if (totalRead == rent.Length)
                        {
                            var data = new byte[totalRead];
                            Array.Copy(rent, data, totalRead);
                            await ProcessIncomingPayload(stream, data);
                            totalRead = 0;
                        }
                    }
                    if (totalRead > 0)
                    {
                        var data = new byte[totalRead];
                        Array.Copy(rent, data, totalRead);
                        await ProcessIncomingPayload(stream, data);
                    }
                    ArrayPool<byte>.Shared.Return(rent);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Stream {stream.StreamId} error: {ex.Message}");
                }
            }
        }

        // Example: interpret incoming payload as "broadcast request":
        // Expected client message: [4 bytes targetId][payload...]
        // If targetId==0 => server will broadcast payload to everyone; otherwise to that target.
        static async Task ProcessIncomingPayload(QuicStream stream, byte[] data)
        {
            if (data.Length < 4) return;
            uint targetId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4));
            var payload = data.AsMemory(4);

            // Look up sender id (if registered)
            uint senderId = 0;
            // find the connection owning this stream
            foreach (var kv in _connections)
            {
                // stream.Connection isn't available directly; we compare by StreamId -> not reliable.
                // Simpler: if you want accurate senderId, pass the QuicConnection into ProcessIncomingPayload
                // in your code. For this example we'll broadcast with senderId=0 (server).
            }

            // Broadcast (server-sourced) or use actual senderId if you pass it in.
            await BroadcastAsync(senderId, targetId, payload);
        }

        // Example control stream sender (your original routine)
        static async Task ServerControlSenderAsync(QuicConnection conn, CancellationToken ct)
        {
            try
            {
                await using var control = await conn.OpenUnidirectionalStreamAsync(ct);

                var versionText = "proto=quic-proto;version=1;max_streams=2048";
                var bytes = Encoding.UTF8.GetBytes(versionText);
                // write a 4-byte length + payload (simple control frame)
                var header = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
                await control.WriteAsync(header);
                await control.WriteAsync(bytes);

                while (!ct.IsCancellationRequested && conn.Connected)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), ct);
                    // keepalive ping (1-byte)
                    await control.WriteAsync(new byte[] { 0x00 });
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"Control sender error: {ex.Message}");
            }
        }
    }*/

    }

}