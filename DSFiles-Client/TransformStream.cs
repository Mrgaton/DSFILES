using DSFiles_Client.Properties;
using Microsoft.VisualBasic.ApplicationServices;
using System.Security.Cryptography;
using System.Text;

namespace DSFiles_Client
{
    public class TransformStream : Stream
    {
        private byte[] TransformedKey = Resources.bin;

        private readonly Stream _baseStream;

        public TransformStream(Stream baseStream, byte[]? subKey = null)
        {
            _baseStream = baseStream;

            if (subKey != null)
            {
                var derivedKey = HKDF.Expand(HashAlgorithmName.MD5, subKey, 2048);

                D(ref TransformedKey, 0, ref derivedKey);
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
                D(ref buffer, pos, ref TransformedKey);

                this.Position += count;
            }

            return result;
        }

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

        public new async Task WriteAsync(byte[] buffer, int offset, int count)
        {
            D(ref buffer, this.Position, ref TransformedKey);

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

        /*public static byte[] D(byte[] data, byte[] key)
        {
            byte[] result = new byte[data.Length];

            int max = data.Length - 1;
            byte last = (byte)(data.Length % byte.MaxValue);

            for (int i = 0; i < data.Length; i++)
            {
                int keyIndex = i % key.Length;

                result[i] = (byte)(data[max - i] ^ key[keyIndex]);
                result[i] += last;

                last += data[max - i];

                if (i % 2 == 0)
                {
                    last &= key[(key.Length - keyIndex) - 1];
                }
                else
                {
                    last ^= key[(key.Length - keyIndex) - 1];
                }
            }

            return result;
        }

        public static byte[] U(byte[] data, byte[] key)
        {
            byte[] result = new byte[data.Length];
            int max = data.Length - 1;
            byte last = (byte)(data.Length % byte.MaxValue);

            for (int i = 0; i < data.Length; i++)
            {
                int keyIndex = i % key.Length;

                data[i] -= last;

                byte b = (byte)(data[i] ^ key[keyIndex]);

                result[max - i] = b;
                last += b;

                if (i % 2 == 0)
                {
                    last &= key[(key.Length - keyIndex) - 1];
                }
                else
                {
                    last ^= key[(key.Length - keyIndex) - 1];
                }
            }

            return result;
        }*/
    }
}