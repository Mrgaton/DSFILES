using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;

public class BrokenSaveFileDialog
{
    [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetSaveFileName(ref OpenFileName ofn);

    [Flags]
    private enum SaveFileNameFlags : int
    {
        OFN_OVERWRITEPROMPT = 0x00000002,
        OFN_HIDEREADONLY = 0x00000004,
        OFN_EXPLORER = 0x00080000,
        OFN_PATHMUSTEXIST = 0x00000800,
        OFN_NOCHANGEDIR = 0x00000008,
        OFN_ENABLESIZING = 0x00800000,
        OFN_CREATEPROMPT = 0x00002000,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpstrFilter;

        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;

        public IntPtr lpstrFile;
        public int nMaxFile;

        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpstrInitialDir;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpstrTitle;

        public int Flags;

        public short nFileOffset;
        public short nFileExtension;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpstrDefExt;

        public IntPtr lCustData;
        public IntPtr lpfnHook;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpTemplateName;

        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    public string Title { get; set; }
    public IntPtr Owner { get; set; } = IntPtr.Zero;
    public string InitialDirectory { get; set; }
    public string Filter { get; set; }
    public int FilterIndex { get; set; } = 1;
    public string DefaultExt { get; set; }
    public string FileName { get; set; } = "";
    public bool AddExtension { get; set; } = true;
    public bool CreatePrompt { get; set; } = false;
    public bool OverwritePrompt { get; set; } = true;
    public bool CheckPathExists { get; set; } = true;
    public bool RestoreDirectory { get; set; } = false;

    public static bool Show(
        out string selectedFileName,
        IntPtr owner = default,
        string title = null,
        string filter = null,
        string initialDirectory = null,
        string defaultExt = null,
        string initialFileName = null,
        bool overwritePrompt = true,
        bool addExtension = true,
        bool checkPathExists = true,
        bool createPrompt = false,
        int filterIndex = 1,
        bool restoreDirectory = false
        )
    {
        var dialog = new BrokenSaveFileDialog();
        if (owner != default) dialog.Owner = owner;
        if (title != null) dialog.Title = title;
        if (filter != null) dialog.Filter = filter;
        if (initialDirectory != null) dialog.InitialDirectory = initialDirectory;
        if (defaultExt != null) dialog.DefaultExt = defaultExt;
        if (initialFileName != null) dialog.FileName = initialFileName;

        dialog.OverwritePrompt = overwritePrompt;
        dialog.AddExtension = addExtension;
        dialog.CheckPathExists = checkPathExists;
        dialog.CreatePrompt = createPrompt;
        dialog.FilterIndex = filterIndex;
        dialog.RestoreDirectory = restoreDirectory;

        bool result = dialog.ShowDialog();
        selectedFileName = dialog.FileName;
        return result;
    }

    public bool ShowDialog()
    {
        return ShowSaveFileDialogInternal();
    }

    private bool ShowSaveFileDialogInternal()
    {
        const int MAX_FILE_PATH_CHARS = 2048;
        const int MAX_FILE_TITLE_CHARS = 256;

        var ofn = new OpenFileName();
        IntPtr lpstrFileBuffer = IntPtr.Zero;
        IntPtr lpstrFileTitleBuffer = IntPtr.Zero;
        bool success = false;

        try
        {
            ofn.lStructSize = Marshal.SizeOf(typeof(OpenFileName));
            ofn.hwndOwner = this.Owner;
            ofn.hInstance = IntPtr.Zero;

            if (!string.IsNullOrEmpty(this.Filter))
            {
                ofn.lpstrFilter = this.Filter.Replace("|", "\0") + "\0\0";
            }
            else
            {
                ofn.lpstrFilter = "All Files (*.*)\0*.*\0\0";
            }
            ofn.nFilterIndex = this.FilterIndex;

            lpstrFileBuffer = Marshal.AllocHGlobal(MAX_FILE_PATH_CHARS * sizeof(char));
            ofn.lpstrFile = lpstrFileBuffer;
            ofn.nMaxFile = MAX_FILE_PATH_CHARS;

            if (!string.IsNullOrEmpty(this.FileName))
            {
                string sfn = this.FileName;
                if (sfn.Length >= MAX_FILE_PATH_CHARS)
                {
                    sfn = sfn.Substring(0, MAX_FILE_PATH_CHARS - 1);
                }
                char[] fileNameChars = sfn.ToCharArray();
                Marshal.Copy(fileNameChars, 0, lpstrFileBuffer, fileNameChars.Length);
                Marshal.WriteInt16(lpstrFileBuffer, fileNameChars.Length * sizeof(char), (short)0);
            }
            else
            {
                Marshal.WriteInt16(lpstrFileBuffer, 0, (short)0);
            }

            lpstrFileTitleBuffer = Marshal.AllocHGlobal(MAX_FILE_TITLE_CHARS * sizeof(char));
            Marshal.WriteInt16(lpstrFileTitleBuffer, 0, (short)0);
            ofn.lpstrFileTitle = lpstrFileTitleBuffer;
            ofn.nMaxFileTitle = MAX_FILE_TITLE_CHARS;

            ofn.lpstrInitialDir = this.InitialDirectory;
            ofn.lpstrTitle = string.IsNullOrEmpty(this.Title) ? null : this.Title;
            ofn.lpstrDefExt = this.AddExtension ? this.DefaultExt : null;

            int flags = (int)SaveFileNameFlags.OFN_EXPLORER |
                        (int)SaveFileNameFlags.OFN_ENABLESIZING |
                        (int)SaveFileNameFlags.OFN_HIDEREADONLY;

            if (this.OverwritePrompt)
            {
                flags |= (int)SaveFileNameFlags.OFN_OVERWRITEPROMPT;
            }
            if (this.CheckPathExists)
            {
                flags |= (int)SaveFileNameFlags.OFN_PATHMUSTEXIST;
            }
            if (this.RestoreDirectory)
            {
                flags |= (int)SaveFileNameFlags.OFN_NOCHANGEDIR;
            }
            if (this.CreatePrompt)
            {
                flags |= (int)SaveFileNameFlags.OFN_CREATEPROMPT;
            }
            ofn.Flags = flags;

            ofn.lpstrCustomFilter = IntPtr.Zero;
            ofn.nMaxCustFilter = 0;
            ofn.nFileOffset = 0;
            ofn.nFileExtension = 0;
            ofn.lCustData = IntPtr.Zero;
            ofn.lpfnHook = IntPtr.Zero;
            ofn.lpTemplateName = null;
            ofn.pvReserved = IntPtr.Zero;
            ofn.dwReserved = 0;
            ofn.FlagsEx = 0;

            bool dialogResult = GetSaveFileName(ref ofn);

            if (dialogResult)
            {
                this.FileName = Marshal.PtrToStringUni(ofn.lpstrFile);
                success = true;
            }
            else
            {
                success = false;
                int error = Marshal.GetLastWin32Error();
                if (error != 0 && error != 1223) // 1223 (ERROR_CANCELLED)
                {
                    Console.WriteLine($"GetSaveFileName failed. Win32 error: 0x{error:X} ({new System.ComponentModel.Win32Exception(error).Message})");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception in ShowSaveFileDialogInternal: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            success = false;
        }
        finally
        {
            if (lpstrFileBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(lpstrFileBuffer);
            }
            if (lpstrFileTitleBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(lpstrFileTitleBuffer);
            }
        }
        return success;
    }
}