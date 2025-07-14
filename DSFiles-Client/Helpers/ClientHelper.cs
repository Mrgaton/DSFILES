using System.Runtime.InteropServices;

namespace DSFiles_Client.Helpers
{
    internal static class ClientHelper
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Auto)] private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, int dwFlags);

        public static void RemoveOnBoot(string filePath) => MoveFileEx(filePath, null, 0x4);

        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;

            public MEMORYSTATUSEX()
            {
                dwLength = (uint)Marshal.SizeOf(this);
            }
        }

        public static ulong GetAvilableMemory()
        {
            MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();

            if (GlobalMemoryStatusEx(memStatus))
            {
                return memStatus.ullAvailPhys;
            }

            return 0;
        }
    }
}