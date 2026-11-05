using DSFiles_Shared.Properties;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using Aes = System.Security.Cryptography.Aes;

namespace DSFiles_Shared
{
    public class AesCTRStream : Stream
    {
        private const int BlockSize = 16;
        private const int CounterSize = sizeof(ulong);
        private const int WriteBufferSize = 81920;

        private readonly Aes? _aes;
        private readonly ICryptoTransform? _encryptEcb;
        private readonly Stream? _baseStream;
        private readonly bool _leaveOpen;
        private long _position;

        /// <summary>
        /// Wraps a stream, or transforms buffers directly when baseStream is null.
        /// A null subKey selects pass-through mode. Instances are not thread-safe.
        /// The key derivation and counter layout preserve the existing DSFiles format.
        /// </summary>
        public AesCTRStream(Stream? baseStream, byte[]? subKey = null, bool leaveOpen = false)
        {
            _baseStream = baseStream;
            _leaveOpen = leaveOpen;

            if (subKey != null)
            {
                byte[] key = HMACSHA256.HashData(SHA512.HashData(subKey), Resources.bin);
                byte[] nonce = HMACMD5.HashData(Resources.bin, key);
                try
                {
                    nonce.AsSpan(CounterSize, BlockSize - CounterSize).CopyTo(_counterBlock.AsSpan(CounterSize));
                    _aes = Aes.Create();
                    _aes.Mode = CipherMode.ECB;
                    _aes.Padding = PaddingMode.None;
                    _aes.Key = key;
                    _encryptEcb = _aes.CreateEncryptor();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                    CryptographicOperations.ZeroMemory(nonce);
                }
            }
        }

        public override bool CanRead => !_disposed && (_baseStream?.CanRead ?? false);
        public override bool CanSeek => !_disposed && (_baseStream?.CanSeek ?? false);
        public override bool CanWrite => !_disposed && (_baseStream?.CanWrite ?? false);
        public override long Length => BaseStream.Length;

        /// <summary>
        /// Logical cipher offset. Setting it does not seek the underlying stream:
        /// downloads use absolute file offsets while writing to a non-seekable response.
        /// Use Seek to move both the underlying stream and the cipher offset.
        /// </summary>
        public override long Position
        {
            get
            {
                ThrowIfDisposed();
                return _position;
            }
            set
            {
                ThrowIfDisposed();
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _position = value;
            }
        }

