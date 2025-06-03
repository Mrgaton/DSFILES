using DSFiles_Client.Windows;
using System;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;

namespace DSFiles_Client.CGuis
{
    public partial class Remove : Window
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
                X = 1,
                Y = 1,

                Text = "Please write the seed of the file you want to remove.",
            };
            
            var usernameLabel = new Label
            {
                X = 1,
                Y = Pos.Bottom(infoLabel)+ 1,

                Text = "Seed/Token:"
            };

            seedTextField = new TextField
            {
                X = Pos.Right(usernameLabel) + 1,
                Y = usernameLabel.Y,
                Width = Dim.Fill()
            };

            btnDown= new Button
            {
                X = Pos.Center(),
                Y = Pos.Bottom(seedTextField) + 1,

                ShadowStyle = ShadowStyle.None,
                Text = "Remove",
                IsDefault = true
            };

            Add(infoLabel, usernameLabel, seedTextField, btnDown);
        }
    }
}
