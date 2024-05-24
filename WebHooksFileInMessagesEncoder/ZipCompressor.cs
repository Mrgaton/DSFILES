using System.IO.Compression;

namespace DSFiles
{
    internal class ZipCompressor
    {
        public static string GetRootPath(string[] pathArrays)
        {
            string commonRoot = Path.GetDirectoryName(pathArrays[0]);

            for (int i = 1; i < pathArrays.Length; i++)
            {
                while (!pathArrays[i].StartsWith(commonRoot,StringComparison.InvariantCultureIgnoreCase))
                {
                    commonRoot = Path.GetDirectoryName(commonRoot);
                }
            }

            return commonRoot;
        }
        public static void CompressZip(ref Stream stream, string[] pathArrays) => CompressZip(ref stream, GetRootPath(pathArrays), pathArrays);
        public static void CompressZip(ref Stream stream,string rootPath ,string[] pathArrays)
        {
            ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create, true);

            string relativePath = GetRootPath(pathArrays);

            //Directory.SetCurrentDirectory(relativePath);

            foreach (string path in pathArrays)
            {
                if (File.Exists(path))
                {
                    AddFile(ref archive, "", Path.GetFileName(path));
                }
                else if (Directory.Exists(path))
                {
                    AddDirectory(ref archive, relativePath, path);
                }
            }

            archive.Dispose();
        }
        private  static void AddFile(ref ZipArchive archive,string relativePath,string file)
        {
            Console.WriteLine("Adding to zip: " + file);

            archive.CreateEntryFromFile(Path.Combine(relativePath, file), file, CompressionLevel.SmallestSize);
        }

        private static void AddDirectory(ref ZipArchive archive, string relativePath, string path)
        {
            string tarjetPath = Path.GetRelativePath(relativePath, path) + '\\';

            archive.CreateEntry(tarjetPath, CompressionLevel.SmallestSize);

            foreach (string file in Directory.EnumerateFiles(tarjetPath))
            {
                AddFile(ref archive, relativePath, file);
            }

            foreach (string dir in Directory.EnumerateDirectories(tarjetPath))
            {
                AddDirectory(ref archive, relativePath, Path.Combine(relativePath, dir));
            }
        }
    }
}