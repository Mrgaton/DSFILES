﻿using DSFiles_Shared.Properties;
using System.Security.Cryptography;

namespace DSFiles_Shared
{
    public class TransformStream : Stream
    {
        private byte[] TransformationKey = Resources.bin;

        private readonly Stream _baseStream;

        public TransformStream(Stream baseStream, byte[]? subKey = null)
        {
            _baseStream = baseStream;

            if (subKey != null)
            {
                var derivedKey = HKDF.Expand(HashAlgorithmName.MD5, subKey, 4080);

                D(ref TransformationKey, 0, ref derivedKey);
            }
        }

        public override bool CanRead => _baseStream.CanRead;
        public override bool CanSeek => _baseStream.CanSeek;
        public override bool CanWrite => _baseStream.CanWrite;
        public override long Length => _baseStream.Length;
        public override long Position { get; set; } = 0;

        /*public override long Position
        {
            get => _baseStream.Position;
            set => _baseStream.Position = value;
        }*/

        public override void Flush()
        {
            _baseStream.Flush();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return _baseStream.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            _baseStream.SetLength(value);
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        public new async Task<int> ReadAsync(byte[] buffer, int offset, int count)
        {
            long pos = this.Position;

            int result = await _baseStream.ReadAsync(buffer, offset, count);

            if (result > 0)
            {
                D(ref buffer, pos, ref TransformationKey);

                this.Position += count;
            }

            return result;
        }

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

        public new async Task WriteAsync(byte[] buffer, int offset, int count)
        {
            D(ref buffer, this.Position, ref TransformationKey);

            this.Position += count;

            await _baseStream.WriteAsync(buffer, offset, count);
        }

        public static void D(ref byte[] data, long position, ref byte[] key)
        {
            for (int i = 0; i < data.Length; i++)
            {
                long relativeIndex = (position) + i;

                long keyIndex = (relativeIndex % key.Length);

                data[i] ^= key[keyIndex];
            }
        }
    }
}