using System;
using System.IO;
using System.IO.Compression;

namespace DSFiles_Client.Helpers
{
    internal class ZipCompressor
    {
        public static string GetRootPath(string[] args, params string[] pathArrays)
        {
            string commonRoot = Directory.Exists(pathArrays[0]) ? pathArrays[0] : Path.GetDirectoryName(pathArrays[0]);

            for (int i = 1; i < pathArrays.Length; i++)
            {
                if (string.IsNullOrEmpty(pathArrays[i])) continue;

                while (!pathArrays[i].StartsWith(commonRoot, StringComparison.InvariantCultureIgnoreCase))
                {
                    if (Directory.Exists(commonRoot))
                    {
                        commonRoot = commonRoot;
                    }
                    else
                    {
                        commonRoot = Path.GetDirectoryName(commonRoot);
                    }
                }
            }

            return commonRoot;
        }

        public static void CompressZip(ref Stream stream, string[] pathArrays) => CompressZip(ref stream, GetRootPath(pathArrays), pathArrays);

        public static void CompressZip(ref Stream stream, string relativePath, string[] pathArrays)
        {
            ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create, true);

            string currentDir = Directory.GetCurrentDirectory();

            Directory.SetCurrentDirectory(relativePath);

            foreach (string path in pathArrays)
            {
                if (string.IsNullOrEmpty(path)) continue;

                if (File.Exists(path))
                {
                    AddFile(ref archive, "", Path.GetFileName(path));
                }
                else if (Directory.Exists(path))
                {
                    AddDirectory(ref archive, relativePath, path);
                }
            }

            Directory.SetCurrentDirectory(currentDir);

            archive.Dispose();
        }

        private static void AddFile(ref ZipArchive archive, string relativePath, string file)
        {
            try
            {
                Console.WriteLine("Adding to zip: " + file);

                archive.CreateEntryFromFile(Path.Combine(relativePath, file), file, CompressionLevel.SmallestSize);
            }
            catch (Exception ex)
            {
                HandleException(ref ex);
            }
        }

        private static void AddDirectory(ref ZipArchive archive, string relativePath, string path)
        {
            string targetPath = Path.GetRelativePath(relativePath, path) + '\\';

            archive.CreateEntry(targetPath, CompressionLevel.SmallestSize);

            foreach (string file in Directory.EnumerateFiles(targetPath)) AddFile(ref archive, relativePath, file);

            foreach (string dir in Directory.EnumerateDirectories(targetPath))
            {
                try
                {
                    AddDirectory(ref archive, relativePath, Path.Combine(relativePath, dir));
                }
                catch (Exception ex)
                {
                    HandleException(ref ex);
                }
            }
        }

        private static void HandleException(ref Exception ex)
        {
            var oldColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine();
            Console.WriteLine(ex.ToString());
            Console.WriteLine();
            Console.ForegroundColor = oldColor;
        }
    }
}