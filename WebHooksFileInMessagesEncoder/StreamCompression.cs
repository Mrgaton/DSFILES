using Microsoft.VisualBasic.Devices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DSFiles
{
    internal class StreamCompression
    {
        private static FileStream tempCompressorStream { 
            get 
            { 
                FileStream stream = File.Open("tempStream.tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                stream.SetLength(0);
                return stream;
            } 
        }

        private static ulong AvailableMemory { get => new ComputerInfo().AvailablePhysicalMemory; }
        public static Stream GetCompressorStream(ulong fileLengh)
        {
            if (fileLengh > int.MaxValue || fileLengh > AvailableMemory + (1000 * 1000 * 1000)) return tempCompressorStream;

            return new MemoryStream();
        }

    }
}
