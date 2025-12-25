using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using OpenFreqClient.Models;

namespace OpenFreqClient.Services;

public class ConfigurationService : IConfigurationService
{
    private static readonly string ConfigDirectory = 
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenFreq");
    
    private static readonly string ConfigFilePath = 
        Path.Combine(ConfigDirectory, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public async Task<AppConfiguration> LoadConfigurationAsync()
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
            {
                return new AppConfiguration();
            }

            var json = await File.ReadAllTextAsync(ConfigFilePath);
            return JsonSerializer.Deserialize<AppConfiguration>(json, JsonOptions) 
                   ?? new AppConfiguration();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load configuration: {ex.Message}");
            return new AppConfiguration();
        }
    }

    public async Task SaveConfigurationAsync(AppConfiguration config)
    {
        try
        {
            // Ensure directory exists
            Directory.CreateDirectory(ConfigDirectory);

            var json = JsonSerializer.Serialize(config, JsonOptions);
            await File.WriteAllTextAsync(ConfigFilePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save configuration: {ex.Message}");
        }
    }

    public void Dispose()
    {
        // nothing to do yet
    }
}
