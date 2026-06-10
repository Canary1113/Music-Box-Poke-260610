using Microsoft.UI.Xaml.Controls;

namespace MusicBox
{
    public sealed partial class ShellContentHost : UserControl
    {
        public ShellContentHost()
        {
            InitializeComponent();
        }

        public Frame MainFrame => ContentFrame;
    }
}
