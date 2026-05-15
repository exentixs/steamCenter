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
        private Dictionary<string, (string pass, string email)> credentials = new Dictionary<string, (string, string)>();

        public MainWindow()
        {
            InitializeComponent();
            dbFile = Path.Combine(appData, "akk.txt");
            favFile = Path.Combine(appData, "favs.txt");
            if (!Directory.Exists(appData)) Directory.CreateDirectory(appData);
            LoadSteamAccounts();
        }

        public class SteamAccount
        {
            public string AccountName { get; set; }
            public string PersonaName { get; set; }
            public string AccountID32 { get; set; }
            public BitmapImage Avatar { get; set; }
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

                var item = new SteamAccount
                {
                    AccountName = acc,
                    PersonaName = name,
                    AccountID32 = (long.Parse(id64) - 76561197960265728).ToString(),
                    IsFavorite = favs.Contains(acc),
                    Avatar = GetAvatar(id64)
                };

                if (credentials.ContainsKey(acc.ToLower()))
                {
                    if (!string.IsNullOrEmpty(credentials[acc.ToLower()].pass)) item.PassLabel = "📋 Скопировать пароль";
                    if (!string.IsNullOrEmpty(credentials[acc.ToLower()].email)) item.EmailLabel = "📋 Скопировать почту";
                }
                accounts.Add(item);
            }
            AccountsList.ItemsSource = accounts.OrderByDescending(a => a.IsFavorite).ThenBy(a => a.AccountName).ToList();
        }

        // --- ЛОГИКА КОНТЕКСТНОГО МЕНЮ (ПКМ) ---
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
            MessageBox.Show("Данные отсутствуют в akk.txt. Добавьте строку:\nL: " + acc + " | P: пароль | E: почта");
        }

        // --- КОПИРОВАНИЕ ВСЕХ КОНФИГОВ ---
        private void SetSource_Click(object sender, RoutedEventArgs e)
        {
            sourceID = (sender as Button).Tag.ToString();
            MessageBox.Show("Источник выбран. Теперь нажмите 'Применить всем' на другом аккаунте.");
        }

        private async void ApplySource_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(sourceID)) return;
            string targetID = (sender as Button).Tag.ToString();
            string src = Path.Combine(steamPath, "userdata", sourceID);
            string dst = Path.Combine(steamPath, "userdata", targetID);

            await Task.Run(() => {
                if (!Directory.Exists(src)) return;
                foreach (string dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
                    Directory.CreateDirectory(dir.Replace(src, dst));
                foreach (string file in Directory.GetFiles(src, "*.*", SearchOption.AllDirectories))
                    File.Copy(file, file.Replace(src, dst), true);
            });
            MessageBox.Show("Все настройки перенесены!");
        }

        // --- ВСПОМОГАТЕЛЬНЫЕ ---
        private void LoadCredentials()
        {
            credentials.Clear();
            if (!File.Exists(dbFile)) return;
            foreach (var line in File.ReadAllLines(dbFile))
            {
                var m = Regex.Match(line, @"L: (\S+) \| P: (\S+)(?: \| E: (\S+))?");
                if (m.Success) credentials[m.Groups[1].Value.ToLower()] = (m.Groups[2].Value, m.Groups[3].Value);
            }
        }

        private BitmapImage GetAvatar(string id64)
        {
            string path = Path.Combine(steamPath, "config", "avatarcache", id64 + "_full.jpg");
            try
            {
                BitmapImage bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = File.Exists(path) ? new Uri(path) : new Uri("https://avatars.steamstatic.com/fef49e7fa7e1997310d705b2a6158ff8dc1cdfeb_full.jpg");
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                return bi;
            }
            catch { return null; }
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            string acc = (sender as Button).Tag.ToString();
            foreach (var p in Process.GetProcessesByName("steam")) p.Kill();
            await Task.Delay(800);
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", true))
            {
                k.SetValue("AutoLoginUser", acc); k.SetValue("RememberPassword", 1);
            }
            Process.Start(Path.Combine(steamPath, "steam.exe"));
        }

        private void Favorite_Click(object sender, RoutedEventArgs e)
        {
            string acc = (sender as MenuItem != null) ? (sender as MenuItem).Tag.ToString() : (sender as Button).Tag.ToString();
            var favs = File.Exists(favFile) ? File.ReadAllLines(favFile).ToList() : new List<string>();
            if (favs.Contains(acc)) favs.Remove(acc); else favs.Add(acc);
            File.WriteAllLines(favFile, favs); LoadSteamAccounts();
        }

        private void GenerateAccount_Click(object sender, RoutedEventArgs e)
        {
            string l = "Player_" + new Random().Next(10000, 99999);
            File.AppendAllText(dbFile, $"L: {l} | P: Pass123! | E: \n");
            LoadSteamAccounts();
        }

        private void OpenDatabase_Click(object sender, RoutedEventArgs e) => Process.Start("notepad.exe", dbFile);
        private void RefreshList_Click(object sender, RoutedEventArgs e) => LoadSteamAccounts();
    }
}