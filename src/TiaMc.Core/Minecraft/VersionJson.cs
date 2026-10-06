using System.Text.Json.Serialization;

namespace TiaMc.Core.Minecraft;

/// <summary>
/// Data model of a Minecraft version JSON file (version/&lt;id&gt;/&lt;id&gt;.json).
/// Field names follow the Mojang launcher specification. The model is tolerant:
/// every collection is created eagerly so merging never has to null-check.
/// </summary>
public sealed class VersionJson
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("inheritsFrom")] public string? InheritsFrom { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "release";
    [JsonPropertyName("mainClass")] public string? MainClass { get; set; }
    [JsonPropertyName("minecraftArguments")] public string? MinecraftArguments { get; set; }
    [JsonPropertyName("assets")] public string? Assets { get; set; }
    [JsonPropertyName("assetIndex")] public AssetIndexJson? AssetIndex { get; set; }
    [JsonPropertyName("downloads")] public Dictionary<string, DownloadJson>? Downloads { get; set; }
    [JsonPropertyName("javaVersion")] public JavaVersionJson? JavaVersion { get; set; }
    [JsonPropertyName("logging")] public LoggingJson? Logging { get; set; }
    [JsonPropertyName("releaseTime")] public string? ReleaseTime { get; set; }
    [JsonPropertyName("time")] public string? Time { get; set; }
    [JsonPropertyName("minimumLauncherVersion")] public int MinimumLauncherVersion { get; set; }
    [JsonPropertyName("libraries")] public List<LibraryJson> Libraries { get; set; } = [];
    [JsonPropertyName("arguments")] public ArgumentsJson? Arguments { get; set; }
    [JsonPropertyName("complianceLevel")] public int? ComplianceLevel { get; set; }
    [JsonPropertyName("_comment")] public string? Comment { get; set; }

    /// <summary>True when the version was produced by a modern Forge/NeoForge installer.</summary>
    [JsonIgnore]
    public bool UsesBootstrapLauncher =>
        Libraries.Any(l => l.Name.Contains("bootstraplauncher", StringComparison.OrdinalIgnoreCase));

    /// <summary>Human readable loader classification, derived from the library list.</summary>
    [JsonIgnore]
    public string LoaderKind
    {
        get
        {
            foreach (var lib in Libraries)
            {
                var n = lib.Name;
                if (n.Contains("net.neoforged", StringComparison.OrdinalIgnoreCase)) return "NeoForge";
                if (n.Contains("minecraftforge", StringComparison.OrdinalIgnoreCase)) return "Forge";
                if (n.Contains("fabricmc", StringComparison.OrdinalIgnoreCase)) return "Fabric";
                if (n.Contains("quiltmc", StringComparison.OrdinalIgnoreCase)) return "Quilt";
            }

            return "Vanilla";
        }
    }
}

public sealed class AssetIndexJson
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("totalSize")] public long TotalSize { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}

public sealed class DownloadJson
{
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
}

public sealed class JavaVersionJson
{
    [JsonPropertyName("component")] public string? Component { get; set; }
    [JsonPropertyName("majorVersion")] public int MajorVersion { get; set; }
}

public sealed class LoggingJson
{
    [JsonPropertyName("client")] public LoggingClientJson? Client { get; set; }
}

public sealed class LoggingClientJson
{
    [JsonPropertyName("argument")] public string? Argument { get; set; }
    [JsonPropertyName("file")] public DownloadJson? File { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
}

public sealed class ArgumentsJson
{
    [JsonPropertyName("game")]
    [JsonConverter(typeof(Json.ArgumentListConverter))]
    public List<ArgumentJson> Game { get; set; } = [];

    [JsonPropertyName("jvm")]
    [JsonConverter(typeof(Json.ArgumentListConverter))]
    public List<ArgumentJson> Jvm { get; set; } = [];
}

/// <summary>One entry of the game/jvm argument list: a plain string or a rule guarded value.</summary>
public sealed class ArgumentJson
{
    [JsonPropertyName("rules")] public List<RuleJson>? Rules { get; set; }

    [JsonPropertyName("value")] public List<string> Value { get; set; } = [];
}

public sealed class LibraryJson
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("downloads")] public LibraryDownloadsJson? Downloads { get; set; }
    [JsonPropertyName("natives")] public Dictionary<string, string>? Natives { get; set; }
    [JsonPropertyName("rules")] public List<RuleJson>? Rules { get; set; }
    [JsonPropertyName("extract")] public ExtractJson? Extract { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("checksums")] public List<string>? Checksums { get; set; }
    [JsonPropertyName("clientreq")] public bool? ClientRequired { get; set; }
    [JsonPropertyName("serverreq")] public bool? ServerRequired { get; set; }
    [JsonPropertyName("MMC-hint")] public string? MmcHint { get; set; }
}

public sealed class LibraryDownloadsJson
{
    [JsonPropertyName("artifact")] public DownloadJson? Artifact { get; set; }
    [JsonPropertyName("classifiers")] public Dictionary<string, DownloadJson>? Classifiers { get; set; }
}

public sealed class ExtractJson
{
    [JsonPropertyName("exclude")] public List<string>? Exclude { get; set; }
}

public sealed class RuleJson
{
    [JsonPropertyName("action")] public string Action { get; set; } = "allow";
    [JsonPropertyName("os")] public RuleOsJson? Os { get; set; }
    [JsonPropertyName("features")] public Dictionary<string, bool>? Features { get; set; }
}

public sealed class RuleOsJson
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("arch")] public string? Arch { get; set; }
}
