using System.Windows;

namespace steamCenter
{
    public partial class EditAccountDialog : Window
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
            public bool SkipPasswordPrompt { get; set; }
        }

        public EditAccountDialog(string login, string password, string email, string emailPassword,
                                  string description, bool autoCopyConfig, bool skipPasswordPrompt)
        {
            InitializeComponent();
            Title = $"Редактирование аккаунта {login}";
            LoginBox.Text = login;
            PasswordBox.Password = password;
            EmailBox.Text = email;
            EmailPasswordBox.Password = emailPassword;
            DescriptionBox.Text = description;
            AutoCopyCheck.IsChecked = autoCopyConfig;
            SkipPasswordCheck.IsChecked = skipPasswordPrompt;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            ResultData = new AccountData
            {
                Login = LoginBox.Text,
                Password = PasswordBox.Password,
                Email = EmailBox.Text,
                EmailPassword = EmailPasswordBox.Password,
                Description = DescriptionBox.Text,
                AutoCopyConfig = AutoCopyCheck.IsChecked == true,
                SkipPasswordPrompt = SkipPasswordCheck.IsChecked == true
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