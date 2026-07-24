using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vwm.Core.Project;

/// <summary>Loads and saves <see cref="WalkthroughProject"/> as versioned JSON (<c>*.vwmproj</c>).</summary>
public static class ProjectStore
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(WalkthroughProject project)
        => JsonSerializer.Serialize(project, Options);

    public static WalkthroughProject Deserialize(string json)
    {
        WalkthroughProject project;
        try
        {
            project = JsonSerializer.Deserialize<WalkthroughProject>(json, Options)
                ?? throw new InvalidDataException("Not a valid walkthrough project.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Not a valid walkthrough project: {ex.Message}", ex);
        }
        if (project.Version != CurrentVersion)
            throw new NotSupportedException(
                $"Project schema version {project.Version} is not supported by this app (expected {CurrentVersion}).");
        return project;
    }

    public static void Save(WalkthroughProject project, string path)
        => File.WriteAllText(path, Serialize(project));

    public static WalkthroughProject Load(string path)
        => Deserialize(File.ReadAllText(path));
}
