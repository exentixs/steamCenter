using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SteamSwitcher
{
    public partial class MainWindow : Window
    {
        private string steamPath = @"C:\Program Files (x86)\Steam";
        private string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamReg");
        private string dbFile, favFile;
        private string sourceID = "";
        private Dictionary<string, AccountData> accountsData = new Dictionary<string, AccountData>();

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_CLOSE = 0x0010;

        public class AccountData
        {
            public string Password { get; set; }
            public string Email { get; set; }
            public string EmailPassword { get; set; }
            public string Description { get; set; }
            public DateTime CreationDate { get; set; }
            public bool AutoCopyConfig { get; set; }
            public bool SkipPasswordPrompt { get; set; }
        }

        public class SteamAccount
        {
            public string AccountName { get; set; }
            public string PersonaName { get; set; }
            public string AccountID32 { get; set; }
            public string LastLoginFormatted { get; set; }
            public ImageSource Avatar { get; set; }
            public string Description { get; set; }
            public bool HasDescription => !string.IsNullOrEmpty(Description);
            public bool IsFavorite { get; set; }
            public SolidColorBrush FavoriteColor => IsFavorite ? Brushes.Gold : Brushes.Transparent;
            public bool HasPassword { get; set; }
            public bool SkipPasswordPrompt { get; set; }
        }

        public MainWindow()
        {
            InitializeComponent();
            dbFile = Path.Combine(appData, "accounts.txt");
            favFile = Path.Combine(appData, "favs.txt");

            if (!Directory.Exists(appData)) Directory.CreateDirectory(appData);

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (key != null)
                    {
                        var path = key.GetValue("SteamPath")?.ToString().Replace('/', '\\');
                        if (path != null && Directory.Exists(path))
                            steamPath = path;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error: {ex.Message}");
            }

            LoadAllData();
            LoadSteamAccounts();
        }

        private void LoadAllData()
        {
            LoadCredentials();
            LoadFavorites();
        }

        private void LoadCredentials()
        {
            accountsData.Clear();
            if (!File.Exists(dbFile)) return;

            var lines = File.ReadAllLines(dbFile);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var parts = line.Split('|');
                if (parts.Length < 2) continue;

                var data = new AccountData();
                string login = parts[0].Trim();

                for (int i = 1; i < parts.Length; i++)
                {
                    var kv = parts[i].Trim().Split(':');
                    if (kv.Length == 2)
                    {
                        string key = kv[0].Trim();
                        string value = kv[1].Trim();

                        switch (key)
                        {
                            case "P": data.Password = value; break;
                            case "E": data.Email = value; break;
                            case "EP": data.EmailPassword = value; break;
                            case "D": data.Description = value; break;
                            case "Date": if (DateTime.TryParse(value, out DateTime tempDate)) data.CreationDate = tempDate; break;
                            case "AutoCopy": if (bool.TryParse(value, out bool tempAutoCopy)) data.AutoCopyConfig = tempAutoCopy; break;
                            case "SkipPrompt": if (bool.TryParse(value, out bool tempSkip)) data.SkipPasswordPrompt = tempSkip; break;
                        }
                    }
                }
                accountsData[login.ToLower()] = data;
            }
        }

        private void SaveCredentials()
        {
            var lines = new List<string>();
            foreach (var acc in accountsData)
            {
                var data = acc.Value;
                string line = $"{acc.Key} | P:{data.Password ?? ""} | E:{data.Email ?? ""} | EP:{data.EmailPassword ?? ""} | D:{data.Description ?? ""} | Date:{data.CreationDate:yyyy-MM-dd HH:mm:ss} | AutoCopy:{data.AutoCopyConfig} | SkipPrompt:{data.SkipPasswordPrompt}";
                lines.Add(line);
            }
            File.WriteAllLines(dbFile, lines);
        }

        private void LoadFavorites()
        {
            if (!File.Exists(favFile))
                File.WriteAllText(favFile, "");
        }

        private async void LoadSteamAccounts()
        {
            try
            {
                var accounts = new List<SteamAccount>();
                string vdf = Path.Combine(steamPath, @"config\loginusers.vdf");

                if (!File.Exists(vdf))
                {
                    AccountsList.ItemsSource = accounts;
                    return;
                }

                string vdfContent = await Task.Run(() => File.ReadAllText(vdf, Encoding.UTF8));
                var favs = File.Exists(favFile) ? File.ReadAllLines(favFile).ToList() : new List<string>();

                foreach (Match m in Regex.Matches(vdfContent, "\"(\\d+)\"\\s*\\{([^}]+)\\}"))
                {
                    string id64 = m.Groups[1].Value;
                    string block = m.Groups[2].Value;
                    string acc = Regex.Match(block, "\"AccountName\"\\s+\"([^\"]+)\"").Groups[1].Value;
                    string name = Regex.Match(block, "\"PersonaName\"\\s+\"([^\"]+)\"").Groups[1].Value;
                    string ts = Regex.Match(block, "\"Timestamp\"\\s+\"([^\"]+)\"").Groups[1].Value;

                    var item = new SteamAccount
                    {
                        AccountName = acc,
                        PersonaName = string.IsNullOrEmpty(name) ? "Без имени" : name,
                        AccountID32 = (long.Parse(id64) - 76561197960265728).ToString(),
                        IsFavorite = favs.Contains(acc),
                        Avatar = await GetAvatarAsync(id64),
                        LastLoginFormatted = FormatLastLogin(ts),
                        HasPassword = accountsData.ContainsKey(acc.ToLower()) && !string.IsNullOrEmpty(accountsData[acc.ToLower()]?.Password),
                        SkipPasswordPrompt = accountsData.ContainsKey(acc.ToLower()) && accountsData[acc.ToLower()].SkipPasswordPrompt
                    };

                    if (accountsData.ContainsKey(acc.ToLower()))
                    {
                        item.Description = accountsData[acc.ToLower()].Description;
                    }
                    accounts.Add(item);
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    AccountsList.ItemsSource = accounts.OrderByDescending(a => a.IsFavorite).ThenByDescending(a => a.LastLoginFormatted).ToList();
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка: {ex.Message}");
            }
        }

        private string FormatLastLogin(string timestamp)
        {
            if (long.TryParse(timestamp, out long unixTime) && unixTime > 0)
            {
                DateTime dt = DateTimeOffset.FromUnixTimeSeconds(unixTime).LocalDateTime;
                TimeSpan diff = DateTime.Now - dt;

                string ago;
                if (diff.TotalDays >= 1) ago = $"{(int)diff.TotalDays} дн. назад";
                else if (diff.TotalHours >= 1) ago = $"{(int)diff.TotalHours} ч. назад";
                else if (diff.TotalMinutes >= 1) ago = $"{(int)diff.TotalMinutes} мин. назад";
                else ago = "Только что";

                return $"Был: {dt:dd.MM.yyyy HH:mm} ({ago})";
            }
            return "Был: Никогда";
        }

        private async Task<ImageSource> GetAvatarAsync(string id64)
        {
            return await Task.Run(() =>
            {
                string[] possibleNames = { $"{id64}_full.jpg", $"{id64}.jpg", $"{id64}.png" };
                string foundPath = null;

                foreach (var name in possibleNames)
                {
                    string p = Path.Combine(steamPath, "config", "avatarcache", name);
                    if (File.Exists(p))
                    {
                        foundPath = p;
                        break;
                    }
                }

                if (foundPath != null)
                {
                    try
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.UriSource = new Uri(foundPath);
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        return bitmap;
                    }
                    catch { }
                }

                return null;
            });
        }

        // МЕТОД "БОЛЬШЕ НЕ СПРАШИВАТЬ"
        private void ToggleSkipPassword_Click(object sender, RoutedEventArgs e)
        {
            string acc = (sender as MenuItem).Tag.ToString();
            bool currentSkip = accountsData.ContainsKey(acc.ToLower()) && accountsData[acc.ToLower()].SkipPasswordPrompt;

            if (!accountsData.ContainsKey(acc.ToLower()))
                accountsData[acc.ToLower()] = new AccountData();

            accountsData[acc.ToLower()].SkipPasswordPrompt = !currentSkip;
            SaveCredentials();
            LoadSteamAccounts();

            ShowNotification(currentSkip ? "Уведомления о пароле включены" : "Уведомления о пароле отключены");
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            string acc = (sender as Button).Tag.ToString();
            bool hasPassword = accountsData.ContainsKey(acc.ToLower()) && !string.IsNullOrEmpty(accountsData[acc.ToLower()].Password);
            bool skipPrompt = accountsData.ContainsKey(acc.ToLower()) && accountsData[acc.ToLower()].SkipPasswordPrompt;

            if (!hasPassword && !skipPrompt)
            {
                var passwordDialog = new PasswordInputDialog(acc);
                if (passwordDialog.ShowDialog() == true)
                {
                    if (!accountsData.ContainsKey(acc.ToLower()))
                        accountsData[acc.ToLower()] = new AccountData();
                    accountsData[acc.ToLower()].Password = passwordDialog.Password;
                    SaveCredentials();
                    hasPassword = true;
                }
                else return;
            }

            if (!hasPassword) return;

            // Закрываем Steam
            var progressDialog = new ProgressDialog("Закрытие Steam...");
            progressDialog.Show();

            bool isClosed = await KillAllSteamProcesses();

            if (!isClosed)
            {
                progressDialog.Close();
                var result = MessageBox.Show("Не удалось закрыть Steam. Закройте его вручную и нажмите OK.",
                    "Ошибка", MessageBoxButton.OKCancel);
                if (result != MessageBoxResult.OK) return;

                progressDialog.Show();
                isClosed = await KillAllSteamProcesses();
                progressDialog.Close();
                if (!isClosed) return;
            }

            progressDialog.UpdateMessage("Настройка входа...");

            await CleanSteamRegistry();
            await UpdateLoginUsersVDF(acc);

            progressDialog.UpdateMessage("Запуск Steam...");

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", true))
                {
                    if (key != null)
                    {
                        key.SetValue("AutoLoginUser", acc);
                        key.SetValue("RememberPassword", 1);
                    }
                }

                var steamExe = Path.Combine(steamPath, "steam.exe");
                if (File.Exists(steamExe))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = steamExe,
                        UseShellExecute = true
                    });

                    progressDialog.Close();
                    MessageBox.Show($"Вход в аккаунт {acc} выполнен!", "Успех", MessageBoxButton.OK);
                }
            }
            catch (Exception ex)
            {
                progressDialog.Close();
                MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка");
            }
        }

        private async Task<bool> KillAllSteamProcesses()
        {
            try
            {
                string[] processNames = { "steam", "steamwebhelper", "steamservice" };

                foreach (var name in processNames)
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        try
                        {
                            if (p.MainWindowHandle != IntPtr.Zero)
                                PostMessage(p.MainWindowHandle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                            await Task.Delay(300);
                            if (!p.HasExited) p.Kill();
                            p.Dispose();
                        }
                        catch { }
                    }
                }

                await Task.Delay(2000);
                return Process.GetProcessesByName("steam").Length == 0;
            }
            catch { return false; }
        }

        private async Task CleanSteamRegistry()
        {
            await Task.Run(() =>
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", true))
                    {
                        if (key != null)
                        {
                            key.SetValue("HSteamPipe", 0);
                            key.SetValue("HSteamUser", 0);
                        }
                    }
                }
                catch { }
            });
        }

        private async Task UpdateLoginUsersVDF(string accountName)
        {
            await Task.Run(() =>
            {
                try
                {
                    string vdf = Path.Combine(steamPath, @"config\loginusers.vdf");
                    if (!File.Exists(vdf)) return;

                    string text = File.ReadAllText(vdf, Encoding.UTF8);
                    text = Regex.Replace(text, "\"mostrecent\"\\s+\"1\"", "\"mostrecent\" \"0\"");
                    string pattern = $"(\\{{\\s*\"AccountName\"\\s+\"{Regex.Escape(accountName)}\".*?\"mostrecent\"\\s+)\"0\"";
                    text = Regex.Replace(text, pattern, "$1\"1\"", RegexOptions.Singleline);
                    File.WriteAllText(vdf, text, Encoding.UTF8);
                }
                catch { }
            });
        }

        private void OpenAddAccountDialog_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AddAccountDialog();
            dialog.AccountCreated += (login, password, email, emailPassword, description, autoCopyConfig) =>
            {
                var loginLower = login.ToLower();
                if (!accountsData.ContainsKey(loginLower))
                {
                    accountsData[loginLower] = new AccountData
                    {
                        Password = password,
                        Email = email,
                        EmailPassword = emailPassword,
                        Description = description,
                        CreationDate = DateTime.Now,
                        AutoCopyConfig = autoCopyConfig,
                        SkipPasswordPrompt = false
                    };
                    SaveCredentials();
                    LoadSteamAccounts();
                }
                else
                {
                    MessageBox.Show("Аккаунт уже существует!");
                }
            };
            dialog.ShowDialog();
        }

        private void EditAccount_Click(object sender, RoutedEventArgs e)
        {
            string acc = (sender as MenuItem).Tag.ToString();
            if (accountsData.ContainsKey(acc.ToLower()))
            {
                var data = accountsData[acc.ToLower()];
                var dialog = new EditAccountDialog(acc, data.Password, data.Email, data.EmailPassword, data.Description, data.AutoCopyConfig, data.SkipPasswordPrompt);
                dialog.AccountUpdated += (login, password, email, emailPassword, description, autoCopyConfig, skipPasswordPrompt) =>
                {
                    accountsData[login.ToLower()] = new AccountData
                    {
                        Password = password,
                        Email = email,
                        EmailPassword = emailPassword,
                        Description = description,
                        CreationDate = data.CreationDate,
                        AutoCopyConfig = autoCopyConfig,
                        SkipPasswordPrompt = skipPasswordPrompt
                    };
                    SaveCredentials();
                    LoadSteamAccounts();
                };
                dialog.ShowDialog();
            }
        }

        private void DeleteAccount_Click(object sender, RoutedEventArgs e)
        {
            string acc = (sender as MenuItem).Tag.ToString();
            if (MessageBox.Show($"Удалить аккаунт {acc}?", "Подтверждение", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                accountsData.Remove(acc.ToLower());
                SaveCredentials();
                LoadSteamAccounts();
            }
        }

        private void CopyLogin_Click(object sender, RoutedEventArgs e)
        {
            string acc = (sender as MenuItem).Tag.ToString();
            Clipboard.SetText(acc);
            ShowNotification("Логин скопирован!");
        }

        private void CopyPassword_Click(object sender, RoutedEventArgs e)
        {
            string acc = (sender as MenuItem).Tag.ToString();
            if (accountsData.ContainsKey(acc.ToLower()) && !string.IsNullOrEmpty(accountsData[acc.ToLower()].Password))
            {
                Clipboard.SetText(accountsData[acc.ToLower()].Password);
                ShowNotification("Пароль скопирован!");
            }
            else
            {
                MessageBox.Show("Пароль не сохранен.");
            }
        }

        private void CopyEmail_Click(object sender, RoutedEventArgs e)
        {
            string acc = (sender as MenuItem).Tag.ToString();
            if (accountsData.ContainsKey(acc.ToLower()) && !string.IsNullOrEmpty(accountsData[acc.ToLower()].Email))
            {
                Clipboard.SetText(accountsData[acc.ToLower()].Email);
                ShowNotification("Email скопирован!");
            }
            else
            {
                MessageBox.Show("Email не сохранен.");
            }
        }

        private void EditDescription_Click(object sender, RoutedEventArgs e)
        {
            string acc = (sender as MenuItem).Tag.ToString();
            string currentDesc = accountsData.ContainsKey(acc.ToLower()) ? accountsData[acc.ToLower()].Description : "";

            var dialog = new TextInputDialog($"Описание для {acc}", currentDesc);
            if (dialog.ShowDialog() == true && dialog.InputText != null)
            {
                if (!accountsData.ContainsKey(acc.ToLower()))
                    accountsData[acc.ToLower()] = new AccountData();
                accountsData[acc.ToLower()].Description = dialog.InputText;
                SaveCredentials();
                LoadSteamAccounts();
            }
        }

        private void ShowNotification(string message)
        {
            var notification = new NotificationWindow(message);
            notification.Show();
            var timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.Tick += (s, e) => { timer.Stop(); notification.Close(); };
            timer.Start();
        }

        private void SetSource_Click(object sender, RoutedEventArgs e)
        {
            sourceID = (sender as Button).Tag.ToString();
            SourceStatus.Text = $" | Источник: {sourceID}";
            SourceStatus.Foreground = Brushes.Gold;
        }

        private async void ApplySource_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(sourceID))
            {
                MessageBox.Show("Сначала выберите источник.");
                return;
            }

            string targetID = (sender as Button).Tag.ToString();
            await Task.Run(() => CopyConfigurations(sourceID, targetID));
            MessageBox.Show("Настройки скопированы!");
        }

        private void CopyConfigurations(string sourceId, string targetId)
        {
            string src = Path.Combine(steamPath, "userdata", sourceId);
            string dst = Path.Combine(steamPath, "userdata", targetId);
            if (!Directory.Exists(src)) return;
            Directory.CreateDirectory(dst);

            foreach (string dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(src, dst));

            foreach (string file in Directory.GetFiles(src, "*.*", SearchOption.AllDirectories))
                File.Copy(file, file.Replace(src, dst), true);
        }

        private void Favorite_Click(object sender, RoutedEventArgs e)
        {
            string acc = (sender as MenuItem).Tag.ToString();
            var favs = File.Exists(favFile) ? File.ReadAllLines(favFile).ToList() : new List<string>();
            if (favs.Contains(acc)) favs.Remove(acc);
            else favs.Add(acc);
            File.WriteAllLines(favFile, favs);
            LoadSteamAccounts();
        }

        private void OpenDatabase_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("notepad.exe", dbFile) { UseShellExecute = true }); }
            catch { }
        }

        private void RefreshList_Click(object sender, RoutedEventArgs e)
        {
            LoadAllData();
            LoadSteamAccounts();
        }
    }

    // Диалоговые окна (оставляем как есть из предыдущего кода)
    public class ProgressDialog : Window
    {
        private TextBlock messageText;
        private ProgressBar progressBar;

        public ProgressDialog(string initialMessage)
        {
            Title = "Подождите";
            Width = 400;
            Height = 120;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            Topmost = true;
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            Opacity = 0.95;

            var stackPanel = new StackPanel { Margin = new Thickness(20) };
            messageText = new TextBlock { Text = initialMessage, Foreground = Brushes.White, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 15) };
            progressBar = new ProgressBar { Height = 25, IsIndeterminate = true, Foreground = Brushes.LightGreen, Background = Brushes.DarkGray };
            stackPanel.Children.Add(messageText);
            stackPanel.Children.Add(progressBar);
            Content = stackPanel;
        }

        public void UpdateMessage(string message)
        {
            Dispatcher.Invoke(() => messageText.Text = message);
        }
    }

    public class PasswordInputDialog : Window
    {
        public string Password { get; private set; }
        private PasswordBox passwordBox;

        public PasswordInputDialog(string accountName)
        {
            Title = $"Введите пароль для {accountName}";
            Width = 400;
            Height = 180;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Background = Brushes.White;

            var stackPanel = new StackPanel { Margin = new Thickness(15) };
            stackPanel.Children.Add(new TextBlock { Text = $"Введите пароль для аккаунта {accountName}:", Margin = new Thickness(0, 0, 0, 10), FontSize = 13, FontWeight = FontWeights.Bold });
            passwordBox = new PasswordBox { Height = 35, Margin = new Thickness(0, 0, 0, 15), FontSize = 12 };
            stackPanel.Children.Add(passwordBox);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okButton = new Button { Content = "OK", Width = 80, Height = 32, Margin = new Thickness(5), Background = Brushes.LightGreen, FontWeight = FontWeights.Bold };
            var cancelButton = new Button { Content = "Отмена", Width = 80, Height = 32, Margin = new Thickness(5) };
            okButton.Click += (s, e) => { Password = passwordBox.Password; DialogResult = true; };
            cancelButton.Click += (s, e) => DialogResult = false;

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            stackPanel.Children.Add(buttonPanel);
            Content = stackPanel;
            Loaded += (s, e) => passwordBox.Focus();
        }
    }

    public class TextInputDialog : Window
    {
        public string InputText { get; private set; }
        private TextBox textBox;

        public TextInputDialog(string title, string defaultText)
        {
            Title = title;
            Width = 450;
            Height = 220;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Background = Brushes.White;

            var stackPanel = new StackPanel { Margin = new Thickness(15) };
            stackPanel.Children.Add(new TextBlock { Text = "Введите описание:", Margin = new Thickness(0, 0, 0, 10), FontWeight = FontWeights.Bold });
            textBox = new TextBox { Text = defaultText, Height = 80, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            stackPanel.Children.Add(textBox);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var okButton = new Button { Content = "Сохранить", Width = 100, Height = 35, Margin = new Thickness(5), Background = Brushes.LightGreen, FontWeight = FontWeights.Bold };
            var cancelButton = new Button { Content = "Отмена", Width = 100, Height = 35, Margin = new Thickness(5) };
            okButton.Click += (s, e) => { InputText = textBox.Text; DialogResult = true; };
            cancelButton.Click += (s, e) => DialogResult = false;

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            stackPanel.Children.Add(buttonPanel);
            Content = stackPanel;
            Loaded += (s, e) => textBox.Focus();
        }
    }

    public class AddAccountDialog : Window
    {
        public event Action<string, string, string, string, string, bool> AccountCreated;
        private TextBox loginBox, passwordBox, emailBox, emailPasswordBox, descriptionBox;
        private CheckBox autoCopyCheckBox;
        private Random random = new Random();

        public AddAccountDialog()
        {
            Title = "Добавление аккаунта";
            Width = 520;
            Height = 580;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Background = Brushes.White;

            var scrollViewer = new ScrollViewer();
            var stackPanel = new StackPanel { Margin = new Thickness(15) };

            stackPanel.Children.Add(new TextBlock { Text = "Логин:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 5, 0, 5) });
            loginBox = new TextBox { Height = 35, Margin = new Thickness(0, 0, 0, 10), FontSize = 12 };
            stackPanel.Children.Add(loginBox);

            stackPanel.Children.Add(new TextBlock { Text = "Пароль:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 5, 0, 5) });
            var passwordPanel = new StackPanel { Orientation = Orientation.Horizontal };
            passwordBox = new TextBox { Height = 35, Width = 380, Margin = new Thickness(0, 0, 5, 0), FontSize = 12 };
            var genButton = new Button { Content = "🔑 Ген", Width = 70, Height = 35, FontWeight = FontWeights.Bold };
            genButton.Click += (s, e) => passwordBox.Text = GeneratePassword();
            passwordPanel.Children.Add(passwordBox);
            passwordPanel.Children.Add(genButton);
            stackPanel.Children.Add(passwordPanel);

            stackPanel.Children.Add(new TextBlock { Text = "Email:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 5) });
            emailBox = new TextBox { Height = 35, Margin = new Thickness(0, 0, 0, 10), FontSize = 12 };
            stackPanel.Children.Add(emailBox);

            stackPanel.Children.Add(new TextBlock { Text = "Пароль Email:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 5, 0, 5) });
            emailPasswordBox = new TextBox { Height = 35, Margin = new Thickness(0, 0, 0, 10), FontSize = 12 };
            stackPanel.Children.Add(emailPasswordBox);

            stackPanel.Children.Add(new TextBlock { Text = "Описание:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 5, 0, 5) });
            descriptionBox = new TextBox { Height = 60, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Margin = new Thickness(0, 0, 0, 10), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 12 };
            stackPanel.Children.Add(descriptionBox);

            autoCopyCheckBox = new CheckBox { Content = "Автокопировать конфиги", Margin = new Thickness(0, 5, 0, 15), FontSize = 12 };
            stackPanel.Children.Add(autoCopyCheckBox);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var createButton = new Button { Content = "✓ Создать", Width = 100, Height = 35, Margin = new Thickness(5), Background = Brushes.LightGreen, FontWeight = FontWeights.Bold };
            var cancelButton = new Button { Content = "✗ Отмена", Width = 100, Height = 35, Margin = new Thickness(5) };
            createButton.Click += (s, e) => { if (string.IsNullOrEmpty(loginBox.Text)) { MessageBox.Show("Введите логин!"); return; } AccountCreated?.Invoke(loginBox.Text, passwordBox.Text, emailBox.Text, emailPasswordBox.Text, descriptionBox.Text, autoCopyCheckBox.IsChecked ?? false); DialogResult = true; };
            cancelButton.Click += (s, e) => DialogResult = false;
            buttonPanel.Children.Add(createButton);
            buttonPanel.Children.Add(cancelButton);
            stackPanel.Children.Add(buttonPanel);

            scrollViewer.Content = stackPanel;
            Content = scrollViewer;

            loginBox.Text = "Player_" + random.Next(10000, 99999);
            passwordBox.Text = GeneratePassword();
        }

        private string GeneratePassword()
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!@#$%^&*";
            char[] password = new char[12];
            for (int i = 0; i < 12; i++) password[i] = chars[random.Next(chars.Length)];
            return new string(password);
        }
    }

    public class EditAccountDialog : Window
    {
        public event Action<string, string, string, string, string, bool, bool> AccountUpdated;
        private TextBox loginBox, passwordBox, emailBox, emailPasswordBox, descriptionBox;
        private CheckBox autoCopyCheckBox, skipPromptCheckBox;

        public EditAccountDialog(string login, string password, string email, string emailPassword, string description, bool autoCopyConfig, bool skipPasswordPrompt)
        {
            Title = $"Редактирование {login}";
            Width = 520;
            Height = 630;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Background = Brushes.White;

            var scrollViewer = new ScrollViewer();
            var stackPanel = new StackPanel { Margin = new Thickness(15) };

            stackPanel.Children.Add(new TextBlock { Text = "Логин:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 5, 0, 5) });
            loginBox = new TextBox { Text = login, Height = 35, Margin = new Thickness(0, 0, 0, 10), IsEnabled = false, FontSize = 12 };
            stackPanel.Children.Add(loginBox);

            stackPanel.Children.Add(new TextBlock { Text = "Пароль:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 5, 0, 5) });
            passwordBox = new TextBox { Text = password, Height = 35, Margin = new Thickness(0, 0, 0, 10), FontSize = 12 };
            stackPanel.Children.Add(passwordBox);

            stackPanel.Children.Add(new TextBlock { Text = "Email:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 5) });
            emailBox = new TextBox { Text = email, Height = 35, Margin = new Thickness(0, 0, 0, 10), FontSize = 12 };
            stackPanel.Children.Add(emailBox);

            stackPanel.Children.Add(new TextBlock { Text = "Пароль Email:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 5, 0, 5) });
            emailPasswordBox = new TextBox { Text = emailPassword, Height = 35, Margin = new Thickness(0, 0, 0, 10), FontSize = 12 };
            stackPanel.Children.Add(emailPasswordBox);

            stackPanel.Children.Add(new TextBlock { Text = "Описание:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 5, 0, 5) });
            descriptionBox = new TextBox { Text = description, Height = 60, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Margin = new Thickness(0, 0, 0, 10), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 12 };
            stackPanel.Children.Add(descriptionBox);

            autoCopyCheckBox = new CheckBox { Content = "Автокопировать конфиги", IsChecked = autoCopyConfig, Margin = new Thickness(0, 5, 0, 10), FontSize = 12 };
            stackPanel.Children.Add(autoCopyCheckBox);

            skipPromptCheckBox = new CheckBox { Content = "Больше не спрашивать пароль", IsChecked = skipPasswordPrompt, Margin = new Thickness(0, 5, 0, 15), FontSize = 12 };
            stackPanel.Children.Add(skipPromptCheckBox);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var saveButton = new Button { Content = "✓ Сохранить", Width = 100, Height = 35, Margin = new Thickness(5), Background = Brushes.LightGreen, FontWeight = FontWeights.Bold };
            var cancelButton = new Button { Content = "✗ Отмена", Width = 100, Height = 35, Margin = new Thickness(5) };
            saveButton.Click += (s, e) => { AccountUpdated?.Invoke(loginBox.Text, passwordBox.Text, emailBox.Text, emailPasswordBox.Text, descriptionBox.Text, autoCopyCheckBox.IsChecked ?? false, skipPromptCheckBox.IsChecked ?? false); DialogResult = true; };
            cancelButton.Click += (s, e) => DialogResult = false;

            buttonPanel.Children.Add(saveButton);
            buttonPanel.Children.Add(cancelButton);
            stackPanel.Children.Add(buttonPanel);

            scrollViewer.Content = stackPanel;
            Content = scrollViewer;
        }
    }

    public class NotificationWindow : Window
    {
        public NotificationWindow(string message)
        {
            Title = "";
            Width = 300;
            Height = 50;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            Topmost = true;
            Background = new SolidColorBrush(Color.FromRgb(42, 71, 94));
            Opacity = 0.95;
            ShowInTaskbar = false;

            var textBlock = new TextBlock { Text = message, Foreground = Brushes.White, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold };
            Content = textBlock;

            Loaded += (s, e) => { Left = (SystemParameters.PrimaryScreenWidth - Width) / 2; Top = SystemParameters.PrimaryScreenHeight - Height - 50; };
        }
    }
}
