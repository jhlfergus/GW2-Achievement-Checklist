using System.Windows;

namespace GuildWars2Achievements
{
    public partial class ConfirmationDialog : Window
    {
        public bool Result { get; private set; }

        public ConfirmationDialog(string message)
        {
            InitializeComponent();
            MessageTextBlock.Text = message;
        }

        private void YesButton_Click(object sender, RoutedEventArgs e)
        {
            Result = true;
            this.Close();
        }

        private void NoButton_Click(object sender, RoutedEventArgs e)
        {
            Result = false;
            this.Close();
        }
    }
}