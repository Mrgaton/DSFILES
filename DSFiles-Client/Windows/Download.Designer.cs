using DSFiles_Client.Windows;
using System;
using Terminal.Gui;
using Terminal.Gui.Views;
using Terminal.Gui.ViewBase;
using Terminal.Gui.App;

namespace DSFiles_Client.CGuis
{
    public partial class Download : Window
    {
        private Button btnDown;
        private TextField seedTextField;
        void InitializeComponent()
        {
            Width = Dim.Fill();
            Height = Dim.Fill();

            SetScheme(ColorSchemes.Main);
            Title = "DSFILES";

            var infoLabel = new Label
            {
                X = 0,
                Y = 1,

                Text = "Please write the seed of the file you want to download.",
            };
            
            var usernameLabel = new Label
            {
                X = 0,
                Y = Pos.Align(Alignment.Center),

                Text = "Seed:"
            };

            seedTextField = new TextField
            {
                X = Pos.Right(usernameLabel) + 1,
                Y = usernameLabel.Y,
                Width = Dim.Fill()
            };

            btnDown= new Button
            {
                Y = Pos.Bottom(seedTextField) + 1,

                X = Pos.Center(),


                ShadowStyle = ShadowStyle.None,
                Text = "Download",
                IsDefault = true
            };

            Add(infoLabel, usernameLabel, seedTextField, btnDown);
        }
    }
}
