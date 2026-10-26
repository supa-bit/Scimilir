namespace Scimilir.Storage;

/// <summary>
/// Where Scimilir keeps its files. Everything lives in one "data" folder:
/// settings, saved chats, login keys and downloaded model files.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string dataFolder)
    {
        Data = Path.GetFullPath(dataFolder);
        Chats = Path.Combine(Data, "chats");
        Models = Path.Combine(Data, "models");
        Keys = Path.Combine(Data, "keys");
        foreach (var folder in new[] { Data, Chats, Models, Keys })
            Directory.CreateDirectory(folder);
    }

    public string Data { get; }
    public string Chats { get; }
    public string Models { get; }
    public string Keys { get; }
    public string SettingsFile => Path.Combine(Data, "settings.json");
}
