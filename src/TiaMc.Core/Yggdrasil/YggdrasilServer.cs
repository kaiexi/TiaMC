using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TiaMc.Core.Accounts;

namespace TiaMc.Core.Yggdrasil;

/// <summary>
/// A self contained Yggdrasil (Minecraft 外置登录) authentication server that is
/// compatible with authlib-injector. Implemented endpoints follow the
/// "Yggdrasil 服务端技术规范":
///
///   GET  /                                                    API metadata
///   POST /authserver/authenticate                             login
///   POST /authserver/refresh                                  refresh / select profile
///   POST /authserver/validate                                 204
///   POST /authserver/invalidate                               204
///   POST /authserver/signout                                  204
///   POST /sessionserver/session/minecraft/join                204
///   GET  /sessionserver/session/minecraft/hasJoined           profile (server side check)
///   GET  /sessionserver/session/minecraft/profile/{uuid}      profile (+ signature)
///   POST /api/profiles/minecraft                              name lookup
///   PUT  /api/user/profile/{uuid}/{skin|cape}                  texture upload
///   GET  /textures/{hash}                                     texture bytes (image/png)
///   GET  /skins/MinecraftSkins/{name}.png                     legacy skin API
///   POST /minecraftservices/player/certificates               204 (no chat signing key)
///
/// Profile uuids are derived from the player name with the same OfflinePlayer rule
/// the vanilla launcher uses, so worlds stay compatible when a server switches
/// between offline mode and this authentication server.
/// </summary>
public sealed class YggdrasilServer : IDisposable
{
    private readonly object _gate = new();
    private readonly string _root;
    private readonly string _storePath;
    private readonly string _textureDir;
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private RSA? _rsa;

    public YggdrasilServer(string root)
    {
        _root = root;
        _storePath = Path.Combine(root, "yggdrasil.json");
        _textureDir = Path.Combine(root, "textures");
        Directory.CreateDirectory(_textureDir);
        Store = LoadStore();
        EnsureKeys();
    }

    /// <summary>Raised for every line of server output (the terminal UI shows these).</summary>
    public event Action<string>? Log;

    public YggdrasilStore Store { get; private set; }

    public bool IsRunning => _listener is not null;

    public int Port => Store.Settings.Port;

