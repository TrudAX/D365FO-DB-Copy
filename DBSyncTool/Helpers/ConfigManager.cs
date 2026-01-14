using System.Text.Json;
using DBSyncTool.Models;

namespace DBSyncTool.Helpers
{
    public class ConfigManager
    {
        private const string CONFIG_FOLDER = "Config";
        private const string LAST_CONFIG_FILE = ".lastconfig";

        private readonly string _configPath;
        private readonly string _lastConfigPath;

        public ConfigManager()
        {
            try
            {
                // Use Application.StartupPath for WinForms, fallback to current directory
                string appPath = Application.StartupPath;
                _configPath = Path.Combine(appPath, CONFIG_FOLDER);
                _lastConfigPath = Path.Combine(_configPath, LAST_CONFIG_FILE);

                // Ensure Config folder exists
                if (!Directory.Exists(_configPath))
                {
                    Directory.CreateDirectory(_configPath);
                }
            }
            catch
            {
                // Fallback to a simpler approach if path resolution fails
                _configPath = Path.Combine(Directory.GetCurrentDirectory(), CONFIG_FOLDER);
                _lastConfigPath = Path.Combine(_configPath, LAST_CONFIG_FILE);

                if (!Directory.Exists(_configPath))
                {
                    Directory.CreateDirectory(_configPath);
                }
            }
        }

        /// <summary>
        /// Saves a configuration to a JSON file
        /// </summary>
        public void SaveConfiguration(AppConfiguration config)
        {
            if (string.IsNullOrWhiteSpace(config.ConfigName))
                throw new ArgumentException("Configuration name cannot be empty");

            // Validate config name (alphanumeric, underscore, hyphen only)
            if (!IsValidConfigName(config.ConfigName))
                throw new ArgumentException("Configuration name can only contain letters, numbers, underscores, and hyphens");

            // Update last modified
            config.LastModified = DateTime.UtcNow;

            // Clone and obfuscate passwords
            var configToSave = CloneAndObfuscate(config);

            // Save to file
            string filePath = GetConfigFilePath(config.ConfigName);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            string json = JsonSerializer.Serialize(configToSave, options);
            File.WriteAllText(filePath, json);

            // Update last used config
            File.WriteAllText(_lastConfigPath, config.ConfigName);
        }

        /// <summary>
        /// Loads a configuration from a JSON file
        /// </summary>
        public AppConfiguration LoadConfiguration(string configName)
        {
            string filePath = GetConfigFilePath(configName);

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Configuration '{configName}' not found");

            string json = File.ReadAllText(filePath);
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var config = JsonSerializer.Deserialize<AppConfiguration>(json, options);
            if (config == null)
                throw new InvalidOperationException($"Failed to deserialize configuration '{configName}'");

            // Deobfuscate passwords
            DeobfuscatePasswords(config);

            return config;
        }

        /// <summary>
        /// Gets the list of available configuration names
        /// </summary>
        public List<string> GetAvailableConfigurations()
        {
            if (!Directory.Exists(_configPath))
                return new List<string>();

            return Directory.GetFiles(_configPath, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .OrderBy(name => name)
                .ToList();
        }

        /// <summary>
        /// Gets the last used configuration name, or null if none
        /// </summary>
        public string? GetLastUsedConfiguration()
        {
            if (!File.Exists(_lastConfigPath))
                return null;

            try
            {
                string configName = File.ReadAllText(_lastConfigPath).Trim();
                return string.IsNullOrWhiteSpace(configName) ? null : configName;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Loads the last used configuration, or creates a default one
        /// </summary>
        public AppConfiguration LoadLastOrDefault()
        {
            string? lastConfig = GetLastUsedConfiguration();

            // Try to load last used config
            if (lastConfig != null && File.Exists(GetConfigFilePath(lastConfig)))
            {
                try
                {
                    return LoadConfiguration(lastConfig);
                }
                catch
                {
                    // Fall through to create default
                }
            }

            // Try to load "Default" config
            if (File.Exists(GetConfigFilePath("Default")))
            {
                try
                {
                    return LoadConfiguration("Default");
                }
                catch
                {
                    // Fall through to create new default
                }
            }

            // Create and save a new default configuration
            var defaultConfig = AppConfiguration.CreateDefault();
            defaultConfig.ConfigName = "Default";
            SaveConfiguration(defaultConfig);

            return defaultConfig;
        }

        /// <summary>
        /// Validates configuration name format
        /// </summary>
        private bool IsValidConfigName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
                return false;

            return name.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-');
        }

        /// <summary>
        /// Gets the full file path for a configuration
        /// </summary>
        private string GetConfigFilePath(string configName)
        {
            return Path.Combine(_configPath, $"{configName}.json");
        }

        /// <summary>
        /// Clones a configuration and obfuscates passwords
        /// </summary>
        private AppConfiguration CloneAndObfuscate(AppConfiguration config)
        {
            // Serialize and deserialize to clone
            string json = JsonSerializer.Serialize(config);
            var clone = JsonSerializer.Deserialize<AppConfiguration>(json)!;

            // Obfuscate passwords
            clone.Tier2Connection.Password = EncryptionHelper.ObfuscatePassword(clone.Tier2Connection.Password);
            clone.AxDbConnection.Password = EncryptionHelper.ObfuscatePassword(clone.AxDbConnection.Password);

            return clone;
        }

        /// <summary>
        /// Deobfuscates passwords in a configuration
        /// </summary>
        private void DeobfuscatePasswords(AppConfiguration config)
        {
            config.Tier2Connection.Password = EncryptionHelper.DeobfuscatePassword(config.Tier2Connection.Password);
            config.AxDbConnection.Password = EncryptionHelper.DeobfuscatePassword(config.AxDbConnection.Password);
        }
    }
}
