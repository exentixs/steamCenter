using System.Windows;

namespace steamCenter
{
    public partial class PasswordInputDialog : Window
    {
        public string Password { get; private set; } = "";

        public PasswordInputDialog(string accountName)
        {
            InitializeComponent();
            MessageText.Text = $"Введите пароль для {accountName}:";
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            Password = PasswordBox.Password;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}