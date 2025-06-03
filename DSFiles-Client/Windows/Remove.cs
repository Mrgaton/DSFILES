using DSFiles_Shared;
using System;
using System.Linq;
using Terminal.Gui;
using Terminal.Gui.App;
using Terminal.Gui.Views;

namespace DSFiles_Client.CGuis
{
    public partial class Remove
    {
        public Remove()
        {
            InitializeComponent();

            btnDown.Accepting += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(seedTextField.Text) || seedTextField.Text.Length < 6)
                {
                    MessageBox.ErrorQuery("Seed is empty", "Please provide a valid seed/token", "ok");
                    return;
                }

                Application.Run(new Progress(new Action(async () =>
                {
                    string data = seedTextField.Text.Split('/').Last();

                    var splited = data.Split(':');

                    var webHookHelper = new WebHookHelper(Program.client, BitConverter.ToUInt64(splited[1].FromBase64Url()), splited[2]);

                    ulong[] ids = new DiscordFilesSpliter.GorillaTimestampCompressor().Decompress(splited[0].FromBase64Url());

                    Progress.infoLabel.Text = "Removing " + ids.Length + " chunks please wait";
                    Progress.logs.Add("Removing file chunks (" + ids.Length + ")");

                    var progress = new Progress<string>((s) =>
                    {
                        Progress.logs.Add(s.Replace("\n", "").Replace("\r", ""));
                        Progress.logsView.MoveEnd();
                    });

                    await webHookHelper.RemoveMessages(ids, progress);
                })));
            };
        }
    }
}