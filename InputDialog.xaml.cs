using System.Windows;

namespace GuildWars2Achievements
{
    public partial class InputDialog : Window
    {
        public string ResponseText { get; set; }

        public InputDialog()
        {
            InitializeComponent();
        }

        private void btnDialogOk_Click(object sender, RoutedEventArgs e)
        {
            ResponseText = txtResponse.Text;
            DialogResult = true; // Closes the dialog and returns success
        }
    }
}