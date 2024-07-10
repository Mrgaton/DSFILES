using Microsoft.VisualBasic.Devices;

namespace DSFiles
{
    internal class StreamCompression
    {
        public static string tempCompressorPath = Path.Combine(Path.GetTempPath(), "tempStream.tmp");

        private static FileStream tempCompressorStream
        {
            get
            {
                FileStream stream = File.Open(tempCompressorPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                stream.SetLength(0);
                return stream;
            }
        }

        public static ulong AvailableMemory { get => new ComputerInfo().AvailablePhysicalMemory; }

        public static Stream GetCompressorStream(ulong fileLengh)
        {
            if (fileLengh > int.MaxValue || fileLengh > AvailableMemory + (1000 * 1000 * 1000)) return tempCompressorStream;

            return new MemoryStream();
        }
    }
}