using System.IO.Compression;

namespace DSFiles
{
    internal class ZipCompressor
    {
        public static void CompressZip(ref Stream stream, string[] pathArrays)
        {
            ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create, true);

            int i = 0;

            string relativePath = string.Empty;

            while (relativePath == string.Empty)
            {
                if (File.Exists(pathArrays[i])) relativePath = Path.GetDirectoryName(pathArrays[0]);
                else if (Directory.Exists(pathArrays[i++])) relativePath = Path.GetDirectoryName(pathArrays[0]);
            }

            Directory.SetCurrentDirectory(relativePath);

            foreach (string path in pathArrays)
            {
                if (File.Exists(path))
                {
                    archive.CreateEntryFromFile(path, Path.GetFileName(path), CompressionLevel.SmallestSize);
                }
                else if (Directory.Exists(path))
                {
                    AddDirectory(ref archive, relativePath, path);
                }
            }

            archive.Dispose();
        }

        private static void AddDirectory(ref ZipArchive archive, string relativePath, string path)
        {
            string tarjetPath = Path.GetRelativePath(relativePath, path) + '\\';

            archive.CreateEntry(tarjetPath, CompressionLevel.SmallestSize);

            foreach (var file in Directory.GetFiles(tarjetPath))
            {
                archive.CreateEntryFromFile(Path.Combine(relativePath, file), file, CompressionLevel.SmallestSize);
            }

            foreach (var directory in Directory.GetDirectories(tarjetPath))
            {
                AddDirectory(ref archive, relativePath, Path.Combine(relativePath, directory));
            }
        }
    }
}