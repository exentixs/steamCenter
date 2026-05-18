using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace SteamSwitcher
{
    public partial class AddAccountWindow : Window
    {
        private readonly string dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "accounts.txt");

        public AddAccountWindow()
        {
            InitializeComponent();
            GenerateAccountData();
        }

        private void GenerateAccountData()
        {
            Random rnd = new Random();

            // Генерация логина в формате Player_XXXXX
            LoginBox.Text = "Player_" + rnd.Next(10000, 99999);

            // Генерация надежного случайного пароля (буквы разного регистра + цифры)
            const string validChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890";
            string generatedPass = new string(Enumerable.Repeat(validChars, 12)
                .Select(s => s[rnd.Next(s.Length)]).ToArray());

            // Добавляем спецсимвол в конец, как в твоем примере
            PassBox.Text = generatedPass + "!";
        }

        private void CopyLogin_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(LoginBox.Text);

        private void CopyPass_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(PassBox.Text);

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            string date = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
            string login = LoginBox.Text.Trim();
            string pass = PassBox.Text.Trim();
            string email = string.IsNullOrEmpty(EmailBox.Text) ? "Нет" : EmailBox.Text.Trim();
            string emailPass = string.IsNullOrEmpty(EmailPassBox.Text) ? "Нет" : EmailPassBox.Text.Trim();

            // Форматирование строго по твоему запросу
            string dbLine = $"Date: {date} | Login: {login} | Pass: {pass} | Email: {email} | EmailPass: {emailPass} | D: Новый";

            try
            {
                File.AppendAllText(dbPath, dbLine + Environment.NewLine);

                // Передаем родителю сигнал, что нужно скопировать конфиг из избранного
                if (AutoConfigCheck.IsChecked == true)
                {
                    this.Tag = "copy_config";
                }

                this.DialogResult = true;
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при записи файла базы: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxBoxImage.Error);
            }
        }
    }
}