        private Stream BaseStream
        {
            get
            {
                ThrowIfDisposed();
                return _baseStream ?? throw new NotSupportedException("No underlying stream was supplied. Use Encode or Transform.");
            }
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

        public override void Flush() => BaseStream.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            BaseStream.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin)
        {
            Stream stream = BaseStream;
            if (!stream.CanSeek)
                throw new NotSupportedException("The underlying stream does not support seeking.");

            long newPosition = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(stream.Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            if (newPosition < 0)
                throw new IOException("An attempt was made to seek before the beginning of the stream.");

            return _position = stream.Seek(newPosition, SeekOrigin.Begin);
        }

        public override void SetLength(long value) => BaseStream.SetLength(value);

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            int bytesRead = BaseStream.Read(buffer);
            Transform(buffer[..bytesRead], _position);
            _position = checked(_position + bytesRead);
            return bytesRead;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Stream stream = BaseStream;
            cancellationToken.ThrowIfCancellationRequested();
            int bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Transform(buffer.Span[..bytesRead], _position);
            _position = checked(_position + bytesRead);
            return bytesRead;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            Write(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Stream stream = BaseStream;
            if (_encryptEcb == null)
            {
                stream.Write(buffer);
                _position = checked(_position + buffer.Length);
                return;
            }

            byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length, WriteBufferSize));
            try
            {
                while (!buffer.IsEmpty)
                {
                    int count = Math.Min(buffer.Length, WriteBufferSize);
                    Span<byte> chunk = rented.AsSpan(0, count);
                    buffer[..count].CopyTo(chunk);
                    Transform(chunk, _position);
                    stream.Write(chunk);
                    _position = checked(_position + count);
                    buffer = buffer[count..];
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);
            return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Stream stream = BaseStream;
            cancellationToken.ThrowIfCancellationRequested();
            if (_encryptEcb == null)
            {
                await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                _position = checked(_position + buffer.Length);
                return;
            }

            byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length, WriteBufferSize));
            try
            {
                while (!buffer.IsEmpty)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = Math.Min(buffer.Length, WriteBufferSize);
                    buffer.Span[..count].CopyTo(rented);
                    Transform(rented.AsSpan(0, count), _position);
                    await stream.WriteAsync(rented.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    _position = checked(_position + count);
                    buffer = buffer[count..];
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }

        /// <summary>Transforms only the first count bytes in place and advances the cipher offset.</summary>
        public void Encode(Span<byte> bufferSegment, int count)
        {
            ThrowIfDisposed();
            if ((uint)count > (uint)bufferSegment.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            long nextPosition = checked(_position + count);
            Transform(bufferSegment[..count], _position);
            _position = nextPosition;
        }

        private readonly byte[] _counterBlock = new byte[BlockSize];
        private readonly byte[] _keystream = new byte[BlockSize];

        private readonly byte[] _keystream32 = new byte[2 * BlockSize];

        /// <summary>Transforms a buffer in place at an absolute cipher offset without changing Position.</summary>
        public void Transform(Span<byte> buffer, long position)
        {
            ThrowIfDisposed();
            ArgumentOutOfRangeException.ThrowIfNegative(position);
            if (position > long.MaxValue - buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(position));
            int length = buffer.Length;
            if (length == 0 || _encryptEcb == null)
                return;

            int offsetInBlock = (int)(position & (BlockSize - 1));
            ulong counterValue = (ulong)(position >> 4);
            int processedBytes = 0;

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

            int remainingLength = length - processedBytes;
            int fullBlocks = remainingLength / BlockSize;

            if (fullBlocks > 0)
            {
                if (Avx2.IsSupported)
                {
                    int numAvxPairs = fullBlocks / 2;

                    if (numAvxPairs > 0)
                    {
                        for (int i = 0; i < numAvxPairs; i++)
                        {
                            WriteCounterToBlock(counterValue);
                            _encryptEcb.TransformBlock(_counterBlock, 0, BlockSize, _keystream32, 0);
                            WriteCounterToBlock(counterValue + 1);
                            _encryptEcb.TransformBlock(_counterBlock, 0, BlockSize, _keystream32, BlockSize);

                            Span<byte> dstSlice = buffer.Slice(processedBytes, 2 * BlockSize);

                            ref byte dstRef = ref MemoryMarshal.GetReference<byte>(dstSlice);

                            ref byte ksRef = ref MemoryMarshal.GetReference<byte>(_keystream32);

                            var ksVec = Unsafe.ReadUnaligned<Vector256<byte>>(ref ksRef);
                            var dataVec = Unsafe.ReadUnaligned<Vector256<byte>>(ref dstRef);

                            var resultVec = Avx2.Xor(ksVec, dataVec);

                            Unsafe.WriteUnaligned(ref dstRef, resultVec);

                            processedBytes += 2 * BlockSize;
                            counterValue += 2;
                        }

                        fullBlocks -= numAvxPairs * 2;
                    }
                }

                for (int i = 0; i < fullBlocks; i++)
                {
                    WriteCounterToBlock(counterValue);

                    _encryptEcb.TransformBlock(_counterBlock, 0, BlockSize, _keystream, 0);

                    Span<byte> targetSlice = buffer.Slice(processedBytes, BlockSize);

                    ReadOnlySpan<ulong> ksU = MemoryMarshal.Cast<byte, ulong>(_keystream);
                    Span<ulong> dstU = MemoryMarshal.Cast<byte, ulong>(targetSlice);
                    for (int j = 0; j < dstU.Length; j++)
                        dstU[j] ^= ksU[j];

                    processedBytes += BlockSize;
                    counterValue++;
                }
            }

            if (processedBytes < length)
            {
                WriteCounterToBlock(counterValue);
                _encryptEcb.TransformBlock(_counterBlock, 0, BlockSize, _keystream, 0);

                int bytesRemaining = length - processedBytes;
                XorBytes(buffer.Slice(processedBytes, bytesRemaining),
                         _keystream.AsSpan().Slice(0, bytesRemaining));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteCounterToBlock(ulong counter)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_counterBlock, counter);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void XorBytes(Span<byte> target, ReadOnlySpan<byte> source)
        {
            for (int i = 0; i < target.Length; i++)
            {
                target[i] ^= source[i];
            }
        }

        private bool _disposed;

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing)
                {
                    try
                    {
                        _encryptEcb?.Dispose();
                        _aes?.Dispose();
                        CryptographicOperations.ZeroMemory(_counterBlock);
                        CryptographicOperations.ZeroMemory(_keystream);
                        CryptographicOperations.ZeroMemory(_keystream32);
                    }
                    finally
                    {
                        if (!_leaveOpen)
                            _baseStream?.Dispose();
                    }
                }
            }

            base.Dispose(disposing);
        }
    }
}
