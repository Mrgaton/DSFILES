using DSFiles_Client.CGuis;
using System;
using System.Text.RegularExpressions;
using Terminal.Gui.Drawing;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace DSFiles_Client.Helpers
{
    internal static class WindowsHelper
    {
        public static Scheme MainColors = new Scheme()
        {
            // Text on dark slate background
            Normal = new Attribute(Color.Parse("#ECEFF4"), Color.Parse("#2E3440")),

            // Muted grays for disabled items
            Disabled = new Attribute(Color.Parse("#FCD6DA"), Color.Parse("#3B4252")),

            // Darker blue for focused controls (reads on white)
            Focus = new Attribute(Color.Parse("#002F67"), Color.Parse("#FFFFFF")),

            // Rich cyan when a control is both “hot” and focused
            HotFocus = new Attribute(Color.Parse("#13698C"), Color.Parse("#FFFFFF")),

            // Warm orange for hot (but unfocused) highlights
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
