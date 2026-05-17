using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

        // Словарь: Логин -> (Пароль, Почта, Описание)
        private Dictionary<string, (string pass, string email, string desc)> credentials = new Dictionary<string, (string, string, string)>();

        public MainWindow()
        {
            InitializeComponent();
            dbFile = Path.Combine(appData, "akk.txt");
            favFile = Path.Combine(appData, "favs.txt");
            if (!Directory.Exists(appData)) Directory.CreateDirectory(appData);

            // Надежно получаем путь к Стиму из реестра
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                if (key != null) steamPath = key.GetValue("SteamPath")?.ToString().Replace('/', '\\') ?? steamPath;
            }

            LoadSteamAccounts();
        }

        public class SteamAccount
        {
            public string AccountName { get; set; }
            public string PersonaName { get; set; }
            public string AccountID32 { get; set; }
            public string LastLoginFormatted { get; set; }
            public BitmapImage Avatar { get; set; }
            public string Description { get; set; }
            public bool HasDescription => !string.IsNullOrEmpty(Description);
            public bool IsFavorite { get; set; }
            public SolidColorBrush FavoriteColor => IsFavorite ? Brushes.Gold : Brushes.Transparent;
            public string PassLabel { get; set; } = "➕ Добавить пароль";
            public string EmailLabel { get; set; } = "➕ Добавить почту";
        }

        private void LoadSteamAccounts()
        {
            LoadCredentials();
            var favs = File.Exists(favFile) ? File.ReadAllLines(favFile).ToList() : new List<string>();
            var accounts = new List<SteamAccount>();
            string vdf = Path.Combine(steamPath, @"config\loginusers.vdf");

            if (!File.Exists(vdf)) return;

            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"(\\d+)\"\\s*\\{([^}]+)\\}"))
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
                    Avatar = GetAvatar(id64),
                    LastLoginFormatted = FormatLastLogin(ts)
                };

                if (credentials.ContainsKey(acc.ToLower()))
                {
                    var cred = credentials[acc.ToLower()];
                    if (!string.IsNullOrEmpty(cred.pass)) item.PassLabel = "📋 Скопировать пароль";
                    if (!string.IsNullOrEmpty(cred.email)) item.EmailLabel = "📋 Скопировать почту";
                    item.Description = cred.desc;
                }
                accounts.Add(item);
            }
            AccountsList.ItemsSource = accounts.OrderByDescending(a => a.IsFavorite).ThenByDescending(a => a.LastLoginFormatted).ToList();
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

        private BitmapImage GetAvatar(string id64)
        {
            // Проверяем все возможные варианты названия файлов аватарки
            string[] possibleNames = { $"{id64}_full.jpg", $"{id64}.jpg", $"{id64}.png" };
            string foundPath = null;

            foreach (var name in possibleNames)
            {
                string p = Path.Combine(steamPath, "config", "avatarcache", name);
                if (File.Exists(p)) { foundPath = p; break; }
            }

            try
            {
                BitmapImage bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = foundPath != null ? new Uri(foundPath) : new Uri("pack://application:,,,/"); // Если нет, будет просто фон из XAML
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                return bi;
            }
            catch { return null; }
        }

        // --- ЛОГИКА ВХОДА С ЖЕСТКОЙ ПРОВЕРКОЙ ЗАКРЫТИЯ ---
        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            string acc = (sender as Button).Tag.ToString();

            // 1. Пытаемся закрыть Стим
            Process[] steamProcesses = Process.GetProcessesByName("steam");
            foreach (var p in steamProcesses)
            {
                try { p.Kill(); } catch { }
            }

            // 2. ЖДЕМ полного закрытия (иначе Стим перезапишет VDF файл и ключи)
            bool isClosed = false;
            for (int i = 0; i < 20; i++)
            { // Ждем максимум 10 секунд (20 раз по 500мс)
                if (Process.GetProcessesByName("steam").Length == 0)
                {
                    isClosed = true;
                    break;
                }
                await Task.Delay(500);
            }

            if (!isClosed)
            {
                MessageBox.Show("Не удалось полностью закрыть Steam. Попробуйте закрыть его вручную в диспетчере задач.", "Ошибка");
                return;
            }

            // 3. Обновляем VDF, чтобы этот аккаунт был "самым новым"
            try
            {
                string vdf = Path.Combine(steamPath, @"config\loginusers.vdf");
                string text = File.ReadAllText(vdf);
                text = Regex.Replace(text, "\"mostrecent\"\\s+\"1\"", "\"mostrecent\" \"0\"");
                string pattern = "(\"AccountName\"\\s+\"" + acc + "\".*?\"mostrecent\"\\s+)\"0\"";
                text = Regex.Replace(text, pattern, "$1\"1\"", RegexOptions.Singleline);
                File.WriteAllText(vdf, text);
            }
            catch { }

            // 4. Прописываем в реестр и запускаем
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", true))
            {
                k.SetValue("AutoLoginUser", acc);
                k.SetValue("RememberPassword", 1);
            }
            Process.Start(Path.Combine(steamPath, "steam.exe"));
        }

        // --- БАЗА ДАННЫХ И ОПИСАНИЯ ---
        private void LoadCredentials()
        {
            credentials.Clear();
            if (!File.Exists(dbFile)) return;
            // Улучшенная регулярка, которая понимает формат: L: логин | P: пароль | E: почта | D: описание
            foreach (var line in File.ReadAllLines(dbFile))
            {
                var m = Regex.Match(line, @"L:\s*(.*?)\s*\|\s*P:\s*(.*?)(?:\s*\|\s*E:\s*(.*?))?(?:\s*\|\s*D:\s*(.*))?$");
                if (m.Success) credentials[m.Groups[1].Value.ToLower()] = (m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);
            }
        }

        private void EditDescription_Click(object sender, RoutedEventArgs e)
        {
            string acc = (sender as MenuItem).Tag.ToString();
            string currentDesc = credentials.ContainsKey(acc.ToLower()) ? credentials[acc.ToLower()].desc : "";

            // Вызываем наше кастомное окно ввода
            string newDesc = ShowInputBox($"Описание для {acc}", currentDesc);
            if (newDesc == null) return; // Нажали отмену

            acc = acc.ToLower();
            string pass = "", email = "";
            if (credentials.ContainsKey(acc))
            {
                pass = credentials[acc].pass; email = credentials[acc].email;
            }

            // Перезаписываем файл akk.txt
            var lines = File.Exists(dbFile) ? File.ReadAllLines(dbFile).ToList() : new List<string>();
            bool found = false;
            string newLine = $"L: {acc} | P: {pass} | E: {email} | D: {newDesc}";

            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].StartsWith($"L: {acc}", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = newLine; found = true; break;
                }
            }
            if (!found) lines.Add(newLine);

            File.WriteAllLines(dbFile, lines);
            LoadSteamAccounts();
        }

        // Кастомное мини-окно для ввода текста прямо внутри кода (чтобы не создавать новые файлы)
        private string ShowInputBox(string title, string defaultText)
        {
            Window w = new Window { Title = title, Width = 350, Height = 160, WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize, Background = Brushes.White };
            StackPanel sp = new StackPanel { Margin = new Thickness(15) };
            TextBox tb = new TextBox { Text = defaultText, Margin = new Thickness(0, 10, 0, 15), Height = 25, Padding = new Thickness(2) };
            Button btn = new Button { Content = "Сохранить", Height = 30, Width = 100, HorizontalAlignment = HorizontalAlignment.Right };
            btn.Click += (s, e) => w.DialogResult = true;
            sp.Children.Add(new TextBlock { Text = "Введите короткое описание (например: Ферма #1):" });
            sp.Children.Add(tb);
            sp.Children.Add(btn);
            w.Content = sp;
            tb.Focus();
            return w.ShowDialog() == true ? tb.Text : null;
        }

        // --- МЕНЮ КОПИРОВАНИЯ ---
        private void CopyLogin_Click(object sender, RoutedEventArgs e) => Clipboard.SetText((sender as MenuItem).Tag.ToString());
        private void ManagePass_Click(object sender, RoutedEventArgs e) => CopyOrAlert((sender as MenuItem).Tag.ToString(), 0);
        private void ManageEmail_Click(object sender, RoutedEventArgs e) => CopyOrAlert((sender as MenuItem).Tag.ToString(), 1);

        private void CopyOrAlert(string acc, int mode)
        {
            acc = acc.ToLower();
            if (credentials.ContainsKey(acc))
            {
                string val = mode == 0 ? credentials[acc].pass : credentials[acc].email;
                if (!string.IsNullOrEmpty(val)) { Clipboard.SetText(val); return; }
            }
            MessageBox.Show("Данные отсутствуют. Откройте БАЗУ TXT и добавьте их в формате:\nL: " + acc + " | P: пароль | E: почта | D: описание");
        }

        // --- ОСТАЛЬНОЕ ---
        private void SetSource_Click(object sender, RoutedEventArgs e)
        {
            sourceID = (sender as Button).Tag.ToString();
            SourceStatus.Text = $" | Источник: {sourceID}";
            SourceStatus.Foreground = Brushes.Gold;
        }

        private async void ApplySource_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(sourceID)) return;
            string targetID = (sender as Button).Tag.ToString();
            string src = Path.Combine(steamPath, "userdata", sourceID);
            string dst = Path.Combine(steamPath, "userdata", targetID);

            await Task.Run(() => {
                if (!Directory.Exists(src)) return;
                foreach (string dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(dir.Replace(src, dst));
                foreach (string file in Directory.GetFiles(src, "*.*", SearchOption.AllDirectories)) File.Copy(file, file.Replace(src, dst), true);
            });
            MessageBox.Show("Настройки успешно перенесены!");
        }

        private void Favorite_Click(object sender, RoutedEventArgs e)
        {
            string acc = (sender as MenuItem).Tag.ToString();
            var favs = File.Exists(favFile) ? File.ReadAllLines(favFile).ToList() : new List<string>();
            if (favs.Contains(acc)) favs.Remove(acc); else favs.Add(acc);
            File.WriteAllLines(favFile, favs); LoadSteamAccounts();
        }

        private void GenerateAccount_Click(object sender, RoutedEventArgs e)
        {
            string l = "Player_" + new Random().Next(10000, 99999);
            File.AppendAllText(dbFile, $"L: {l} | P: Pass123! | E: | D: Новый\n");
            LoadSteamAccounts();
        }

        private void OpenDatabase_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("notepad.exe", dbFile) { UseShellExecute = true });
        private void RefreshList_Click(object sender, RoutedEventArgs e) => LoadSteamAccounts();
    }
}
