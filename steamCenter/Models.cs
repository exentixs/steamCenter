using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using Newtonsoft.Json;

namespace steamCenter
{
    public class SteamAccount
    {
        public string AccountName { get; set; } = "";
        public string AccountId32 { get; set; } = "";
        public string SteamId64 { get; set; } = "";
        public string PersonaName { get; set; } = "";
        public string Description { get; set; } = "";
        public BitmapImage? Avatar { get; set; }
        public string AvatarPath { get; set; } = "";
        public bool IsFavorite { get; set; }
        public DateTime LastLogin { get; set; }
        public bool HasPassword { get; set; }
        public bool SkipPasswordPrompt { get; set; }

        public string LastLoginFormatted
        {
            get
            {
                if (LastLogin == DateTime.MinValue) return "Был: Никогда";
                var diff = DateTime.Now - LastLogin;
                string ago = diff.TotalDays >= 1 ? $"{diff.Days} дн. назад" :
                            diff.TotalHours >= 1 ? $"{diff.Hours} ч. назад" :
                            diff.TotalMinutes >= 1 ? $"{diff.Minutes} мин. назад" : "Только что";
                return $"Был: {LastLogin:dd.MM.yyyy HH:mm} ({ago})";
            }
        }
    }

    public class SecureAccountData
    {
        public string EncryptedPassword { get; set; } = "";
        public string EncryptedEmail { get; set; } = "";
        public string EncryptedEmailPassword { get; set; } = "";
        public string Description { get; set; } = "";
        public string CreationDate { get; set; } = "";
        public bool AutoCopyConfig { get; set; } = false;
        public bool SkipPasswordPrompt { get; set; } = false;
    }

    public class LoggerService
    {
        private readonly string _logPath;

        public LoggerService()
        {
            var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamSwitcher");
            Directory.CreateDirectory(appDataDir);
            var logDir = Path.Combine(appDataDir, "logs");
            Directory.CreateDirectory(logDir);
            _logPath = Path.Combine(logDir, $"app_{DateTime.Now:yyyyMMdd}.log");
        }

        public void Info(string message) => WriteLog("INFO", message);
        public void Warning(string message) => WriteLog("WARN", message);
        public void Error(string message, Exception? ex = null)
        {
            WriteLog("ERROR", message);
            if (ex != null) WriteLog("EXCEPTION", ex.ToString());
        }

        private void WriteLog(string level, string message)
        {
            try
            {
                File.AppendAllText(_logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {level}: {message}{Environment.NewLine}");
            }
            catch { }
        }
    }

    public class CryptoService
    {
        private readonly byte[] _key;

        public CryptoService()
        {
            var salt = Encoding.UTF8.GetBytes("SteamSwitcherSalt2026");
            using (var deriveBytes = new Rfc2898DeriveBytes("SteamSwitcherSecureKey2026", salt, 100000, HashAlgorithmName.SHA256))
            {
                _key = deriveBytes.GetBytes(32);
            }
        }

