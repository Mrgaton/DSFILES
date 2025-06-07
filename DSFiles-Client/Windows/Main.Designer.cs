using DSFiles_Client.Helpers;
using System;
using Terminal.Gui;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DSFiles_Client.CGuis
{
    public partial class Main : Window
    {
        StatusBar statusBar;
        Button downloadButton, uploadButton, removeButton;

        void InitializeComponent()
        {
            Width = Dim.Fill();
            Height = Dim.Fill();

            Title = "DSFiles Files Manager";

            SetScheme(WindowsHelper.MainColors);

            ShadowStyle = ShadowStyle.Transparent;

            var label = new Label()
            {
                X = 1,
                Y = 1,

                Text = "Welcome to the main menu."
            };

            downloadButton = new Button()
            {
                X = Pos.Percent(15),
                Y = Pos.Align(Alignment.Center),

                Width = Dim.Auto(),

                ShadowStyle = ShadowStyle.None,
                Title = "Download",
                IsDefault = false
            };

            uploadButton = new Button()
            {
                X = Pos.Percent(45),
                Y = downloadButton.Y,
                Width = Dim.Auto(),

                ShadowStyle = ShadowStyle.None,
                Title = "Upload",
            };

            removeButton = new Button()
            {
                X = Pos.Percent(75),
                Y = uploadButton.Y,
                Width = Dim.Auto(),

                ShadowStyle = ShadowStyle.None,
                Title = "Remove",
            };

            this.Add(label, downloadButton, uploadButton, removeButton);
        }
    }
}
