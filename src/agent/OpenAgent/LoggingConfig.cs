using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models.Providers;
using Serilog.Core;
using Serilog.Events;

namespace OpenAgent;

/// <summary>
/// Runtime-configurable log levels exposed via the admin endpoint.
/// Holds LoggingLevelSwitch instances that Serilog reads in real time.
/// </summary>
internal sealed class LoggingConfig : IConfigurable
{
    private static readonly string[] LogLevels = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];

    private static readonly string[] Modules =
    [
        "OpenAgent",
        "OpenAgent.Api",
        "OpenAgent.LlmText",
        "OpenAgent.LlmVoice",
        "Microsoft.AspNetCore",
        "Microsoft.AspNetCore.Mvc"
    ];

    public string Key => "logging";

    public LoggingLevelSwitch DefaultLevel { get; } = new(LogEventLevel.Debug);

    public Dictionary<string, LoggingLevelSwitch> Overrides { get; } = Modules
        .ToDictionary(m => m, m => new LoggingLevelSwitch(m switch
        {
            "Microsoft.AspNetCore.Mvc" => LogEventLevel.Error,
            "Microsoft.AspNetCore" => LogEventLevel.Information,
            _ => LogEventLevel.Debug
        }));

    public IReadOnlyList<ProviderConfigField> ConfigFields
    {
        get
        {
            var fields = new List<ProviderConfigField>
            {
                new()
                {
                    Key = "defaultLevel",
                    Label = "Default Level",
                    Type = "Enum",
                    Required = true,
                    Options = LogLevels,
                    DefaultValue = "Information"
                }
            };

            foreach (var module in Modules)
            {
                fields.Add(new ProviderConfigField
                {
                    Key = module,
                    Label = module,
                    Type = "Enum",
                    Required = false,
                    Options = LogLevels,
                    DefaultValue = "Information"
                });
            }

            return fields;
        }
    }

    public void Configure(JsonElement configuration)
    {
        if (configuration.TryGetProperty("defaultLevel", out var defaultProp) &&
            Enum.TryParse<LogEventLevel>(defaultProp.GetString(), true, out var defaultLevel))
        {
            DefaultLevel.MinimumLevel = defaultLevel;
        }

        foreach (var (module, levelSwitch) in Overrides)
        {
            if (configuration.TryGetProperty(module, out var moduleProp) &&
                Enum.TryParse<LogEventLevel>(moduleProp.GetString(), true, out var level))
            {
                levelSwitch.MinimumLevel = level;
            }
        }
    }
}
