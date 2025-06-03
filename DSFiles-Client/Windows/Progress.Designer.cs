using DSFiles_Client.Windows;
using System;
using System.Collections.ObjectModel;
using Terminal.Gui;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DSFiles_Client.CGuis
{
    public partial class Progress : Window
    {
        public static Label infoLabel;
        public static ListView logsView;
        public static ObservableCollection<string> logs = [];
        void InitializeComponent()
        {
            Width = Dim.Fill();
            Height = Dim.Fill();

            SetScheme(ColorSchemes.Main);

            Title = "DSFILES";

            infoLabel = new Label
            {
                X = 1,
                Y = 1,

                Width = Dim.Auto(),
                Height = Dim.Auto(),

                Text = "Loading...",
            };

            /*progressBar = new ProgressBar
            {
                X = 1,
                Y = infoLabel.Y + 2,

                ProgressBarStyle = ProgressBarStyle.MarqueeBlocks,
                Width = Dim.Percent(50),
            };*/

     
            logs = [];

            logsView = new ListView
            {
                X = 1,
                Y = Pos.Bottom(infoLabel) + 1,

                Width = Dim.Fill(),
                Height = Dim.Fill(),

                Enabled = true,
                Visible = true,
                AllowsMultipleSelection = true,
                
                Source = new ListWrapper<string>(logs)
            };

            logsView.SetScheme(ColorSchemes.Main);

            Add(infoLabel, logsView);
        }
    }
}
