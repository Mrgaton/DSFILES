using DSFiles_Server;
using System.Collections.Concurrent;
using System.Reflection;

namespace DSFiles_Tests
{
    internal static class QuicRelayTests
    {
        internal static async Task Run()
        {
            UInt128 pool = 123456789;
            const ulong senderId = 123, receiverId = 456;
            var connections = (ConcurrentDictionary<UInt128, ConcurrentDictionary<ulong, QuicServer.Client>>)
                typeof(QuicServer).GetField("_connections", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            Require((ushort)typeof(QuicServer).GetField("CurrentVersion", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()! == 50002);

            using var incoming = new MemoryStream();
            using (var writer = new BinaryWriter(incoming, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write((ushort)42);
                writer.Write((byte)1);
                writer.Write(3);
                writer.Write(receiverId);
                writer.Write(new byte[] { 10, 20, 30 });
            }
            incoming.Position = 0;
            using var outgoing = new MemoryStream();
            var sender = new QuicServer.Client { Id = senderId, Pool = pool, Stream = incoming };
            var receiver = new QuicServer.Client { Id = receiverId, Pool = pool, Stream = outgoing };
            connections[pool] = new ConcurrentDictionary<ulong, QuicServer.Client>(
                [new(senderId, sender), new(receiverId, receiver)]);
            try
            {
                try
                {
                    await Invoke("ReadLoop", sender, CancellationToken.None);
                    throw new Exception("Expected end of test input.");
                }
                catch (EndOfStreamException) { }
                Require(receiver.QueuedPackets == 1 && receiver.QueuedBytes > 0);
                receiver.SendQueue.Writer.TryComplete();
                await Invoke("WriteLoop", receiver, CancellationToken.None);
                Require(receiver.QueuedPackets == 0 && receiver.QueuedBytes == 0);
                outgoing.Position = 0;
                using var reader = new BinaryReader(outgoing, System.Text.Encoding.UTF8, leaveOpen: true);
                Require(reader.ReadUInt16() == 42 && reader.ReadUInt64() == senderId);
                Require(reader.ReadByte() == 1 && reader.ReadInt32() == 3 && reader.ReadUInt64() == receiverId);
                Require(reader.ReadBytes(3).SequenceEqual(new byte[] { 10, 20, 30 }) && outgoing.Position == outgoing.Length);

                using var duplicate = new MemoryStream();
                using (var writer = new BinaryWriter(duplicate, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write((ulong)pool);
                    writer.Write(0UL);
                    writer.Write(senderId);
                    writer.Write((byte)0);
                }
                duplicate.Position = 0;
                try
                {
                    await QuicServer.Verify(null!, duplicate, CancellationToken.None);
                    throw new Exception("Duplicate QUIC client ID accepted.");
                }
                catch (InvalidOperationException) { }
                Require(ReferenceEquals(connections[pool][senderId], sender));
            }
            finally
            {
                connections.TryRemove(pool, out _);
            }

            var cancelled = new QuicServer.Client { Stream = Stream.Null };
            var payloads = new List<QuicServer.SharedPayload>();
            for (int i = 0; i < 3; i++)
            {
                var payload = QuicServer.SharedPayload.Rent(1024);
                payloads.Add(payload);
                var packet = new QuicServer.OutgoingPacket(0, senderId, null, payload);
                Require(cancelled.TryReserveQueueSpace(packet.QueuedBytes));
                Require(cancelled.SendQueue.Writer.TryWrite(packet));
            }
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            await Invoke("WriteLoop", cancelled, stop.Token);
            Require(cancelled.QueuedBytes == 0 && cancelled.QueuedPackets == 0);
            foreach (var payload in payloads)
                Require(typeof(QuicServer.SharedPayload).GetField("_buffer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(payload) == null);
            Console.WriteLine("PASS: QUIC v50002 framing, duplicate IDs and cancelled queue cleanup");
        }

        private static Task Invoke(string name, QuicServer.Client client, CancellationToken token)
        {
            return (Task)typeof(QuicServer).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [client, token])!;
        }

        private static void Require(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException("QUIC relay regression.");
        }
    }
}
