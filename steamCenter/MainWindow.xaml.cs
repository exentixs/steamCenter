using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace steamCenter
{
    public partial class MainWindow : Window
    {
        private LoggerService _logger;
        private CredentialService _credentials;
        private List<SteamAccount> _accounts = new List<SteamAccount>();
        private string _sourceId = "";
        private string _steamPath;
        private SteamAccount? _currentContextAccount;

        public MainWindow()
        {
            InitializeComponent();

            _logger = new LoggerService();
            _steamPath = DetectSteamPath();
            _credentials = new CredentialService(_logger);

            Loaded += async (s, e) => await LoadAllData();
        }

        private string DetectSteamPath()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (key != null)
                    {
                        var path = key.GetValue("SteamPath")?.ToString()?.Replace('/', '\\');
                        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                        {
                            _logger?.Info($"Steam найден: {path}");
                            return path;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Error("Ошибка определения пути Steam", ex);
            }

            var defaultPath = @"C:\Program Files (x86)\Steam";
            _logger?.Info($"Используется путь Steam по умолчанию: {defaultPath}");
            return defaultPath;
        }

        private async Task LoadAllData()
        {
            try
            {
                _credentials.Load();
                await LoadSteamAccounts();
                UpdateAccountsList();
                _logger.Info("Данные успешно загружены");
            }
            catch (Exception ex)
            {
                _logger.Error("Ошибка загрузки данных", ex);
                MessageBox.Show($"Ошибка загрузки данных: {ex.Message}", "Ошибка",
                               MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private List<string> LoadFavorites()
        {
            var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamSwitcher");
            var favFile = Path.Combine(appDataDir, "favorites.json");
            if (File.Exists(favFile))
            {
                try
                {
                    var json = File.ReadAllText(favFile);
                    return JsonConvert.DeserializeObject<List<string>>(json) ?? new List<string>();
                }
                catch { }
            }
            return new List<string>();
        }

        private void SaveFavorites(List<string> favorites)
        {
            var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamSwitcher");
            var favFile = Path.Combine(appDataDir, "favorites.json");
            try
            {
                Directory.CreateDirectory(appDataDir);
                File.WriteAllText(favFile + ".tmp", JsonConvert.SerializeObject(favorites));
                File.Move(favFile + ".tmp", favFile, true);
            }
            catch (Exception ex)
            {
                _logger.Error("Ошибка сохранения избранного", ex);
            }
        }

        private async Task LoadSteamAccounts()
        {
            var vdfPath = Path.Combine(_steamPath, "config", "loginusers.vdf");

            if (!File.Exists(vdfPath))
            {
                MessageBox.Show($"loginusers.vdf не найден:\n{vdfPath}", "Ошибка",
                               MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                var fileInfo = new FileInfo(vdfPath);
                if (fileInfo.IsReadOnly)
                {
                    fileInfo.IsReadOnly = false;
                }

                string content = File.ReadAllText(vdfPath, Encoding.UTF8);
                _logger.Info($"Файл loginusers.vdf загружен, размер: {content.Length} байт");

                // Парсим через регулярные выражения для надежности
                var accountMatches = Regex.Matches(content, @"(""\d{17}"")\s*\{([^}]+)\}", RegexOptions.Singleline);
                _logger.Info($"Найдено блоков аккаунтов: {accountMatches.Count}");

                var favorites = LoadFavorites();
                _accounts.Clear();

                foreach (Match match in accountMatches)
                {
                    try
                    {
                        string steamId = match.Groups[1].Value.Trim('"');
                        string blockContent = match.Groups[2].Value;

                        // Извлекаем AccountName
                        var nameMatch = Regex.Match(blockContent, @"""AccountName""\s*""([^""]+)""", RegexOptions.IgnoreCase);
                        if (!nameMatch.Success) continue;

                        string accountName = nameMatch.Groups[1].Value;

                        // Извлекаем PersonaName
                        var personaMatch = Regex.Match(blockContent, @"""PersonaName""\s*""([^""]+)""", RegexOptions.IgnoreCase);
                        string personaName = personaMatch.Success ? personaMatch.Groups[1].Value : accountName;

                        // Извлекаем Timestamp
                        DateTime lastLogin = DateTime.MinValue;
                        var timestampMatch = Regex.Match(blockContent, @"""Timestamp""\s*""([^""]+)""", RegexOptions.IgnoreCase);
                        if (timestampMatch.Success && long.TryParse(timestampMatch.Groups[1].Value, out long timestamp) && timestamp > 0)
                        {
                            lastLogin = DateTimeOffset.FromUnixTimeSeconds(timestamp).DateTime;
                        }

                        var hasPassword = _credentials.HasAccount(accountName);
                        var description = _credentials.GetDescription(accountName) ?? "";
                        var skipPasswordPrompt = _credentials.GetSkipPasswordPrompt(accountName);
                        var isFavorite = favorites.Contains(accountName);

                        var avatarPath = await DownloadAvatar(steamId);
                        BitmapImage? avatar = null;
                        if (!string.IsNullOrEmpty(avatarPath) && File.Exists(avatarPath))
                        {
                            try { avatar = new BitmapImage(new Uri(avatarPath)); }
                            catch { }
                        }

                        _accounts.Add(new SteamAccount
                        {
                            AccountName = accountName,
                            AccountId32 = steamId,
                            SteamId64 = steamId,
                            PersonaName = personaName,
                            Description = description,
                            Avatar = avatar,
                            AvatarPath = avatarPath,
                            IsFavorite = isFavorite,
                            LastLogin = lastLogin,
                            HasPassword = hasPassword,
                            SkipPasswordPrompt = skipPasswordPrompt
                        });

                        _logger.Info($"Добавлен аккаунт: {accountName} ({personaName})");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Ошибка обработки аккаунта", ex);
                    }
                }

                _accounts = _accounts.OrderByDescending(x => x.IsFavorite)
                                     .ThenByDescending(x => x.LastLogin)
                                     .ToList();

                StatusLabel.Text = $"Аккаунтов загружено: {_accounts.Count}";

                if (_accounts.Count == 0)
                {
                    _logger.Warning("Аккаунты не найдены в loginusers.vdf");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Ошибка загрузки Steam аккаунтов", ex);
                MessageBox.Show($"Ошибка загрузки Steam аккаунтов:\n{ex.Message}", "Ошибка",
                               MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task<string> DownloadAvatar(string steamId)
        {
            try
            {
                if (string.IsNullOrEmpty(steamId))
                    return "";

                var avatarDir = Path.Combine(_steamPath, "config", "avatarcache");
                Directory.CreateDirectory(avatarDir);

                var possibleFiles = new[]
                {
                    Path.Combine(avatarDir, $"{steamId}_full.jpg"),
                    Path.Combine(avatarDir, $"{steamId}.jpg"),
                    Path.Combine(avatarDir, $"{steamId}.png")
                };

                foreach (var file in possibleFiles)
                {
                    if (File.Exists(file))
                        return file;
                }

                var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamSwitcher");
                var url = $"https://avatars.steamstatic.com/{steamId}_full.jpg";
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(10);
                    var response = await client.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        var avatarPath = Path.Combine(appDataDir, "avatars", $"{steamId}.jpg");
                        Directory.CreateDirectory(Path.GetDirectoryName(avatarPath) ?? "");
                        var data = await response.Content.ReadAsByteArrayAsync();
                        await File.WriteAllBytesAsync(avatarPath, data);
                        return avatarPath;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Ошибка загрузки аватара для {steamId}", ex);
            }
            return "";
        }

        private void UpdateAccountsList()
        {
            AccountsPanel.Children.Clear();

            foreach (var account in _accounts)
            {
                var widget = CreateAccountWidget(account);
                AccountsPanel.Children.Add(widget);
            }

            StatusLabel.Text = string.IsNullOrEmpty(_sourceId) ? " | Конфиг не выбран" : $" | Источник: {_sourceId}";
        }

        private FrameworkElement CreateAccountWidget(SteamAccount account)
        {
            var border = new Border
            {
                Style = (Style)FindResource("AccountCard"),
                Tag = account
            };

            var grid = new Grid();
            border.Child = grid;

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (account.IsFavorite)
            {
                var starLabel = new TextBlock
                {
                    Text = "★",
                    Foreground = Brushes.Gold,
                    FontSize = 26,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(5, 0, 10, 0)
                };
                Grid.SetColumn(starLabel, 0);
                grid.Children.Add(starLabel);
            }

            var avatarBorder = new Border
            {
                Width = 55,
                Height = 55,
                CornerRadius = new CornerRadius(27.5),
                Margin = new Thickness(5),
                Background = new SolidColorBrush(Color.FromRgb(61, 90, 112))
            };

            if (account.Avatar != null)
            {
                var avatarImg = new Image
                {
                    Source = account.Avatar,
                    Stretch = Stretch.UniformToFill
                };
                avatarBorder.Child = avatarImg;
            }
            else
            {
                var defaultAvatar = new TextBlock
                {
                    Text = "👤",
                    Foreground = new SolidColorBrush(Color.FromRgb(61, 90, 112)),
                    FontSize = 30,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                avatarBorder.Child = defaultAvatar;
            }
            Grid.SetColumn(avatarBorder, 1);
            grid.Children.Add(avatarBorder);

            var infoPanel = new StackPanel
            {
                Margin = new Thickness(10, 5, 10, 5),
                VerticalAlignment = VerticalAlignment.Center
            };

            infoPanel.Children.Add(new TextBlock
            {
                Text = account.PersonaName,
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.Bold
            });

            infoPanel.Children.Add(new TextBlock
            {
                Text = account.AccountName,
                Foreground = (SolidColorBrush)FindResource("SteamBlue"),
                FontSize = 10
            });

            if (!string.IsNullOrEmpty(account.Description))
            {
                infoPanel.Children.Add(new TextBlock
                {
                    Text = account.Description,
                    Foreground = Brushes.Gold,
                    FontSize = 10,
                    FontStyle = FontStyles.Italic
                });
            }

            infoPanel.Children.Add(new TextBlock
            {
                Text = account.LastLoginFormatted,
                Foreground = (SolidColorBrush)FindResource("TextGray"),
                FontSize = 9
            });

            if (account.HasPassword)
            {
                infoPanel.Children.Add(new TextBlock
                {
                    Text = "✓ Пароль сохранен",
                    Foreground = (SolidColorBrush)FindResource("SuccessGreen"),
                    FontSize = 9
                });
            }

            if (account.SkipPasswordPrompt)
            {
                infoPanel.Children.Add(new TextBlock
                {
                    Text = "🔕 Уведомления отключены",
                    Foreground = (SolidColorBrush)FindResource("WarningOrange"),
                    FontSize = 9
                });
            }

            Grid.SetColumn(infoPanel, 2);
            grid.Children.Add(infoPanel);

            var configPanel = new StackPanel
            {
                Margin = new Thickness(5),
                VerticalAlignment = VerticalAlignment.Center
            };

            var takeBtn = new Button
            {
                Content = "ВЗЯТЬ КОНФИГИ",
                Style = (Style)FindResource("ConfigButton"),
                Tag = account
            };
            takeBtn.Click += (s, e) => SetSource(account);

            var applyBtn = new Button
            {
                Content = "ПРИМЕНИТЬ ВСЕМ",
                Style = (Style)FindResource("ConfigButton"),
                Background = new SolidColorBrush(Color.FromRgb(61, 90, 112)),
                Foreground = (SolidColorBrush)FindResource("SteamBlue"),
                Margin = new Thickness(0, 5, 0, 0),
                Tag = account
            };
            applyBtn.Click += (s, e) => ApplySource(account);

            configPanel.Children.Add(takeBtn);
            configPanel.Children.Add(applyBtn);

            Grid.SetColumn(configPanel, 3);
            grid.Children.Add(configPanel);

            // В Tag передаем ТОЛЬКО логин (строку), а не весь объект!
            var loginBtn = new Button
            {
                Content = "ВОЙТИ",
                Style = (Style)FindResource("LoginButton"),
                Margin = new Thickness(5, 0, 10, 0),
                Tag = account.AccountName
            };
            loginBtn.Click += LoginButton_Click;

            Grid.SetColumn(loginBtn, 4);
            grid.Children.Add(loginBtn);

            border.MouseRightButtonDown += (s, e) =>
            {
                _currentContextAccount = account;
                ShowContextMenu();
                e.Handled = true;
            };

            return border;
        }

        private void ShowContextMenu()
        {
            var contextMenu = new ContextMenu();
            contextMenu.Background = new SolidColorBrush(Color.FromRgb(42, 71, 94));
            contextMenu.Foreground = Brushes.White;

            var copyLogin = new MenuItem { Header = "📋 Скопировать Логин", Tag = "copy_login" };
            copyLogin.Click += ContextMenuItem_Click;
            contextMenu.Items.Add(copyLogin);

            var copyPassword = new MenuItem { Header = "📋 Скопировать Пароль", Tag = "copy_password" };
            copyPassword.Click += ContextMenuItem_Click;
            contextMenu.Items.Add(copyPassword);

            var copyEmail = new MenuItem { Header = "📧 Скопировать Email", Tag = "copy_email" };
            copyEmail.Click += ContextMenuItem_Click;
            contextMenu.Items.Add(copyEmail);

            contextMenu.Items.Add(new Separator());

            var editDesc = new MenuItem { Header = "📝 Изменить описание", Tag = "edit_desc" };
            editDesc.Click += ContextMenuItem_Click;
            contextMenu.Items.Add(editDesc);

            var editAccount = new MenuItem { Header = "✏️ Редактировать данные", Tag = "edit_account" };
            editAccount.Click += ContextMenuItem_Click;
            contextMenu.Items.Add(editAccount);

            contextMenu.Items.Add(new Separator());

            var toggleFav = new MenuItem { Header = "⭐ В избранное / Убрать", Tag = "toggle_fav" };
            toggleFav.Click += ContextMenuItem_Click;
            contextMenu.Items.Add(toggleFav);

            var toggleSkip = new MenuItem { Header = "🔕 Больше не спрашивать пароль", Tag = "toggle_skip" };
            toggleSkip.Click += ContextMenuItem_Click;
            contextMenu.Items.Add(toggleSkip);

            contextMenu.Items.Add(new Separator());

            var delete = new MenuItem { Header = "🗑️ Удалить аккаунт", Tag = "delete" };
            delete.Click += ContextMenuItem_Click;
            contextMenu.Items.Add(delete);

            contextMenu.IsOpen = true;
        }

        private async void ContextMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuItem;
            if (item?.Tag == null || _currentContextAccount == null) return;

            switch (item.Tag.ToString())
            {
                case "copy_login":
                    Clipboard.SetText(_currentContextAccount.AccountName);
                    ShowNotification("Логин скопирован!");
                    break;
                case "copy_password":
                    var pwd = _credentials.GetPassword(_currentContextAccount.AccountName);
                    if (!string.IsNullOrEmpty(pwd))
                    {
                        Clipboard.SetText(pwd);
                        ShowNotification("Пароль скопирован!");
                    }
                    else MessageBox.Show("Пароль не сохранен для этого аккаунта.", "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                case "copy_email":
                    var email = _credentials.GetEmail(_currentContextAccount.AccountName);
                    if (!string.IsNullOrEmpty(email))
                    {
                        Clipboard.SetText(email);
                        ShowNotification("Email скопирован!");
                    }
                    else MessageBox.Show("Email не сохранен для этого аккаунта.", "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                case "edit_desc":
                    await EditDescription();
                    break;
                case "edit_account":
                    await EditAccount();
                    break;
                case "toggle_fav":
                    await ToggleFavorite();
                    break;
                case "toggle_skip":
                    await ToggleSkipPassword();
                    break;
                case "delete":
                    await DeleteAccount();
                    break;
            }
        }

        /// <summary>
        /// Безопасное закрытие Steam через команду -shutdown
        /// </summary>
        private void ShutdownSteamGracefully()
        {
            try
            {
                if (Process.GetProcessesByName("steam").Length > 0)
                {
                    _logger.Info("Отправляем команду -shutdown в Steam");
                    var shutdownProc = Process.Start(Path.Combine(_steamPath, "steam.exe"), "-shutdown");
                    shutdownProc?.WaitForExit(10000);
                    Thread.Sleep(2000);
                    _logger.Info("Steam получил команду на закрытие");
                }

                foreach (var process in Process.GetProcessesByName("steamwebhelper"))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Ошибка при закрытии Steam", ex);
            }
        }

        /// <summary>
        /// Безопасное обновление файла loginusers.vdf
        /// </summary>
        private void UpdateLoginUsersVdf(string targetLogin)
        {
            string vdfPath = Path.Combine(_steamPath, "config", "loginusers.vdf");
            if (!File.Exists(vdfPath))
            {
                _logger.Warning("Файл loginusers.vdf не найден.");
                return;
            }

            try
            {
                // Читаем все строки файла, чтобы не нарушать его кодировку и структуру
                List<string> lines = File.ReadAllLines(vdfPath, Encoding.UTF8).ToList();

                string targetLower = targetLogin.Trim().ToLower();

                int currentAccountStartIndex = -1;
                int targetAccountStartIndex = -1;

                // ШАГ 1: Находим, в каком месте файла лежит именно наш аккаунт
                for (int i = 0; i < lines.Count; i++)
                {
                    string line = lines[i].Trim();

                    // Если строка — это SteamID64 (начинается на "7656...")
                    if (line.StartsWith("\"7656") && i + 1 < lines.Count && lines[i + 1].Contains("{"))
                    {
                        currentAccountStartIndex = i;
                    }

                    // Ищем AccountName целевого аккаунта
                    if (line.StartsWith("\"AccountName\"", StringComparison.OrdinalIgnoreCase))
                    {
                        // Извлекаем значение между кавычками
                        var matches = Regex.Matches(line, @"""([^""]+)""");
                        if (matches.Count >= 2)
                        {
                            string accountInFile = matches[1].Groups[1].Value.Trim().ToLower();
                            if (accountInFile == targetLower)
                            {
                                targetAccountStartIndex = currentAccountStartIndex;
                                _logger.Info($"Найден аккаунт: {accountInFile} на строке {i}");
                            }
                        }
                    }
                }

                if (targetAccountStartIndex == -1)
                {
                    _logger.Warning($"Логин '{targetLogin}' не найден в loginusers.vdf.");
                    return;
                }

                // ШАГ 2: Проходим по файлу и точечно меняем только значения флагов 0 и 1
                currentAccountStartIndex = -1;
                for (int i = 0; i < lines.Count; i++)
                {
                    string line = lines[i].Trim();

                    if (line.StartsWith("\"7656") && i + 1 < lines.Count && lines[i + 1].Contains("{"))
                    {
                        currentAccountStartIndex = i;
                    }

                    // Меняем MostRecent (самый последний запущенный аккаунт)
                    if (line.StartsWith("\"MostRecent\"", StringComparison.OrdinalIgnoreCase))
                    {
                        if (currentAccountStartIndex == targetAccountStartIndex)
                        {
                            lines[i] = lines[i].Replace("\"0\"", "\"1\"");
                            _logger.Info($"Установлен MostRecent=1 для {targetLogin}");
                        }
                        else if (currentAccountStartIndex != -1)
                        {
                            lines[i] = lines[i].Replace("\"1\"", "\"0\"");
                        }
                    }

                    // Для нашего целевого аккаунта принудительно включаем сохранение пароля
                    if (line.StartsWith("\"RememberPassword\"", StringComparison.OrdinalIgnoreCase) && currentAccountStartIndex == targetAccountStartIndex)
                    {
                        lines[i] = lines[i].Replace("\"0\"", "\"1\"");
                        _logger.Info($"Установлен RememberPassword=1 для {targetLogin}");
                    }

                    // Выключаем автономный режим, чтобы Стим не ругался на отсутствие сети
                    if (line.StartsWith("\"WantsOfflineMode\"", StringComparison.OrdinalIgnoreCase) && currentAccountStartIndex == targetAccountStartIndex)
                    {
                        lines[i] = lines[i].Replace("\"1\"", "\"0\"");
                    }

                    // Включаем автоматический вход
                    if (line.StartsWith("\"AllowAutoLogin\"", StringComparison.OrdinalIgnoreCase) && currentAccountStartIndex == targetAccountStartIndex)
                    {
                        lines[i] = lines[i].Replace("\"0\"", "\"1\"");
                    }
                }

                // Записываем чистые строки обратно в файл
                File.WriteAllLines(vdfPath, lines, Encoding.UTF8);
                _logger.Info($"Файл loginusers.vdf успешно обновлен для: {targetLogin}");
            }
            catch (Exception ex)
            {
                _logger.Error("Ошибка при безопасном редактировании файла loginusers.vdf", ex);
            }
        }

        /// <summary>
        /// Установка AutoLoginUser в реестре
        /// </summary>
        private void SetAutoLoginUser(string accountName)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", true))
                {
                    if (key != null)
                    {
                        key.SetValue("AutoLoginUser", accountName, RegistryValueKind.String);
                        key.SetValue("RememberPassword", 1, RegistryValueKind.DWord);
                        _logger.Info($"Установлен AutoLoginUser: {accountName}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Ошибка записи в реестр", ex);
            }
        }

        /// <summary>
        /// Запуск Steam БЕЗ аргументов -login
        /// </summary>
        private void StartSteam()
        {
            try
            {
                var steamExe = Path.Combine(_steamPath, "steam.exe");
                if (!File.Exists(steamExe))
                {
                    throw new FileNotFoundException($"steam.exe не найден по пути: {steamExe}");
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = steamExe,
                    UseShellExecute = true
                });
                _logger.Info($"Steam запущен: {steamExe}");
            }
            catch (Exception ex)
            {
                _logger.Error("Ошибка запуска Steam", ex);
                throw;
            }
        }

        /// <summary>
        /// Главный метод входа в аккаунт
        /// </summary>
        private void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag == null) return;

            string accountName = btn.Tag.ToString();
            _logger.Info($"Попытка входа в аккаунт: {accountName}");

            try
            {
                // 1. Копируем пароль в буфер обмена (на всякий случай)
                var password = _credentials.GetPassword(accountName);
                if (!string.IsNullOrEmpty(password))
                {
                    try { Clipboard.SetText(password); } catch { }
                }

                // 2. Обновляем статус
                StatusLabel.Text = "Закрытие Steam...";

                // 3. Безопасно закрываем Steam
                ShutdownSteamGracefully();

                // 4. Обновляем VDF файл
                StatusLabel.Text = "Обновление конфигурации Steam...";
                UpdateLoginUsersVdf(accountName);

                // 5. Устанавливаем AutoLoginUser в реестре
                StatusLabel.Text = "Настройка автоматического входа...";
                SetAutoLoginUser(accountName);

                // 6. Запускаем Steam БЕЗ ПАРАМЕТРОВ -login!
                StatusLabel.Text = "Запуск Steam...";
                StartSteam();

                // 7. Обновляем время последнего входа
                var account = _accounts.FirstOrDefault(a => a.AccountName == accountName);
                if (account != null)
                {
                    account.LastLogin = DateTime.Now;
                }

                // 8. Показываем уведомление
                ShowNotification($"Вход в {accountName} выполнен!");

                // 9. Обновляем список
                UpdateAccountsList();

                StatusLabel.Text = $"Steam запущен с аккаунтом: {accountName}";
                _logger.Info($"Успешный вход в аккаунт: {accountName}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Ошибка входа в аккаунт {accountName}", ex);
                MessageBox.Show($"Ошибка входа: {ex.Message}", "Ошибка",
                               MessageBoxButton.OK, MessageBoxImage.Error);
                StatusLabel.Text = "Ошибка входа";
            }
        }

        private async void OpenAddAccount()
        {
            var dialog = new AddAccountDialog();
            dialog.Owner = this;
            if (dialog.ShowDialog() == true && dialog.ResultData != null)
            {
                var login = dialog.ResultData.Login.ToLower();

                if (!_credentials.HasAccount(login))
                {
                    _credentials.SetPassword(login, dialog.ResultData.Password);
                    _credentials.SetEmail(login, dialog.ResultData.Email);
                    _credentials.SetEmailPassword(login, dialog.ResultData.EmailPassword);
                    _credentials.SetDescription(login, dialog.ResultData.Description);
                    _credentials.SetAutoCopyConfig(login, dialog.ResultData.AutoCopyConfig);
                    _credentials.Save();
                    await LoadAllData();
                    ShowNotification($"Аккаунт {login} добавлен");
                }
                else
                {
                    MessageBox.Show("Аккаунт уже существует!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async Task EditAccount()
        {
            if (_currentContextAccount == null) return;

            var acc = _currentContextAccount;
            var dialog = new EditAccountDialog(
                acc.AccountName,
                _credentials.GetPassword(acc.AccountName) ?? "",
                _credentials.GetEmail(acc.AccountName) ?? "",
                _credentials.GetEmailPassword(acc.AccountName) ?? "",
                _credentials.GetDescription(acc.AccountName) ?? "",
                _credentials.GetAutoCopyConfig(acc.AccountName),
                _credentials.GetSkipPasswordPrompt(acc.AccountName));
            dialog.Owner = this;

            if (dialog.ShowDialog() == true && dialog.ResultData != null)
            {
                var data = dialog.ResultData;
                _credentials.SetPassword(data.Login, data.Password);
                _credentials.SetEmail(data.Login, data.Email);
                _credentials.SetEmailPassword(data.Login, data.EmailPassword);
                _credentials.SetDescription(data.Login, data.Description);
                _credentials.SetAutoCopyConfig(data.Login, data.AutoCopyConfig);
                _credentials.SetSkipPasswordPrompt(data.Login, data.SkipPasswordPrompt);
                _credentials.Save();
                await LoadAllData();
                ShowNotification("Данные обновлены");
            }
        }

        private async Task DeleteAccount()
        {
            if (_currentContextAccount == null) return;

            var acc = _currentContextAccount;
            if (MessageBox.Show($"Удалить аккаунт {acc.AccountName}? Это действие нельзя отменить.",
                               "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _credentials.RemoveAccount(acc.AccountName);
                _credentials.Save();
                await LoadAllData();
                ShowNotification($"Аккаунт {acc.AccountName} удален");
            }
        }

        private async Task EditDescription()
        {
            if (_currentContextAccount == null) return;

            var acc = _currentContextAccount;
            var currentDesc = _credentials.GetDescription(acc.AccountName) ?? "";
            var dialog = new TextInputDialog($"Введите описание для {acc.AccountName}", currentDesc);
            dialog.Owner = this;

            if (dialog.ShowDialog() == true)
            {
                _credentials.SetDescription(acc.AccountName, dialog.InputText);
                _credentials.Save();
                await LoadAllData();
                ShowNotification("Описание обновлено");
            }
        }

        private async Task ToggleSkipPassword()
        {
            if (_currentContextAccount == null) return;

            var acc = _currentContextAccount;
            var current = _credentials.GetSkipPasswordPrompt(acc.AccountName);
            _credentials.SetSkipPasswordPrompt(acc.AccountName, !current);
            _credentials.Save();
            await LoadAllData();
            ShowNotification(!current ? "Уведомления о пароле отключены" : "Уведомления о пароле включены");
        }

        private async Task ToggleFavorite()
        {
            if (_currentContextAccount == null) return;

            var acc = _currentContextAccount;
            var favorites = LoadFavorites();

            if (favorites.Contains(acc.AccountName))
            {
                favorites.Remove(acc.AccountName);
                ShowNotification($"Аккаунт {acc.AccountName} удален из избранного");
            }
            else
            {
                favorites.Add(acc.AccountName);
                ShowNotification($"Аккаунт {acc.AccountName} добавлен в избранное");
            }

            SaveFavorites(favorites);
            await LoadAllData();
        }

        private void SetSource(SteamAccount account)
        {
            _sourceId = account.AccountId32;
            StatusLabel.Text = $" | Источник: {_sourceId}";
            ShowNotification($"Источник конфигов установлен: {account.AccountName}");
        }

        private void ApplySource(SteamAccount account)
        {
            if (string.IsNullOrEmpty(_sourceId))
            {
                MessageBox.Show("Сначала выберите источник конфигов (кнопка 'ВЗЯТЬ КОНФИГИ')",
                               "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                CopyConfigurations(_sourceId, account.AccountId32);
                MessageBox.Show($"Настройки успешно скопированы в {account.AccountName}!",
                               "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка копирования: {ex.Message}", "Ошибка",
                               MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CopyConfigurations(string sourceId, string targetId)
        {
            try
            {
                var src = Path.Combine(_steamPath, "userdata", sourceId);
                var dst = Path.Combine(_steamPath, "userdata", targetId);

                if (!Directory.Exists(src))
                {
                    _logger.Warning($"Исходная папка конфигов не найдена: {src}");
                    return;
                }

                Directory.CreateDirectory(dst);

                var allowedExtensions = new[] { ".vdf", ".cfg", ".txt" };
                var allowedFolders = new[] { "config", "remote" };

                foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(file).ToLower();
                    var relativePath = Path.GetRelativePath(src, file);

                    if (allowedExtensions.Contains(ext) && allowedFolders.Any(f => relativePath.StartsWith(f)))
                    {
                        var targetFile = file.Replace(src, dst);
                        var targetDir = Path.GetDirectoryName(targetFile);
                        if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
                        File.Copy(file, targetFile, true);
                    }
                }

                _logger.Info($"Скопированы конфиги из {sourceId} в {targetId}");
                ShowNotification($"Конфиги скопированы");
            }
            catch (Exception ex)
            {
                _logger.Error($"Ошибка копирования конфигов из {sourceId} в {targetId}", ex);
                throw;
            }
        }

        private void OpenDatabase()
        {
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamSwitcher");
                Process.Start("explorer.exe", path);
            }
            catch (Exception ex)
            {
                _logger.Error("Ошибка открытия папки с данными", ex);
                MessageBox.Show($"Не удалось открыть папку: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void RefreshList()
        {
            await LoadAllData();
            ShowNotification("Список обновлен!");
        }

        private void ShowNotification(string message)
        {
            var notification = new NotificationWindow(message, this);
            notification.Show();
        }

        private void BtnNewAccount_Click(object sender, RoutedEventArgs e) => OpenAddAccount();
        private void BtnOpenDatabase_Click(object sender, RoutedEventArgs e) => OpenDatabase();
        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshList();
    }
}