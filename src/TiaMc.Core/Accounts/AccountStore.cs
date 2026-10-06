using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMc.Core.Accounts;

/// <summary>
/// Persistent list of launcher accounts (offline + Microsoft). The store always
/// contains at least one account so the launcher can start a game.
/// </summary>
public sealed class AccountStore
{
    [JsonPropertyName("accounts")] public List<MinecraftAccount> Accounts { get; set; } = [];
    [JsonPropertyName("selected")] public string? SelectedKey { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    [JsonIgnore] public string FilePath { get; private set; } = "";

    /// <summary>The account the launcher will use; never null.</summary>
    [JsonIgnore]
    public MinecraftAccount Selected
    {
        get
        {
            EnsureDefaults();
            var match = Accounts.FirstOrDefault(a =>
                string.Equals(a.Key, SelectedKey, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;

            var first = Accounts[0];
            SelectedKey = first.Key;
            return first;
        }
    }

    public static AccountStore Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var store = JsonSerializer.Deserialize<AccountStore>(File.ReadAllText(path), Options)
                            ?? new AccountStore();
                store.FilePath = path;
                store.EnsureDefaults();
                return store;
            }
        }
        catch (Exception)
        {
            // A corrupt account file must not stop the launcher: fall back to a
            // fresh store, the user can log in again.
        }

        var fresh = new AccountStore { FilePath = path };
        fresh.EnsureDefaults();
        return fresh;
    }

    private void EnsureDefaults()
    {
        if (Accounts.Count == 0)
        {
            Accounts.Add(MinecraftAccount.CreateOffline("Steve"));
        }

        if (string.IsNullOrWhiteSpace(SelectedKey) ||
            Accounts.All(a => !string.Equals(a.Key, SelectedKey, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedKey = Accounts[0].Key;
        }
    }

    public void Save()
    {
        try
        {
            if (string.IsNullOrEmpty(FilePath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing else to do; the in-memory store stays usable.
        }
    }

    /// <summary>Adds or updates an account (matched by kind + name) and selects it.</summary>
    public MinecraftAccount AddOrUpdate(MinecraftAccount account)
    {
        var index = Accounts.FindIndex(a =>
            a.Kind == account.Kind && string.Equals(a.Name, account.Name, StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
        {
            Accounts[index] = account;
        }
        else
        {
            Accounts.Add(account);
            index = Accounts.Count - 1;
        }

        SelectedKey = Accounts[index].Key;
        Save();
        return Accounts[index];
    }

    public bool Remove(MinecraftAccount account)
    {
        var removed = Accounts.RemoveAll(a =>
            a.Kind == account.Kind && string.Equals(a.Name, account.Name, StringComparison.OrdinalIgnoreCase)) > 0;

        if (removed)
        {
            EnsureDefaults();
            Save();
        }

        return removed;
    }

    public void Select(MinecraftAccount account)
    {
        SelectedKey = account.Key;
        Save();
    }
}
