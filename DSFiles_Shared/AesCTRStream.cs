using DSFiles_Shared.Properties;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DSFiles_Shared
{
    public class AesCTRStream : Stream
    {
        private byte[] TransformationKey = Resources.bin;

        private const int BlockSize = 16;
        private const int CounterSize = sizeof(ulong);

        private readonly byte[] Key;

        private readonly byte[] Nonce;
        private readonly int _nonceOffsetInBlock;


        private readonly Aes _aes;
        private readonly ICryptoTransform _encryptEcb;

        private readonly Stream _baseStream;

        public AesCTRStream(Stream? baseStream, byte[]? subKey = null)
        {
            if (baseStream != null)
            {
                _baseStream = baseStream;
            }

            if (subKey != null)
            {
                //int hashSize = 256;
                //var derivedKey = HKDF.Expand(HashAlgorithmName.SHA256, SHA512.HashData(subKey), (hashSize / 8) * 255);

                Key = new HMACSHA256(SHA512.HashData(subKey)).ComputeHash(TransformationKey);

                Nonce = (new HMACMD5(TransformationKey).ComputeHash(Key));
                _nonceOffsetInBlock = CounterSize;
                Buffer.BlockCopy(Nonce, 8, _counterBlock, _nonceOffsetInBlock, BlockSize - CounterSize);

                _aes = AesCng.Create();
                _aes.Mode = CipherMode.ECB;
                _aes.Padding = PaddingMode.None;
                _aes.Key = Key;
                _encryptEcb = _aes.CreateEncryptor();
            }
        }

        public override bool CanRead => _baseStream.CanRead;
        public override bool CanSeek => _baseStream.CanSeek;
        public override bool CanWrite => _baseStream.CanWrite;
        public override long Length => _baseStream.Length;
        public override long Position { get; set; } = 0;

        public override void Flush()
        {
            _baseStream.Flush();
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            long newPosition;

            switch (origin)
            {
                case SeekOrigin.Begin:
                    newPosition = offset;
                    break;
                case SeekOrigin.Current:
                    newPosition = this.Position + offset;
                    break;
                case SeekOrigin.End:
                    newPosition = this.Length + offset;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(origin));
            }

            if (newPosition < 0) throw new IOException("An attempt was made to move the position before the beginning of the stream.");

            return this.Position = _baseStream.Seek(newPosition, SeekOrigin.Begin);
        }

        public override void SetLength(long value)
        {
            _baseStream.SetLength(value);
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).Result;

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) // Added CancellationToken
        {
            long currentStreamPos = this.Position; // Capture position before read

            int bytesRead = await _baseStream.ReadAsync(buffer, offset, count, cancellationToken);

            if (bytesRead > 0)
            {
                Transform(buffer.AsSpan(offset, bytesRead), currentStreamPos);
                this.Position += bytesRead;
            }
            return bytesRead;
        }

        public override void Write(byte[] buffer, int offset, int count) => this.WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

        public new async Task WriteAsync(byte[] buffer, int offset, int count)
        {
            Transform(buffer, this.Position);
            await _baseStream.WriteAsync(buffer, offset, count);
            this.Position += count;
        }

        public void Encode(Span<byte> bufferSegment, int count)
        {
            Transform(bufferSegment, this.Position);

            this.Position += count;
        }

        private readonly byte[] _counterBlock = new byte[BlockSize];
        private readonly byte[] _keystream = new byte[BlockSize];

        public void Transform(Span<byte> buffer, long position)
        {
            int length = buffer.Length;
            if (length == 0) return;

            int offsetInBlock = (int)(position & (BlockSize - 1)); // e.g., position & 0xF
            ulong counterValue = (ulong)(position >> 4); // e.g., position / BlockSize
            int processedBytes = 0;

            // --- 1) PREPARE COUNTER BLOCK (Optimized) ---
            // The nonce part is ALREADY in _counterBlock from the constructor.
            // We only need to write the counterValue.
            // WriteCounter is already inlined and efficient.

            // --- 2) If we start in the middle of a block, do the partial first block ---
            if (offsetInBlock != 0)
            {
                WriteCounterToBlock(counterValue);
                _encryptEcb.TransformBlock(_counterBlock, 0, BlockSize, _keystream, 0);

                int bytesToXor = Math.Min(BlockSize - offsetInBlock, length);
                XorBytes(buffer.Slice(0, bytesToXor),
                         _keystream.AsSpan().Slice(offsetInBlock, bytesToXor));

                processedBytes += bytesToXor;
                counterValue++;
            }

            // --- 3) Full 16-byte blocks ---
            // How many full blocks remain AFTER the potential partial first block
            int remainingLength = length - processedBytes;
            int fullBlocks = remainingLength / BlockSize;

            if (fullBlocks > 0)
            {
                Span<byte> fullBlockBufferSlice = buffer.Slice(processedBytes, fullBlocks * BlockSize);
                ReadOnlySpan<ulong> keystreamUlongs = MemoryMarshal.Cast<byte, ulong>(_keystream);

                for (int i = 0; i < fullBlocks; i++)
                {
                    WriteCounterToBlock(counterValue);
                    _encryptEcb.TransformBlock(_counterBlock, 0, BlockSize, _keystream, 0);

                    Span<ulong> targetUlongs = MemoryMarshal.Cast<byte, ulong>(
                        buffer.Slice(processedBytes, BlockSize) // Slice directly here
                    );

                    for (int j = 0; j < targetUlongs.Length; j++)
                    {
                        targetUlongs[j] ^= keystreamUlongs[j];
                    }
                    processedBytes += BlockSize; // Increment here
                    counterValue++;
                }
                processedBytes += fullBlocks * BlockSize;
            }


            // --- 4) Final partial block (if any) ---
            if (processedBytes < length)
            {
                WriteCounterToBlock(counterValue);
                _encryptEcb.TransformBlock(_counterBlock, 0, BlockSize, _keystream, 0);

                int bytesRemaining = length - processedBytes;
                XorBytes(buffer.Slice(processedBytes, bytesRemaining),
                         _keystream.AsSpan().Slice(0, bytesRemaining));
                // processedBytes += bytesRemaining; // Not needed as we are done
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteCounterToBlock(ulong counter)
        {
            // This writes to the first 8 bytes of _counterBlock.
            // The nonce part (e.g., bytes 8-15) remains untouched.
            BinaryPrimitives.WriteUInt64LittleEndian(_counterBlock, counter);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void XorBytes(Span<byte> target, ReadOnlySpan<byte> source)
        {
            // The JIT is often smart enough to vectorize this for small, fixed sizes like this.
            // For very large spans, manual vectorization with System.Numerics.Vectors
            // or System.Runtime.Intrinsics might be considered, but for crypto block
            // processing, this is usually sufficient.
            for (int i = 0; i < target.Length; i++)
            {
                target[i] ^= source[i];
            }
        }

        private bool _disposed = false;

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _encryptEcb?.Dispose();
                    _aes?.Dispose();
                    _baseStream?.Dispose();
                }
                _disposed = true;
            }

            base.Dispose(disposing);
        }
    }
}