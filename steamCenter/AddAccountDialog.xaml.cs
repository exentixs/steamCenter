using System.Windows;
using System.Windows.Controls;

namespace steamCenter
{
    public partial class AddAccountDialog : Window
    {
        public AccountData? ResultData { get; private set; }

        public class AccountData
        {
            public string Login { get; set; } = "";
            public string Password { get; set; } = "";
            public string Email { get; set; } = "";
            public string EmailPassword { get; set; } = "";
            public string Description { get; set; } = "";
            public bool AutoCopyConfig { get; set; }
        }

        public AddAccountDialog()
        {
            InitializeComponent();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(LoginBox.Text))
            {
                MessageBox.Show("Введите логин!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            ResultData = new AccountData
            {
                Login = LoginBox.Text.Trim(),
                Password = PasswordBox.Password,
                Email = EmailBox.Text,
                EmailPassword = EmailPasswordBox.Password,
                Description = DescriptionBox.Text,
                AutoCopyConfig = AutoCopyCheck.IsChecked == true
            };
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