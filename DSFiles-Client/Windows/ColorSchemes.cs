using Terminal.Gui;
using Terminal.Gui.Drawing;

namespace DSFiles_Client.Windows
{
    internal class ColorSchemes
    {
        public static Scheme Main = new Scheme()
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
    }
}
