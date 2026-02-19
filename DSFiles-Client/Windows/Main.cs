using DSFiles_Client.Helpers;
using DSFiles_Shared;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Terminal.Gui.Views;
using Application = Terminal.Gui.App.Application;
using Clipboard = System.Windows.Forms.Clipboard;

namespace DSFiles_Client.CGuis
{
    public partial class Main
    {
        public Main()
        {
            InitializeComponent();

            SetScheme(WindowsHelper.MainColors);

            downloadButton.Accepting += (s, e) =>
            {
                this.Enabled = false;

                App.Run<Download>();
                //Application.Run<Download>();

                this.Enabled = true;
            };

            uploadButton.Accepting += (s, e) =>
            {
                this.Enabled = false;

                App.Run<Upload>();
                //Application.Run<Download>();

                this.Enabled = true;
            };

            removeButton.Accepting += (s, e) =>
            {
                this.Enabled = false;
                App.Run<Remove>();
                this.Enabled = true;
            };
        }
    }
}