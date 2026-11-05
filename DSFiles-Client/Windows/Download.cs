using DSFiles_Client.Helpers;
using DSFiles_Shared;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Terminal.Gui.App;
using Terminal.Gui.Views;
using MessageBox = Terminal.Gui.Views.MessageBox;

namespace DSFiles_Client.CGuis
{
    public partial class Download
    {
        public Download(IApplication app)
        {
            InitializeComponent();

            btnDown.Accepting += async (s, e) =>
            {
                string? fileData = seedTextField.Text.Trim();

                if (fileData.StartsWith("jsp:/", StringComparison.InvariantCultureIgnoreCase))
                    fileData = Program.GetFromJspaste(fileData);

                string[] fileDataSplited = fileData.Split('/')[0].Split(':');

                string[] seedSplited = fileDataSplited.Last().Split('$');

                byte[] seed = seedSplited[0].FromBase64Url();
                byte[]? key = seedSplited.Length > 1 ? seedSplited[1].FromBase64Url() : null;

                string destFileName = string.Empty;

                if (fileDataSplited.Length > 1)
                {
                    destFileName = Encoding.UTF8.GetString(fileDataSplited[0].FromBase64Url().Inflate()) +
                    (
                        fileDataSplited.Length > 2 &&
                        !string.IsNullOrEmpty(fileDataSplited.Skip(1).First()) ? '.' + fileDataSplited.Skip(1).First() : null
                    );
                }

                var sd = new SaveDialog()
                {
                    Title = "Please select the file",
                    AllowsMultipleSelection = true
                };

                app.Run(sd);

                if (string.IsNullOrEmpty(sd.FileName))
                {
                    MessageBox.ErrorQuery(app!, "DSFiles Manager", "Operation cancelled", "ok", "retry");
                    return;
                }

                var prog = new Progress()
                {
                    ActionToRun = new Action(async () =>
                    {
                        DiscordFilesSpliter.ConsoleProgress = WindowsHelper.GetProgress();

                        string filename = sd.FileName;

                        Console.WriteLine();

                        using (FileStream fs = File.Open(filename, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
                        {
                            await DiscordFilesSpliter.Decode(seed, key, fs);
                        }

                        await Task.Delay(2000);
                    })
                };

                app.Run(prog);
            };
        }
    }
}