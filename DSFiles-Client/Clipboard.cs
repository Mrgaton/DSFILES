using System;
using System.ComponentModel;
using System.Diagnostics;

namespace DSFiles_Client
{
    public static class ClipClipboard
    {
        public static bool SetText(string text)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "clip.exe",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    CreateNoWindow = true
                };

                var process = new Process { StartInfo = startInfo };
                process.Start();

                process.StandardInput.Write(text);
                process.StandardInput.Close();
                process.WaitForExit();
                return process.ExitCode == 0;
            }
            catch (Win32Exception ex)
            {
                // This exception occurs if clip.exe is not found in the system's PATH.
                throw new Exception("Error: clip.exe not found. Is it in your system's PATH?", ex);
            }
        }
    }
}