        public string Encrypt(string data)
        {
            if (string.IsNullOrEmpty(data)) return "";

            using (var aes = Aes.Create())
            {
                aes.Key = _key;
                aes.GenerateIV();

                using (var encryptor = aes.CreateEncryptor())
                using (var ms = new MemoryStream())
                {
                    ms.Write(aes.IV, 0, aes.IV.Length);
                    using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                    using (var sw = new StreamWriter(cs))
                    {
                        sw.Write(data);
                    }
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
        }

        public string Decrypt(string encrypted)
        {
            if (string.IsNullOrEmpty(encrypted)) return "";

            var fullCipher = Convert.FromBase64String(encrypted);
            using (var aes = Aes.Create())
            {
                var iv = new byte[aes.BlockSize / 8];
                var cipher = new byte[fullCipher.Length - iv.Length];

                Array.Copy(fullCipher, iv, iv.Length);
                Array.Copy(fullCipher, iv.Length, cipher, 0, cipher.Length);

                aes.Key = _key;
                aes.IV = iv;

                using (var decryptor = aes.CreateDecryptor())
                using (var ms = new MemoryStream(cipher))
                using (var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                using (var sr = new StreamReader(cs))
                {
                    return sr.ReadToEnd();
                }
            }
        }
    }

    public class CredentialService
    {
        private readonly LoggerService _logger;
        private readonly CryptoService _crypto;
        private readonly Dictionary<string, SecureAccountData> _accounts = new Dictionary<string, SecureAccountData>();
        private readonly object _lock = new object();

        public CredentialService(LoggerService logger)
        {
            _logger = logger;
            _crypto = new CryptoService();
        }

        private string GetAccountsFile()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamSwitcher", "accounts.json");
        }

        public void Load()
        {
            lock (_lock)
            {
                _accounts.Clear();
                var file = GetAccountsFile();
                if (!File.Exists(file)) return;

                try
                {
                    var json = File.ReadAllText(file);
                    var data = JsonConvert.DeserializeObject<Dictionary<string, SecureAccountData>>(json);
                    foreach (var kvp in data ?? new Dictionary<string, SecureAccountData>())
                    {
                        _accounts[kvp.Key.ToLower()] = kvp.Value;
                    }
                    _logger.Info($"Загружено {_accounts.Count} аккаунтов");
                }
                catch (Exception ex)
                {
                    _logger.Error("Ошибка загрузки базы данных", ex);
                }
            }
        }

        public void Save()
        {
            lock (_lock)
            {
                try
                {
                    var file = GetAccountsFile();
                    var tempFile = file + ".tmp";
                    var dir = Path.GetDirectoryName(file);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    var json = JsonConvert.SerializeObject(_accounts, Formatting.Indented);
                    File.WriteAllText(tempFile, json);
                    File.Move(tempFile, file, true);
                    _logger.Info($"Сохранено {_accounts.Count} аккаунтов");
                }
                catch (Exception ex)
                {
                    _logger.Error("Ошибка сохранения базы данных", ex);
                    throw;
                }
            }
        }

        public string? GetPassword(string login)
        {
            lock (_lock)
            {
                if (_accounts.TryGetValue(login.ToLower(), out var data) && !string.IsNullOrEmpty(data.EncryptedPassword))
                {
                    try { return _crypto.Decrypt(data.EncryptedPassword); }
                    catch { return null; }
                }
                return null;
            }
        }

        public void SetPassword(string login, string password)
        {
            lock (_lock)
            {
                if (!_accounts.ContainsKey(login.ToLower()))
                    _accounts[login.ToLower()] = new SecureAccountData();
                _accounts[login.ToLower()].EncryptedPassword = _crypto.Encrypt(password);
            }
        }

        public string? GetEmail(string login)
        {
            lock (_lock)
            {
                if (_accounts.TryGetValue(login.ToLower(), out var data) && !string.IsNullOrEmpty(data.EncryptedEmail))
                {
                    try { return _crypto.Decrypt(data.EncryptedEmail); }
                    catch { return null; }
                }
                return null;
            }
        }

        public void SetEmail(string login, string email)
        {
            lock (_lock)
            {
                if (!_accounts.ContainsKey(login.ToLower()))
                    _accounts[login.ToLower()] = new SecureAccountData();
                _accounts[login.ToLower()].EncryptedEmail = _crypto.Encrypt(email);
            }
        }

        public string? GetEmailPassword(string login)
        {
            lock (_lock)
            {
                if (_accounts.TryGetValue(login.ToLower(), out var data) && !string.IsNullOrEmpty(data.EncryptedEmailPassword))
                {
                    try { return _crypto.Decrypt(data.EncryptedEmailPassword); }
                    catch { return null; }
                }
                return null;
            }
        }

        public void SetEmailPassword(string login, string emailPassword)
        {
            lock (_lock)
            {
                if (!_accounts.ContainsKey(login.ToLower()))
                    _accounts[login.ToLower()] = new SecureAccountData();
                _accounts[login.ToLower()].EncryptedEmailPassword = _crypto.Encrypt(emailPassword);
            }
        }

        public string? GetDescription(string login)
        {
            lock (_lock)
            {
                return _accounts.TryGetValue(login.ToLower(), out var data) ? data.Description : null;
            }
        }

        public void SetDescription(string login, string description)
        {
            lock (_lock)
            {
                if (!_accounts.ContainsKey(login.ToLower()))
                    _accounts[login.ToLower()] = new SecureAccountData();
                _accounts[login.ToLower()].Description = description;
            }
        }

        public bool GetSkipPasswordPrompt(string login)
        {
            lock (_lock)
            {
                return _accounts.TryGetValue(login.ToLower(), out var data) && data.SkipPasswordPrompt;
            }
        }

        public void SetSkipPasswordPrompt(string login, bool skip)
        {
            lock (_lock)
            {
                if (!_accounts.ContainsKey(login.ToLower()))
                    _accounts[login.ToLower()] = new SecureAccountData();
                _accounts[login.ToLower()].SkipPasswordPrompt = skip;
            }
        }

        public bool GetAutoCopyConfig(string login)
        {
            lock (_lock)
            {
                return _accounts.TryGetValue(login.ToLower(), out var data) && data.AutoCopyConfig;
            }
        }

        public void SetAutoCopyConfig(string login, bool autoCopy)
        {
            lock (_lock)
            {
                if (!_accounts.ContainsKey(login.ToLower()))
                    _accounts[login.ToLower()] = new SecureAccountData();
                _accounts[login.ToLower()].AutoCopyConfig = autoCopy;
            }
        }

        public bool HasAccount(string login) => _accounts.ContainsKey(login.ToLower());
        public void RemoveAccount(string login) => _accounts.Remove(login.ToLower());
        public List<string> GetAllLogins() => _accounts.Keys.ToList();
    }
}