using System.IO.Compression;
using System.Text;

namespace DSFiles_Client.Utils
{
    public static class Base64Url
    {
        public static string ToBase64Url(byte[] data) => Convert.ToBase64String(data).Trim('=').Replace('+', '-').Replace('/', '_');

        public static byte[] FromBase64Url(string data) => Convert.FromBase64String(data.Replace('_', '/').Replace('-', '+').PadRight(data.Length + (4 - data.Length % 4) % 4, '='));
    }

    public static class TimespanUtils
    {
        public static string ToReadableAgeString(this TimeSpan span)
        {
            return string.Format("{0:0}", span.Days / 365.25);
        }

        public static string ToReadableString(this TimeSpan span)
        {
            string formatted = string.Format("{0}{1}{2}{3}",
                span.Duration().Days > 0 ? string.Format("{0:0}d{1}, ", span.Days, span.Days == 1 ? string.Empty : string.Empty) : string.Empty,
                span.Duration().Hours > 0 ? string.Format("{0:0}h{1}, ", span.Hours, span.Hours == 1 ? string.Empty : string.Empty) : string.Empty,
                span.Duration().Minutes > 0 ? string.Format("{0:0}m{1}, ", span.Minutes, span.Minutes == 1 ? string.Empty : string.Empty) : string.Empty,
                span.Duration().Seconds > 0 ? string.Format("{0:0}s{1}", span.Seconds, span.Seconds == 1 ? string.Empty : string.Empty) : string.Empty);

            if (formatted.EndsWith(", ")) formatted = formatted.Substring(0, formatted.Length - 2);

            if (string.IsNullOrEmpty(formatted)) formatted = "0 s";

            return formatted;
        }
    }

    public static class StringExtensions
    {
        public static byte[] FromBase64(this string data) => Convert.FromBase64String(data.PadRight(data.Length + (4 - data.Length % 4) % 4, '='));

        public static byte[] FromBase64Url(this string data) => data.Replace('_', '/').Replace('-', '+').FromBase64();

        public static IEnumerable<string> SplitInParts(this string s, int partLength)
        {
            if (s == null)
                throw new ArgumentNullException(nameof(s));
            if (partLength <= 0)
                throw new ArgumentException("Part length has to be positive.", nameof(partLength));

            for (var i = 0; i < s.Length; i += partLength)
                yield return s.Substring(i, Math.Min(partLength, s.Length - i));
        }
    }

    public static class ByteArrayUtils
    {
        public static string ToBase64(this byte[] data) => Convert.ToBase64String(data).Trim('=');

        public static string ToBase64Url(this byte[] data) => data.ToBase64().Replace('+', '-').Replace('/', '_');

        //private static byte[] SmallestSizeCompressHeader = [0x00, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00];


        public static byte[] BrotliCompress(this byte[] data, CompressionLevel level = CompressionLevel.SmallestSize)
        {
            using (MemoryStream output = new MemoryStream())
            {
                using (BrotliStream dstream = new BrotliStream(output, level, true))
                {
                    dstream.Write(data, 0, data.Length);
                    dstream.Flush();
                }

                return output.ToArray();
            }
        }

        public static byte[] BrotliDecompress(this byte[] data, CompressionLevel level = CompressionLevel.SmallestSize)
        {
            using (MemoryStream input = new MemoryStream(data))
            {
                using (MemoryStream output = new MemoryStream())
                {
                    using (BrotliStream dstream = new BrotliStream(input, CompressionMode.Decompress))
                    {
                        dstream.CopyTo(output);
                        dstream.Flush();
                    }

                    return output.ToArray();
                }
            }
        }

        public static byte[] Deflate(this byte[] data, CompressionLevel level = CompressionLevel.SmallestSize)
        {
            using (MemoryStream output = new MemoryStream())
            {
                using (DeflateStream dstream = new DeflateStream(output, level, true))
                {
                    dstream.Write(data, 0, data.Length);
                    dstream.Flush();
                }

                output.SetLength(output.Length - 7);

                return output.ToArray();
            }
        }

        public static byte[] Inflate(this byte[] data)
        {
            using (MemoryStream input = new MemoryStream(data, 0, data.Length)) //+ CompressHeader.Length))
            {
                using (MemoryStream output = new MemoryStream())
                {
                    using (DeflateStream dstream = new DeflateStream(input, CompressionMode.Decompress))
                    {
                        dstream.CopyTo(output);
                        dstream.Flush();
                    }

                    return output.ToArray();
                }
            }
        }
    }

    public static class StreamUtils
    {
        public static void WriteLine(this Stream input, string line, bool flush = true)
        {
            input.Write(Encoding.UTF8.GetBytes(line + '\n'));

            if (flush) input.Flush();
        }

        public static void Write(this Stream input, byte[] data)
        {
            input.Write(data, 0, data.Length);
            //Console.WriteLine("Writen" + data.Length);
        }

        public static byte[] ReadAmout(this Stream input, long length)
        {
            byte[] buffer = new byte[length];
            input.Read(buffer, 0, buffer.Length);
            return buffer;
        }

        public static sbyte ReadSbyte(this MemoryStream input)
        {
            return (sbyte)input.ReadByte();
        }

        public static short ReadShort(this MemoryStream input)
        {
            byte[] buffer = new byte[2];
            input.Read(buffer, 0, buffer.Length);
            return BitConverter.ToInt16(buffer, 0);
        }

        public static int ReadInt(this MemoryStream input)
        {
            byte[] buffer = new byte[4];
            input.Read(buffer, 0, buffer.Length);
            return BitConverter.ToInt32(buffer, 0);
        }

        public static long ReadLong(this MemoryStream input)
        {
            byte[] buffer = new byte[8];
            input.Read(buffer, 0, buffer.Length);
            return BitConverter.ToInt64(buffer, 0);
        }

        public static ulong ReadULong(this MemoryStream input, bool invert)
        {
            byte[] buffer = new byte[8];
            input.Read(buffer, 0, buffer.Length);

            if (invert) Array.Reverse(buffer);

            return BitConverter.ToUInt64(buffer, 0);
        }
    }
}