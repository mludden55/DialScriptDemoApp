using System.Windows;

namespace ConnectIVRTester
{
    public partial class ShareScriptsHelpDialog : Window
    {
        public ShareScriptsHelpDialog()
        {
            InitializeComponent();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
