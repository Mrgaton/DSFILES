namespace DSFiles_Shared
{
    internal static class StreamCompression
    {
        public static FileStream tempCompressorStream
        {
            get
            {
                return new FileStream(Path.GetTempFileName(), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
            }
        }

        public static Stream GetCompressorStream(ulong fileLengh)
        {
            if (fileLengh > int.MaxValue / 2) return tempCompressorStream;

            return new MemoryStream();
        }
    }
}