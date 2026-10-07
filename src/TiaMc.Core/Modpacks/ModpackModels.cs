using System.Text.Json.Serialization;

namespace TiaMc.Core.Modpacks;

/// <summary>Client pack or server pack. Server packs are highlighted in the UI.</summary>
public enum ModpackKind
{
    Client,
    Server
}

/// <summary>Where a pack came from, which decides how it is installed.</summary>
public enum ModpackFormat
{
    /// <summary>Modrinth .mrpack (modrinth.index.json).</summary>
    Modrinth,
    /// <summary>CurseForge zip (manifest.json).</summary>
    CurseForge,
    /// <summary>Forge / NeoForge server installer jar.</summary>
    InstallerJar,
    /// <summary>Fabric / Quilt server launcher jar.</summary>
    ServerJar,
    /// <summary>A plain zip: overrides only.</summary>
    Archive,
    /// <summary>An existing unpacked folder.</summary>
    Folder
}

/// <summary>One file a pack wants: relative path, download url and hash.</summary>
public sealed class ModpackFile
{
    public string Path { get; set; } = "";
    public string Url { get; set; } = "";
    public long Size { get; set; }
    public string Sha1 { get; set; } = "";
    public string Sha512 { get; set; } = "";
    /// <summary>"client", "server" or "both" (Modrinth env fields).</summary>
    public string Side { get; set; } = "both";

    public bool IsMod => Path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)
                         && Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);

    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>Parsed modpack metadata, format independent.</summary>
public sealed class Modpack
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Summary { get; set; } = "";
    public ModpackKind Kind { get; set; } = ModpackKind.Client;
    public ModpackFormat Format { get; set; } = ModpackFormat.Archive;
    public string GameVersion { get; set; } = "";
    public string Loader { get; set; } = "";
    public string LoaderVersion { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public List<ModpackFile> Files { get; set; } = [];
    /// <summary>Folder inside the archive that holds the overrides (CurseForge uses "overrides").</summary>
    public string OverrideFolder { get; set; } = "overrides";
    /// <summary>Entry jar of a server pack (Forge installer, fabric server jar, ...).</summary>
    public string ServerJar { get; set; } = "";
    /// <summary>Files found inside a plain archive (relative paths).</summary>
    public List<string> ArchiveEntries { get; set; } = [];

    [JsonIgnore] public int ModCount => Files.Count(f => f.IsMod) + ArchiveModCount;

    /// <summary>Mods found inside a plain archive (no manifest).</summary>
    [JsonIgnore] public int ArchiveModCount { get; set; }
    [JsonIgnore] public long TotalSize => Files.Sum(f => f.Size);
    [JsonIgnore] public bool IsServer => Kind == ModpackKind.Server;
    [JsonIgnore] public string KindText => IsServer ? "服务端整合包" : "客户端整合包";
    [JsonIgnore] public string LoaderText => string.IsNullOrEmpty(LoaderVersion) ? Loader : $"{Loader} {LoaderVersion}";
}

/// <summary>modpack.json written next to an installed pack.</summary>
public sealed class InstalledModpack
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>包内清单（modrinth.index.json / manifest.json / mmc-pack.json）声明的名字。</summary>
    [JsonPropertyName("declaredName")] public string DeclaredName { get; set; } = "";

    /// <summary>列表显示用名字：优先包内声明的名字，其次文件夹名。</summary>
    [JsonIgnore]
    public string DisplayName => !string.IsNullOrWhiteSpace(DeclaredName) ? DeclaredName : Name;
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "client";
    [JsonPropertyName("format")] public string Format { get; set; } = "Archive";
    [JsonPropertyName("gameVersion")] public string GameVersion { get; set; } = "";
    [JsonPropertyName("loader")] public string Loader { get; set; } = "";
    [JsonPropertyName("loaderVersion")] public string LoaderVersion { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("sourceFile")] public string SourceFile { get; set; } = "";
    [JsonPropertyName("installedUtc")] public DateTime InstalledUtc { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("modCount")] public int ModCount { get; set; }
    [JsonPropertyName("missingFiles")] public int MissingFiles { get; set; }
    [JsonPropertyName("serverJar")] public string ServerJar { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    /// <summary>Version id this pack was deployed into (empty when it is standalone).</summary>
    [JsonPropertyName("instanceId")] public string InstanceId { get; set; } = "";
    /// <summary>Game directory the pack was copied into.</summary>
    [JsonPropertyName("gameDirectory")] public string GameDirectory { get; set; } = "";

    [JsonIgnore] public bool HasInstance => InstanceId.Length > 0;

    [JsonIgnore]
    public string InstanceText => HasInstance ? $"已加入实例 {InstanceId}" : "未加入实例（仅导入整合包目录）";

    [JsonIgnore] public bool IsServer => Kind.Equals("server", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public string KindText => IsServer ? "服务端整合包" : "客户端整合包";
    [JsonIgnore] public string SizeText { get; set; } = "";
}

// ------------------------------------------------------------------ Modrinth

internal sealed class ModrinthIndex
{
    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; }
    [JsonPropertyName("game")] public string Game { get; set; } = "";
    [JsonPropertyName("versionId")] public string VersionId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("summary")] public string? Summary { get; set; }
    [JsonPropertyName("files")] public List<ModrinthIndexFile> Files { get; set; } = [];
    [JsonPropertyName("dependencies")] public Dictionary<string, string> Dependencies { get; set; } = [];
}

internal sealed class ModrinthIndexFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("hashes")] public Dictionary<string, string> Hashes { get; set; } = [];
    [JsonPropertyName("env")] public Dictionary<string, string>? Env { get; set; }
    [JsonPropertyName("downloads")] public List<string> Downloads { get; set; } = [];
    [JsonPropertyName("fileSize")] public long FileSize { get; set; }
}

// --------------------------------------------------------------- CurseForge

internal sealed class CurseForgeManifest
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("overrides")] public string? Overrides { get; set; }
    [JsonPropertyName("minecraft")] public CurseForgeMinecraft? Minecraft { get; set; }
    [JsonPropertyName("files")] public List<CurseForgeFile> Files { get; set; } = [];
}

internal sealed class CurseForgeMinecraft
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("modLoaders")] public List<CurseForgeLoader> ModLoaders { get; set; } = [];
}

internal sealed class CurseForgeLoader
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("primary")] public bool Primary { get; set; }
}

internal sealed class CurseForgeFile
{
    [JsonPropertyName("projectID")] public int ProjectId { get; set; }
    [JsonPropertyName("fileID")] public int FileId { get; set; }
    [JsonPropertyName("required")] public bool Required { get; set; } = true;
}
