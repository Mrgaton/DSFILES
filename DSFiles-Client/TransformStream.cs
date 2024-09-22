using DSFiles_Client.Properties;

namespace DSFiles_Client
{
    public class TransformStream : Stream
    {
        private static byte[] Key = Resources.bin;

        private readonly Stream _baseStream;

        public TransformStream(Stream baseStream)
        {
            _baseStream = baseStream;
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

        public override int Read(byte[] buffer, int offset, int count) => throw new NotImplementedException();

        public new async Task<int> ReadAsync(byte[] buffer, int offset, int count) => throw new NotImplementedException();

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

        public new async Task WriteAsync(byte[] buffer, int offset, int count)
        {
            D(ref buffer, (int)this.Position + buffer.Length);

            this.Position += count + offset;

            await _baseStream.WriteAsync(buffer, offset, count);
        }

        public static void D(ref byte[] data, int position)
        {
            for (int i = 0; i < data.Length; i++)
            {
                int relativeIndex = (position ) + i;

                int keyIndex = (relativeIndex % Key.Length);

                data[i] ^= Key[keyIndex];
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