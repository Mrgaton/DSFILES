using DSFiles_Client.Helpers;
using DSFiles_Shared;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using MessageBox = Terminal.Gui.Views.MessageBox;

namespace DSFiles_Client.CGuis
{
    public partial class Upload
    {
        public Upload(IApplication app)
        {
            SaveDialog sd = new SaveDialog()
            {
                AllowsMultipleSelection = true,

                Title = "Select a file to upload",
                Width = Dim.Fill(),
                Height = Dim.Fill()
            };

            app.Run(sd);

            if (sd.MultiSelected.Count <= 0)
            {
                MessageBox.Query(app!, "DSFiles Manager", "Operation canceled", "Ok");
                app.Run<Main>();
                return;
            }

            string currentPath = Directory.GetCurrentDirectory();

            if (!File.Exists(Program.WebHookFileName))
            {
                string cp = app.Clipboard.GetClipboardData();

                if (string.IsNullOrEmpty(cp))
                {
                    MessageBox.Query(app, "DSFiles Manager", $"We couldn't find '{Program.WebHookFileName}' file with webhook data and we could not retrieve the clipboard content for autocompletion.", "ok :c");
                    app.Run<Main>();
                    return;
                }

                cp = cp.Trim();

            retryClip:

                if (cp.StartsWith("https://", StringComparison.InvariantCultureIgnoreCase) && cp.Contains("webhook", StringComparison.InvariantCultureIgnoreCase))
                {
                    var wh = new WebHookHelper(Program.client, cp);

                    if (wh.channelId <= 0)
                        throw new Exception("WebHook is not valid");

                    File.WriteAllText(Program.WebHookFileName, cp);
                }
                else
                {
                    var res = MessageBox.ErrorQuery(app, "DSFiles Manager", "Please copy a valid webhook first onto the clipboard first", "Ok", "Retry");

                    if (res == 1)
                        goto retryClip;

                    app.Run<Main>();
                    return;
                }
            }

            var prog = new Progress()
            {
                ActionToRun = new(async () =>
                {
                retry:

                    WebHookHelper? webHookHelper = null;

                    webHookHelper = new WebHookHelper(Program.client, File.Exists(Program.WebHookFileName) ? File.ReadAllText(Program.WebHookFileName) : "");

                    app.Invoke(async () =>
                    {
                        var progress = WindowsHelper.GetProgress();
                        var paths = sd.MultiSelected;

                        string filePath = paths[0];

                        if (paths.Count == 1 && Directory.Exists(filePath))
                            paths = paths.Concat([""]).ToArray();

                        CompressionLevel compLevel = CompressionLevel.Optimal;

                        Stream? stream = null;

                        if (paths.Count == 1)
                        {
                            stream = File.OpenRead(filePath);

                            int reply = (int)MessageBox.Query(app, "DSFiles Manager", "Do you want to compress this file?", "None", "Fastest", "Optimal", "Smallest");

                            switch (reply)
                            {
                                case 0:
                                    compLevel = CompressionLevel.NoCompression;
                                    break;

                                case 1:
                                    compLevel = CompressionLevel.Fastest;
                                    break;

                                case 2:
                                    compLevel = CompressionLevel.Optimal;
                                    break;

                                case 3:
                                    compLevel = CompressionLevel.SmallestSize;
                                    break;
                            }
                        }
                        else
                        {
                            string rootPath = ZipCompressor.GetRootPath(paths.ToArray());

                            ((IProgress<string>)progress).Report("Compressing files please wait");

                            string archivedPath = Path.GetTempFileName();

                            ClientHelper.RemoveOnBoot(archivedPath);

                            Program.SevenZipPaths(archivedPath, CompressionLevel.Optimal, paths.ToArray());

                            filePath = rootPath.Split('\\').Last(c => !string.IsNullOrEmpty(c)) + ".7z";

                            int tries = 0;

                            while (tries < 5)
                            {
                                try
                                {
                                    stream = File.OpenRead(archivedPath);
                                    break;
                                }
                                catch (Exception ex)
                                {
                                    tries++;

                                    Program.WriteException(ref ex);
                                }
                            }

                        }

                        if (stream == null)
                            throw new ArgumentNullException(nameof(stream));

                        string fileName = Path.GetFileName(filePath);

                        DiscordFilesSpliter.ConsoleProgress = progress;

                        Task.Factory.StartNew(async () =>
                        {
                            var result = await DiscordFilesSpliter.EncodeCore(webHookHelper, fileName, stream, compLevel);

                            await stream.DisposeAsync();

                            Program.UploadedFilesWriter.WriteLine(result.UploadLog);

                            try
                            {
                                DiyClipboard.SetText(result.WebLink);
                            }
                            catch (Exception ex)
                            {
                                Program.WriteException(ref ex);
                            }

                            ((IProgress<string>)progress).Report("WebLink: " + result.WebLink);
                            ((IProgress<string>)progress).Report("FileSeed: " + result.DownloadToken);
                        });
                    });
                })
            };

            app.Run(prog);
        }
    }
}