    public string LanAddress
    {
        get
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                var ip = host.AddressList.FirstOrDefault(a =>
                    a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
                return ip?.ToString() ?? "127.0.0.1";
            }
            catch (Exception)
            {
                return "127.0.0.1";
            }
        }
    }

    /// <summary>API root that launchers and authlib-injector should use.</summary>
    public string ApiRoot => $"http://{(Store.Settings.Bind == "any" ? LanAddress : "127.0.0.1")}:{Port}/";

    public string StorePath => _storePath;

    // ------------------------------------------------------------ lifecycle

    public bool Start(int? port = null)
    {
        lock (_gate)
        {
            if (IsRunning)
            {
                Write("服务端已在运行: " + ApiRoot);
                return true;
            }

            if (port is > 0 and < 65536) Store.Settings.Port = port.Value;

            try
            {
                var address = Store.Settings.Bind == "any" ? IPAddress.Any : IPAddress.Loopback;
                _listener = new TcpListener(address, Store.Settings.Port);
                _listener.Start();
            }
            catch (Exception e)
            {
                Write($"启动失败({Store.Settings.Port}): {e.Message}");
                _listener = null;
                return false;
            }

            _cts = new CancellationTokenSource();
            var listener = _listener;
            _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
            SaveStore();

            Write($"认证服务端已启动: {ApiRoot}");
            Write($"元数据地址: {ApiRoot}  （authlib-injector 用它自动发现 API）");
            Write($"用角色名登录: user add <角色名> <密码>，然后在启动器里选择该账户");
            Write($"启动器 JVM 参数: -javaagent:authlib-injector.jar={ApiRoot}");
            if (Store.Settings.Bind == "any") Write($"局域网地址: http://{LanAddress}:{Port}/");
            return true;
        }
    }

    public void Stop()
    {
        if (Environment.GetEnvironmentVariable("TIAMC_YGG_TRACE") == "1")
        {
            try
            {
                File.AppendAllText(Path.Combine(_root, "stop-trace.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] Stop() 调用栈:\n{Environment.StackTrace}\n\n");
            }
            catch (Exception)
            {
                // diagnostics only
            }
        }

        lock (_gate)
        {
            try
            {
                _cts?.Cancel();
                _listener?.Stop();
            }
            catch (Exception)
            {
                // shutting down anyway
            }

            _listener = null;
            _cts = null;
        }

        SaveStore();
        Write("认证服务端已停止");
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    client.NoDelay = true;
                    using (client)
                    await using (var stream = client.GetStream())
                    {
                        var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
                        var request = await MiniHttp.ReadAsync(stream, remote, token).ConfigureAwait(false);
                        if (request is null) return;

                        var response = new MiniHttpResponse(stream);
                        await HandleAsync(request, response).ConfigureAwait(false);
                        Write($"{request.Method,-4} {request.Path} -> {response.Status}");
                    }
                }
                catch (Exception e)
                {
                    Write("请求处理失败: " + e.Message);
                }
            }, token);
        }
    }

    public void Dispose()
    {
        Stop();
        _rsa?.Dispose();
    }

    private void Write(string message) => Log?.Invoke(message);

    // ------------------------------------------------------------- store io

    private YggdrasilStore LoadStore()
    {
        try
        {
            if (File.Exists(_storePath))
            {
                var store = JsonSerializer.Deserialize<YggdrasilStore>(File.ReadAllText(_storePath), JsonOptions);
                if (store is not null) return store;
            }
        }
        catch (Exception e)
        {
            Write("读取 yggdrasil.json 失败，使用空配置: " + e.Message);
        }

        return new YggdrasilStore();
    }

    public void SaveStore()
    {
        try
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(_storePath, JsonSerializer.Serialize(Store, JsonOptions));
        }
        catch (Exception e)
        {
            Write("保存 yggdrasil.json 失败: " + e.Message);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private void EnsureKeys()
    {
        if (!string.IsNullOrWhiteSpace(Store.PrivateKeyPem))
        {
            try
            {
                _rsa = RSA.Create();
                _rsa.ImportFromPem(Store.PrivateKeyPem);
                return;
            }
            catch (Exception e)
            {
                Write("导入签名私钥失败，将重新生成: " + e.Message);
            }
        }

        _rsa?.Dispose();
        _rsa = RSA.Create(2048);
        Store.PrivateKeyPem = _rsa.ExportRSAPrivateKeyPem();
        Store.PublicKeyPem = _rsa.ExportSubjectPublicKeyInfoPem();
        SaveStore();
        Write("已生成签名密钥对（SHA1withRSA，用于角色属性签名）");
    }

    // --------------------------------------------------------------- users

    /// <summary>Adds a user plus its profile; the profile name doubles as the login name.</summary>
    public (bool Ok, string Message) AddUser(string name, string password, string? email = null)
    {
        name = name.Trim();
        if (name.Length == 0) return (false, "角色名不能为空");
        if (Store.Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return (false, $"角色 {name} 已存在");
        }

        var user = new YggdrasilUser
        {
            Id = Guid.NewGuid().ToString("N"),
            Email = string.IsNullOrWhiteSpace(email) ? $"{name.ToLowerInvariant()}@tiamc.local" : email!.Trim(),
            Salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()
        };
        user.PasswordHash = HashPassword(password, user.Salt);

        var profile = new YggdrasilProfile
        {
            // Same rule as the vanilla offline launcher, so existing worlds keep working.
            Uuid = MinecraftAccount.OfflineUuid(name).ToString("N"),
            Name = name,
            UserId = user.Id
        };

        user.Profiles.Add(profile.Uuid);
        Store.Users.Add(user);
        Store.Profiles.Add(profile);
        SaveStore();

        return (true, $"已添加 {name}（uuid {profile.Uuid}）");
    }

    public (bool Ok, string Message) RemoveUser(string name)
    {
        var profile = FindProfileByName(name);
        if (profile is null) return (false, $"没有找到角色 {name}");

        Store.Profiles.Remove(profile);
        var user = Store.Users.FirstOrDefault(u => u.Id == profile.UserId);
        if (user is not null)
        {
            Store.Users.Remove(user);
            Store.Tokens.RemoveAll(t => t.UserId == user.Id);
        }

        SaveStore();
        return (true, $"已删除 {name}");
    }

    public (bool Ok, string Message) SetPassword(string name, string password)
    {
        var profile = FindProfileByName(name);
        if (profile is null) return (false, $"没有找到角色 {name}");

        var user = Store.Users.FirstOrDefault(u => u.Id == profile.UserId);
        if (user is null) return (false, "用户记录缺失");

        user.Salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        user.PasswordHash = HashPassword(password, user.Salt);
        Store.Tokens.RemoveAll(t => t.UserId == user.Id);
        SaveStore();
        return (true, $"{name} 的密码已更新（旧令牌全部失效）");
    }

    /// <summary>Copies a PNG file into the texture store and attaches it to a profile.</summary>
    public (bool Ok, string Message) SetSkin(string name, string pngPath, string model = "default", bool cape = false)
    {
        var profile = FindProfileByName(name);
        if (profile is null) return (false, $"没有找到角色 {name}");
        if (!File.Exists(pngPath)) return (false, $"文件不存在: {pngPath}");

        try
        {
            var bytes = File.ReadAllBytes(pngPath);
            if (!LooksLikePng(bytes)) return (false, "不是合法的 PNG 文件");

            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            File.WriteAllBytes(Path.Combine(_textureDir, hash + ".png"), bytes);

            if (cape)
            {
                profile.CapeHash = hash;
            }
            else
            {
                profile.SkinHash = hash;
                profile.Model = model.Equals("slim", StringComparison.OrdinalIgnoreCase) ? "slim" : "default";
            }

            SaveStore();
            return (true, $"已设置{(cape ? "披风" : "皮肤")}（{profile.Model}）hash={hash}");
        }
        catch (Exception e)
        {
            return (false, "设置失败: " + e.Message);
        }
    }

    /// <summary>Generates a simple placeholder skin so the server is usable out of the box.</summary>
    public (bool Ok, string Message) SetPlaceholderSkin(string name, string hexColor = "#3B7FB5")
    {
        var profile = FindProfileByName(name);
        if (profile is null) return (false, $"没有找到角色 {name}");

        try
        {
            var (r, g, b) = ParseColor(hexColor);
            var bytes = MiniHttp.BuildSolidSkin(r, g, b);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            File.WriteAllBytes(Path.Combine(_textureDir, hash + ".png"), bytes);
            profile.SkinHash = hash;
            profile.CapeHash = null;
            SaveStore();
            return (true, $"已生成占位皮肤 {hexColor}，hash={hash}");
        }
        catch (Exception e)
        {
            return (false, "生成失败: " + e.Message);
        }
    }

    private static (byte R, byte G, byte B) ParseColor(string hex)
    {
        var clean = hex.Trim().TrimStart('#');
        if (clean.Length != 6)
        {
            clean = "3B7FB5";
        }

        return (Convert.ToByte(clean[..2], 16), Convert.ToByte(clean[2..4], 16), Convert.ToByte(clean[4..], 16));
    }

    private static bool LooksLikePng(byte[] bytes) =>
        bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;

    public YggdrasilProfile? FindProfileByName(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : Store.Profiles.FirstOrDefault(p => p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    public YggdrasilProfile? FindProfileByUuid(string? uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid)) return null;
        var clean = uuid.Replace("-", "").ToLowerInvariant();
        return Store.Profiles.FirstOrDefault(p => p.Uuid.Equals(clean, StringComparison.OrdinalIgnoreCase));
    }

    private static string HashPassword(string password, string salt)
    {
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), Convert.FromHexString(salt), 120_000, HashAlgorithmName.SHA256, 32);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private YggdrasilUser? Authenticate(string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username) || password is null) return null;
        var wanted = username.Trim();

        var user = Store.Users.FirstOrDefault(u => u.Email.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        user ??= Store.Users.FirstOrDefault(u => u.Profiles.Any(pid =>
            Store.Profiles.Any(p => p.Uuid == pid && p.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))));

        if (user is null) return null;

        var hash = HashPassword(password, user.Salt);
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(hash), Convert.FromHexString(user.PasswordHash))
                ? user
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // -------------------------------------------------------------- routing

    private async Task HandleAsync(MiniHttpRequest request, MiniHttpResponse response)
    {
        try
        {
            var path = request.Path;
            var method = request.Method;

            if (method == "GET" && path == "/")
            {
                await HandleMetadataAsync(response).ConfigureAwait(false);
            }
            else if (method == "POST" && path == "/authserver/authenticate")
            {
                await HandleAuthenticateAsync(request, response).ConfigureAwait(false);
            }
            else if (method == "POST" && path == "/authserver/refresh")
            {
                await HandleRefreshAsync(request, response).ConfigureAwait(false);
            }
            else if (method == "POST" && path == "/authserver/validate")
            {
                await HandleValidateAsync(request, response).ConfigureAwait(false);
            }
            else if (method == "POST" && path == "/authserver/invalidate")
            {
                await HandleInvalidateAsync(request, response).ConfigureAwait(false);
            }
            else if (method == "POST" && path == "/authserver/signout")
            {
                await HandleSignoutAsync(request, response).ConfigureAwait(false);
            }
            else if (method == "POST" && path == "/sessionserver/session/minecraft/join")
            {
                await HandleJoinAsync(request, response).ConfigureAwait(false);
            }
            else if (method == "GET" && path == "/sessionserver/session/minecraft/hasJoined")
            {
                await HandleHasJoinedAsync(request, response).ConfigureAwait(false);
            }
            else if (method == "POST" && path == "/api/profiles/minecraft")
            {
                await HandleProfileLookupAsync(request, response).ConfigureAwait(false);
            }
            else if (method == "POST" && path == "/minecraftservices/player/certificates")
            {
                await response.EmptyAsync(204).ConfigureAwait(false);
            }
            else
            {
                await HandleDynamicPathAsync(method, path, request, response).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            Write("处理请求出错: " + e.Message);
            await WriteErrorAsync(response, 500, "InternalServerError", e.Message).ConfigureAwait(false);
        }
    }

    /// <summary>Paths that carry parameters: profile lookup, texture upload and serving.</summary>
    private async Task HandleDynamicPathAsync(string method, string path, MiniHttpRequest request,
        MiniHttpResponse response)
    {
        const string profilePrefix = "/sessionserver/session/minecraft/profile/";
        if (method == "GET" && path.StartsWith(profilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var uuid = path[profilePrefix.Length..];
            var unsigned = !string.Equals(request.Query.GetValueOrDefault("unsigned"), "false",
                StringComparison.OrdinalIgnoreCase);
            await HandleProfileAsync(uuid, unsigned, response).ConfigureAwait(false);
            return;
        }

        const string texturePrefix = "/textures/";
        if (method == "GET" && path.StartsWith(texturePrefix, StringComparison.OrdinalIgnoreCase))
        {
            await HandleTextureAsync(path[texturePrefix.Length..].Replace(".png", ""), response).ConfigureAwait(false);
            return;
        }

        const string legacySkinPrefix = "/skins/MinecraftSkins/";
        if (method == "GET" && path.StartsWith(legacySkinPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(path[legacySkinPrefix.Length..]);
            var profile = FindProfileByName(name);
            if (profile?.SkinHash is { Length: > 0 } hash)
            {
                await HandleTextureAsync(hash, response).ConfigureAwait(false);
            }
            else
            {
                await response.EmptyAsync(204).ConfigureAwait(false);
            }

            return;
        }

        const string userProfilePrefix = "/api/user/profile/";
        if (method == "PUT" && path.StartsWith(userProfilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var rest = path[userProfilePrefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (rest.Length == 2)
            {
                await HandleTextureUploadAsync(rest[0], rest[1], request, response).ConfigureAwait(false);
                return;
            }
        }

        await response.EmptyAsync(404).ConfigureAwait(false);
    }

    // ----------------------------------------------------------- endpoints

    private async Task HandleMetadataAsync(MiniHttpResponse response)
    {
        var host = Store.Settings.Bind == "any" ? LanAddress : "127.0.0.1";
        var metadata = new Dictionary<string, object>
        {
            ["meta"] = new Dictionary<string, object>
            {
                ["serverName"] = Store.Settings.ServerName,
                ["implementationName"] = "TiaMC Yggdrasil Server",
                ["implementationVersion"] = Utils.AppInfo.Version,
                ["feature.non_email_login"] = Store.Settings.NonEmailLogin,
                ["feature.legacy_skin_api"] = true,
                ["links"] = new Dictionary<string, string> { ["homepage"] = ApiRoot }
            },
            ["skinDomains"] = Store.Settings.SkinDomains.Count > 0
                ? Store.Settings.SkinDomains
                : ["127.0.0.1", "localhost", host],
            ["signaturePublickey"] = Store.PublicKeyPem ?? ""
        };

        await response.JsonAsync(200, JsonSerializer.Serialize(metadata, JsonOptions)).ConfigureAwait(false);
    }

    private async Task HandleAuthenticateAsync(MiniHttpRequest request, MiniHttpResponse response)
    {
        var body = Deserialize<YggAuthenticateRequest>(request.BodyText);
        if (body is null)
        {
            await WriteErrorAsync(response, 400, "IllegalArgumentException", "请求体不是合法 JSON").ConfigureAwait(false);
            return;
        }

        var agent = body.Agent?.Name ?? "Minecraft";
        if (!agent.Equals("Minecraft", StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(response, 400, "IllegalArgumentException", "不支持的 agent: " + agent)
                .ConfigureAwait(false);
            return;
        }

        var user = Authenticate(body.Username, body.Password);
        if (user is null)
        {
            Write($"登录失败: {body.Username}");
            await WriteErrorAsync(response, 403, "ForbiddenOperationException",
                "Invalid credentials. Invalid username or password.").ConfigureAwait(false);
            return;
        }

        var token = IssueToken(user, body.ClientToken);
        var profiles = user.Profiles
            .Select(FindProfileByUuid)
            .Where(p => p is not null)
            .Select(p => ToProfileResponse(p!, unsigned: true))
            .ToList();

        // Binding rule from the spec: a single profile is selected automatically,
        // and logging in with a profile name binds that profile.
        string? selectedUuid = profiles.Count == 1
            ? profiles[0].Id
            : Store.Settings.NonEmailLogin && FindProfileByName(body.Username) is { } byName &&
              user.Profiles.Contains(byName.Uuid)
                ? byName.Uuid
                : null;

        if (selectedUuid is not null)
        {
            token.ProfileUuid = selectedUuid;
            SaveStore();
        }

        Write($"登录成功: {FindProfileByUuid(selectedUuid)?.Name ?? user.Email}" +
              (selectedUuid is null ? "（需要选择角色）" : ""));

        await response.JsonAsync(200, JsonSerializer.Serialize(new YggAuthenticateResponse
        {
            AccessToken = token.AccessToken,
            ClientToken = token.ClientToken,
            AvailableProfiles = profiles,
            SelectedProfile = selectedUuid is null ? null : profiles.FirstOrDefault(p => p.Id == selectedUuid),
            User = body.RequestUser ? ToUserResponse(user) : null
        }, JsonOptions)).ConfigureAwait(false);
    }

    private async Task HandleRefreshAsync(MiniHttpRequest request, MiniHttpResponse response)
    {
        var body = Deserialize<YggRefreshRequest>(request.BodyText);
        var token = Store.Tokens.FirstOrDefault(t => t.AccessToken == body?.AccessToken);
        if (body?.AccessToken is null || token is null || !token.Valid)
        {
            await WriteErrorAsync(response, 403, "ForbiddenOperationException", "Invalid token.").ConfigureAwait(false);
            return;
        }

        // Refreshing is still allowed while the token is only temporarily invalid.
        var now = DateTime.UtcNow;
        if (now > token.ExpiresUtc && now > token.ExpiresUtc.AddDays(2))
        {
            token.Valid = false;
            SaveStore();
            await WriteErrorAsync(response, 403, "ForbiddenOperationException", "Invalid token.").ConfigureAwait(false);
            return;
        }

        if (body.ClientToken is { Length: > 0 } client && client != token.ClientToken)
        {
            await WriteErrorAsync(response, 403, "ForbiddenOperationException", "Invalid token.").ConfigureAwait(false);
            return;
        }

        if (body.SelectedProfile?.Id is { Length: > 0 } wanted)
        {
            if (token.ProfileUuid is { Length: > 0 })
            {
                await WriteErrorAsync(response, 400, "IllegalArgumentException",
                    "Access token already has a profile assigned.").ConfigureAwait(false);
                return;
            }

            var profile = FindProfileByUuid(wanted);
            var owner = Store.Users.FirstOrDefault(u => u.Id == token.UserId);
            if (profile is null || owner is null || !owner.Profiles.Contains(profile.Uuid))
            {
                await WriteErrorAsync(response, 403, "ForbiddenOperationException", "Invalid token.")
                    .ConfigureAwait(false);
                return;
            }

            token.ProfileUuid = profile.Uuid;
        }

        token.Valid = false; // the spec revokes the old token on refresh
        var user = Store.Users.FirstOrDefault(u => u.Id == token.UserId);
        if (user is null)
        {
            await WriteErrorAsync(response, 403, "ForbiddenOperationException", "Invalid token.").ConfigureAwait(false);
            return;
        }

        var fresh = IssueToken(user, token.ClientToken);
        fresh.ProfileUuid = token.ProfileUuid;
        SaveStore();

        await response.JsonAsync(200, JsonSerializer.Serialize(new YggRefreshResponse
        {
            AccessToken = fresh.AccessToken,
            ClientToken = fresh.ClientToken,
            SelectedProfile = fresh.ProfileUuid is null
                ? null
                : ToProfileResponse(FindProfileByUuid(fresh.ProfileUuid)!, unsigned: true),
            User = body.RequestUser ? ToUserResponse(user) : null
        }, JsonOptions)).ConfigureAwait(false);
    }

    private async Task HandleValidateAsync(MiniHttpRequest request, MiniHttpResponse response)
    {
        var body = Deserialize<YggTokenRequest>(request.BodyText);
        if (FindValidToken(body?.AccessToken, body?.ClientToken) is null)
        {
            await WriteErrorAsync(response, 403, "ForbiddenOperationException", "Invalid token.").ConfigureAwait(false);
            return;
        }

        await response.EmptyAsync(204).ConfigureAwait(false);
    }

    private async Task HandleInvalidateAsync(MiniHttpRequest request, MiniHttpResponse response)
    {
        var body = Deserialize<YggTokenRequest>(request.BodyText);
        var token = Store.Tokens.FirstOrDefault(t => t.AccessToken == body?.AccessToken);
        if (token is not null)
        {
            token.Valid = false;
            SaveStore();
        }

        await response.EmptyAsync(204).ConfigureAwait(false); // 204 either way, per spec
    }

    private async Task HandleSignoutAsync(MiniHttpRequest request, MiniHttpResponse response)
    {
        var body = Deserialize<YggSignoutRequest>(request.BodyText);
        var user = Authenticate(body?.Username, body?.Password);
        if (user is not null)
        {
            foreach (var token in Store.Tokens.Where(t => t.UserId == user.Id)) token.Valid = false;
            SaveStore();
        }

        await response.EmptyAsync(204).ConfigureAwait(false);
    }

    private async Task HandleJoinAsync(MiniHttpRequest request, MiniHttpResponse response)
    {
        var body = Deserialize<YggJoinRequest>(request.BodyText);
        var token = FindValidToken(body?.AccessToken, null);
        if (token is null || body?.ServerId is not { Length: > 0 } serverId)
        {
            await WriteErrorAsync(response, 403, "ForbiddenOperationException", "Invalid token.").ConfigureAwait(false);
            return;
        }

        if (body.SelectedProfile is { Length: > 0 } uuid &&
            !string.Equals(token.ProfileUuid, uuid.Replace("-", ""), StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(response, 403, "ForbiddenOperationException", "Invalid token.").ConfigureAwait(false);
            return;
        }

        lock (_gate)
        {
            _sessions[serverId] = new Session
            {
                ProfileUuid = token.ProfileUuid ?? "",
                Ip = request.RemoteAddress,
                ExpiresUtc = DateTime.UtcNow.AddSeconds(30)
            };
        }

        token.Uses++;
        SaveStore();
        await response.EmptyAsync(204).ConfigureAwait(false);
    }

    private async Task HandleHasJoinedAsync(MiniHttpRequest request, MiniHttpResponse response)
    {
        var username = request.Query.GetValueOrDefault("username");
        var serverId = request.Query.GetValueOrDefault("serverId");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(serverId))
        {
            await response.EmptyAsync(204).ConfigureAwait(false);
            return;
        }

        Session? session;
        lock (_gate)
        {
            _sessions.TryGetValue(serverId, out session);
            if (session is not null && session.ExpiresUtc < DateTime.UtcNow)
            {
                _sessions.Remove(serverId);
                session = null;
            }
        }

        var profile = FindProfileByName(username);
        if (session is null || profile is null ||
            !string.Equals(profile.Uuid, session.ProfileUuid, StringComparison.OrdinalIgnoreCase))
        {
            Write($"hasJoined 拒绝: {username}");
            await response.EmptyAsync(204).ConfigureAwait(false);
            return;
        }

        Write($"hasJoined 通过: {profile.Name}");
        // The server side check receives signed properties.
        await response.JsonAsync(200,
            JsonSerializer.Serialize(ToProfileResponse(profile, unsigned: false), JsonOptions)).ConfigureAwait(false);
    }

    private async Task HandleProfileAsync(string uuid, bool unsigned, MiniHttpResponse response)
    {
        var profile = FindProfileByUuid(uuid);
        if (profile is null)
        {
            await response.EmptyAsync(204).ConfigureAwait(false);
            return;
        }

        await response.JsonAsync(200,
            JsonSerializer.Serialize(ToProfileResponse(profile, unsigned), JsonOptions)).ConfigureAwait(false);
    }

    private async Task HandleProfileLookupAsync(MiniHttpRequest request, MiniHttpResponse response)
    {
        List<string>? names;
        try
        {
            names = JsonSerializer.Deserialize<List<string>>(request.BodyText);
        }
        catch (JsonException)
        {
            names = null;
        }

        if (names is null)
        {
            await WriteErrorAsync(response, 400, "IllegalArgumentException", "请求体应为角色名数组").ConfigureAwait(false);
            return;
        }

        // Guard against abuse (the spec asks for a limit of at least 2).
        var result = names.Take(64)
            .Select(FindProfileByName)
            .Where(p => p is not null)
            .Select(p => new YggProfileResponse { Id = p!.Uuid, Name = p.Name })
            .ToList();

        await response.JsonAsync(200, JsonSerializer.Serialize(result, JsonOptions)).ConfigureAwait(false);
    }

    private async Task HandleTextureAsync(string hash, MiniHttpResponse response)
    {
        if (hash.Length == 0 || hash.Any(c => !char.IsAsciiHexDigit(c)))
        {
            await response.EmptyAsync(404).ConfigureAwait(false);
            return;
        }

        var file = Path.Combine(_textureDir, hash + ".png");
        if (!File.Exists(file))
        {
            await response.EmptyAsync(404).ConfigureAwait(false);
            return;
        }

        try
        {
            // Content type must be image/png, otherwise clients may sniff it.
            await response.BytesAsync(200, "image/png", File.ReadAllBytes(file)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Write("读取材质失败: " + e.Message);
            await response.EmptyAsync(500).ConfigureAwait(false);
        }
    }

    private async Task HandleTextureUploadAsync(string uuid, string type, MiniHttpRequest request,
        MiniHttpResponse response)
    {
        var profile = FindProfileByUuid(uuid);
        if (profile is null)
        {
            await response.EmptyAsync(204).ConfigureAwait(false);
            return;
        }

        var isCape = type.Equals("cape", StringComparison.OrdinalIgnoreCase);
        if (!isCape && !type.Equals("skin", StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(response, 400, "IllegalArgumentException", "未知材质类型").ConfigureAwait(false);
            return;
        }

        // A multipart body is accepted as is: only the PNG payload is kept.
        var bytes = ExtractPng(request.Body);
        if (bytes is null)
        {
            await WriteErrorAsync(response, 400, "IllegalArgumentException", "请求体里没有 PNG 数据")
                .ConfigureAwait(false);
            return;
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        File.WriteAllBytes(Path.Combine(_textureDir, hash + ".png"), bytes);

        if (isCape)
        {
            profile.CapeHash = hash;
        }
        else
        {
            profile.SkinHash = hash;
            var model = request.Query.GetValueOrDefault("model") ?? "default";
            profile.Model = model.Equals("slim", StringComparison.OrdinalIgnoreCase) ? "slim" : "default";
        }

        SaveStore();
        Write($"{(isCape ? "披风" : "皮肤")}已更新: {profile.Name} -> {hash}");
        await response.EmptyAsync(204).ConfigureAwait(false);
    }

    /// <summary>Finds the PNG payload inside a raw or multipart request body.</summary>
    private static byte[]? ExtractPng(byte[] body)
    {
        if (LooksLikePng(body)) return body;

        for (var i = 0; i + 8 < body.Length; i++)
        {
            if (body[i] != 0x89 || body[i + 1] != 0x50 || body[i + 2] != 0x4E || body[i + 3] != 0x47) continue;

            // Trim trailing multipart boundary junk.
            var end = body.Length;
            for (var j = i; j + 4 < body.Length; j++)
            {
                if (body[j] == 0x49 && body[j + 1] == 0x45 && body[j + 2] == 0x4E && body[j + 3] == 0x44)
                {
                    end = j + 8; // IEND + crc
                    break;
                }
            }

            return body[i..Math.Min(end, body.Length)];
        }

        return null;
    }

    // ------------------------------------------------------------ responses

    private sealed class Session
    {
        public string ProfileUuid { get; init; } = "";
        public string Ip { get; init; } = "";
        public DateTime ExpiresUtc { get; init; }
    }

    private YggdrasilToken? FindValidToken(string? accessToken, string? clientToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return null;
        var token = Store.Tokens.FirstOrDefault(t => t.AccessToken == accessToken);
        if (token is null || !token.Valid || DateTime.UtcNow > token.ExpiresUtc) return null;
        if (clientToken is { Length: > 0 } && clientToken != token.ClientToken) return null;
        return token;
    }

    private YggdrasilToken IssueToken(YggdrasilUser user, string? clientToken)
    {
        var token = new YggdrasilToken
        {
            AccessToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            ClientToken = string.IsNullOrWhiteSpace(clientToken) ? Guid.NewGuid().ToString("N") : clientToken!,
            UserId = user.Id,
            IssuedUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.AddDays(Math.Max(1, Store.Settings.TokenDays))
        };

        Store.Tokens.Add(token);
        Store.Tokens.RemoveAll(t => t.ExpiresUtc < DateTime.UtcNow.AddDays(-3));
        return token;
    }

    private YggProfileResponse ToProfileResponse(YggdrasilProfile profile, bool unsigned)
    {
        var properties = new List<YggProperty>();

        if (BuildTexturesPayload(profile) is { } textures)
        {
            var value = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(textures, JsonOptions)));
            properties.Add(new YggProperty
            {
                Name = "textures",
                Value = value,
                Signature = unsigned ? null : Sign(value)
            });
        }

        const string uploadable = "skin,cape";
        properties.Add(new YggProperty
        {
            Name = "uploadableTextures",
            Value = uploadable,
            Signature = unsigned ? null : Sign(uploadable)
        });

        return new YggProfileResponse { Id = profile.Uuid, Name = profile.Name, Properties = properties };
    }

    private YggTexturesPayload? BuildTexturesPayload(YggdrasilProfile profile)
    {
        var map = new Dictionary<string, YggTexture>();

        if (profile.SkinHash is { Length: > 0 } skin)
        {
            map["SKIN"] = new YggTexture
            {
                Url = $"{ApiRoot}textures/{skin}",
                Metadata = profile.Model.Equals("slim", StringComparison.OrdinalIgnoreCase)
                    ? new Dictionary<string, string> { ["model"] = "slim" }
                    : null
            };
        }

        if (profile.CapeHash is { Length: > 0 } cape)
        {
            map["CAPE"] = new YggTexture { Url = $"{ApiRoot}textures/{cape}" };
        }

        if (map.Count == 0) return null;

        return new YggTexturesPayload
        {
            ProfileId = profile.Uuid,
            ProfileName = profile.Name,
            Textures = map
        };
    }

    private static YggUserResponse ToUserResponse(YggdrasilUser user) => new()
    {
        Id = user.Id,
        Properties = [new YggProperty { Name = "preferredLanguage", Value = "zh_CN" }]
    };

    private string Sign(string value)
    {
        if (_rsa is null) return "";
        var signature = _rsa.SignData(Encoding.UTF8.GetBytes(value), HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
        return Convert.ToBase64String(signature);
    }

    private static T? Deserialize<T>(string text)
    {
        try
        {
            return string.IsNullOrWhiteSpace(text)
                ? default
                : JsonSerializer.Deserialize<T>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static async Task WriteErrorAsync(MiniHttpResponse response, int status, string error, string message)
    {
        var payload = new YggError { Error = error, ErrorMessage = message };
        await response.JsonAsync(status, JsonSerializer.Serialize(payload, JsonOptions)).ConfigureAwait(false);
    }
}
