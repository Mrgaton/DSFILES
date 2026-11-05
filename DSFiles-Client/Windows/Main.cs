using DSFiles_Client.Helpers;
using static DSFiles_Shared.DiscordFilesSpliter;

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

                var download = new Download(App);
                App.Run(download);

                this.Enabled = true;
            };

            uploadButton.Accepting += (s, e) =>
            {
                this.Enabled = false;

                var upload = new Upload(App);
                App.Run(upload);

                this.Enabled = true;
            };

            removeButton.Accepting += (s, e) =>
            {
                this.Enabled = false;

                var remove = new Remove(App);
                App.Run(remove);

                this.Enabled = true;
            };
        }
    }
}