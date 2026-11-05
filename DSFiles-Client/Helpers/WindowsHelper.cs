using DSFiles_Client.CGuis;
using System;
using Terminal.Gui.Drawing;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace DSFiles_Client.Helpers
{
    internal static class WindowsHelper
    {
        public static Scheme MainColors = new Scheme()
        {

            Normal = new Attribute(Color.Parse("#ECEFF4"), Color.Parse("#2E3440")),

            Disabled = new Attribute(Color.Parse("#FCD6DA"), Color.Parse("#3B4252")),

            Focus = new Attribute(Color.Parse("#002F67"), Color.Parse("#FFFFFF")),

            HotFocus = new Attribute(Color.Parse("#13698C"), Color.Parse("#FFFFFF")),

            HotNormal = new Attribute(Color.Parse("#D08770"), Color.Parse("#3B4252")),
        };

        public static Progress<string> GetProgress()
        {
            return new Progress<string>((s) =>
            {
                foreach (var line in AnsiStrip.Strip(s).Split('\n'))
                {
                    var content = line.Replace("\r", "");

                    Progress.logs.Add(string.IsNullOrEmpty(content) ? " " : content);
                    Progress.logsView.MoveEnd();
                }
            });
        }
    }
}