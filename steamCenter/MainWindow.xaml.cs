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

        /// <summary>
        /// Прямой парсер для формата VDF Steam (без кавычек вокруг ID пользователей)
        /// </summary>
        private List<Dictionary<string, string>> ParseLoginUsersVdf(string content)
        {
            var accounts = new List<Dictionary<string, string>>();
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            int i = 0;
            while (i < lines.Length)
            {
                string line = lines[i].Trim();

                // Пропускаем "users" и "{"
                if (line == "\"users\"" || line == "users" || line == "{")
                {
                    i++;
                    continue;
                }

                // Закрывающая скобка в конце
                if (line == "}")
                {
                    i++;
                    continue;
                }

                // Ищем ID пользователя (цифры, может быть в кавычках или без)
                string userId = "";
                if (line.StartsWith("\"") && line.EndsWith("\""))
                {
                    userId = line.Trim('"');
                }
                else if (Regex.IsMatch(line, @"^\d+$"))
                {
                    userId = line;
                }

                if (!string.IsNullOrEmpty(userId) && Regex.IsMatch(userId, @"^\d+$"))
                {
                    var accountData = new Dictionary<string, string>();
                    accountData["SteamId"] = userId;

                    i++; // Переходим к следующей строке (должна быть "{")

                    if (i < lines.Length && lines[i].Trim() == "{")
                    {
                        i++;
                        // Читаем свойства до "}"
                        while (i < lines.Length)
                        {
                            string propLine = lines[i].Trim();
                            if (propLine == "}")
                            {
                                break;
                            }

                            if (!string.IsNullOrEmpty(propLine))
                            {
                                // Парсим "Key" "Value" или "Key" "Value" с табуляцией
                                var match = Regex.Match(propLine, "\"([^\"]+)\"\\s+\"([^\"]*)\"");
                                if (match.Success)
                                {
                                    string key = match.Groups[1].Value;
                                    string value = match.Groups[2].Value;
                                    accountData[key] = value;
                                }
                            }
                            i++;
                        }
                    }

                    if (accountData.ContainsKey("AccountName"))
                    {
                        accounts.Add(accountData);
                        _logger.Info($"Найден аккаунт: {accountData["AccountName"]} ({accountData.GetValueOrDefault("PersonaName", "Unknown")})");
                    }
                }
                i++;
            }

            return accounts;
        }

        private async Task LoadSteamAccounts()
        {
            var vdfPath = Path.Combine(_steamPath, "config", "loginusers.vdf");

            if (!File.Exists(vdfPath))
            {
                MessageBox.Show($"loginusers.vdf не найден:\n{vdfPath}\n\nУбедитесь, что Steam установлен.",
                               "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                // Снимаем атрибут "Только чтение" если он есть
                var fileInfo = new FileInfo(vdfPath);
                if (fileInfo.IsReadOnly)
                {
                    _logger.Info($"Снимаем атрибут 'Только чтение' с файла {vdfPath}");
                    fileInfo.IsReadOnly = false;
                }

                // Читаем файл с правильной кодировкой
                string content = File.ReadAllText(vdfPath, Encoding.UTF8);
                _logger.Info($"Файл loginusers.vdf загружен, размер: {content.Length} байт");

                // Парсим файл
                var parsedAccounts = ParseLoginUsersVdf(content);
                _logger.Info($"Найдено пользователей: {parsedAccounts.Count}");

                var favorites = LoadFavorites();
                _accounts.Clear();

                foreach (var accountData in parsedAccounts)
                {
                    try
                    {
                        var steamId = accountData.GetValueOrDefault("SteamId", "");
                        var accountName = accountData.GetValueOrDefault("AccountName", "Unknown");
                        var personaName = accountData.GetValueOrDefault("PersonaName", "Unknown");

                        DateTime lastLogin = DateTime.MinValue;
                        if (accountData.TryGetValue("Timestamp", out string? timestampStr) && !string.IsNullOrEmpty(timestampStr))
                        {
                            if (long.TryParse(timestampStr, out long timestamp) && timestamp > 0)
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
                    MessageBox.Show($"Не удалось найти аккаунты в файле:\n{vdfPath}\n\n" +
                                   "Возможно, файл поврежден.\n\n" +
                                   "Попробуйте:\n" +
                                   "1. Закрыть Steam\n" +
                                   "2. Удалить файл loginusers.vdf\n" +
                                   "3. Запустить Steam и войти в аккаунт\n" +
                                   "4. Запустить приложение снова",
                                   "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    _logger.Info($"Успешно загружено {_accounts.Count} аккаунтов");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Ошибка загрузки Steam аккаунтов", ex);
                MessageBox.Show($"Ошибка загрузки Steam аккаунтов:\n{ex.Message}\n\n" +
                               $"Путь: {vdfPath}",
                               "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
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

            var loginBtn = new Button
            {
                Content = "ВОЙТИ",
                Style = (Style)FindResource("LoginButton"),
                Margin = new Thickness(5, 0, 10, 0),
                Tag = account
            };
            loginBtn.Click += (s, e) => LoginAccount(account);

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
        /// Остановка всех процессов Steam
        /// </summary>
        private void KillSteamProcesses()
        {
            try
            {
                foreach (var process in Process.GetProcessesByName("steam"))
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                        _logger.Info($"Процесс Steam убит (PID: {process.Id})");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Ошибка убийства Steam процесса {process.Id}", ex);
                    }
                }

                foreach (var process in Process.GetProcessesByName("steamwebhelper"))
                {
                    try
                    {
                        process.Kill();
                        _logger.Info($"Процесс steamwebhelper убит (PID: {process.Id})");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Ошибка убийства steamwebhelper процесса {process.Id}", ex);
                    }
                }

                // Даем время процессам завершиться
                Thread.Sleep(2000);
            }
            catch (Exception ex)
            {
                _logger.Error("Ошибка при остановке процессов Steam", ex);
            }
        }

        /// <summary>
        /// Установка AutoLoginUser в реестре для бесшовного входа
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
                    else
                    {
                        _logger.Warning("Ключ реестра Steam не найден");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Ошибка записи в реестр", ex);
                throw;
            }
        }

        /// <summary>
        /// Запуск Steam без аргументов (он сам прочитает реестр)
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

                var processInfo = new ProcessStartInfo
                {
                    FileName = steamExe,
                    UseShellExecute = true,
                    Verb = "runas" // Запуск с правами администратора если нужно
                };

                Process.Start(processInfo);
                _logger.Info($"Steam запущен: {steamExe}");
            }
            catch (Exception ex)
            {
                _logger.Error("Ошибка запуска Steam", ex);
                throw;
            }
        }

        /// <summary>
        /// Бесшовный вход в аккаунт через реестр (без окон Steam)
        /// </summary>
        private void LoginAccount(SteamAccount account)
        {
            try
            {
                _logger.Info($"Попытка входа в аккаунт: {account.AccountName}");

                // 1. Копируем пароль в буфер обмена (на всякий случай, если токен слетит)
                var password = _credentials.GetPassword(account.AccountName);
                if (!string.IsNullOrEmpty(password))
                {
                    try
                    {
                        Clipboard.SetText(password);
                        _logger.Info("Пароль скопирован в буфер обмена");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("Ошибка копирования пароля в буфер", ex);
                    }
                }

                // 2. Обновляем статус
                StatusLabel.Text = "Закрытие Steam...";

                // 3. Останавливаем все процессы Steam
                KillSteamProcesses();

                // 4. Устанавливаем AutoLoginUser в реестре
                StatusLabel.Text = "Настройка автоматического входа...";
                SetAutoLoginUser(account.AccountName);

                // 5. Запускаем Steam без аргументов
                StatusLabel.Text = "Запуск Steam...";
                StartSteam();

                // 6. Обновляем время последнего входа
                account.LastLogin = DateTime.Now;

                // 7. Показываем уведомление
                ShowNotification($"Вход в {account.AccountName} выполнен успешно!");

                // 8. Обновляем список (чтобы показать новое время)
                UpdateAccountsList();

                StatusLabel.Text = $"Steam запущен с аккаунтом: {account.AccountName}";
                _logger.Info($"Успешный вход в аккаунт: {account.AccountName}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Ошибка входа в аккаунт {account.AccountName}", ex);
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
                ShowNotification($"Конфиги скопированы в {targetId}");
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