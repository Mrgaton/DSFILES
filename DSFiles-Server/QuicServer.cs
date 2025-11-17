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
{/*
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
        

    }*/

}