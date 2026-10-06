using System.Windows;
using TiaMc.Core.Modpacks;

namespace TiaMc.App.Views;

/// <summary>
/// Import dialog for a modpack: it shows what was recognised and lets the user
/// decide whether the pack is deployed into a Minecraft instance right away.
/// </summary>
public partial class PackImportWindow : Window
{
    public PackImportWindow(string filePath, Modpack pack,
        IReadOnlyList<(string Id, string GameVersion, string Loader)> instances, string? suggestedInstance)
    {
        InitializeComponent();

        FilePath = filePath;
        Pack = pack;

        TitleText.Text = pack.IsServer ? "导入服务端整合包" : "导入客户端整合包";
        Title = TitleText.Text;
        PackNameText.Text = pack.Name;
        PackInfoText.Text =
            $"类型: {pack.KindText}    格式: {pack.Format}    游戏: {(pack.GameVersion.Length == 0 ? "-" : pack.GameVersion)}    " +
            $"装载器: {(pack.Loader.Length == 0 ? "-" : pack.LoaderText)}\n" +
            $"模组: {pack.ModCount} 个    清单文件: {pack.Files.Count} 个    文件: {System.IO.Path.GetFileName(filePath)}" +
            (pack.Summary.Length == 0 ? "" : "\n说明: " + pack.Summary);

        foreach (var instance in instances)
        {
            var text = $"{instance.Id}    [{(instance.Loader.Length == 0 ? "原版" : instance.Loader)}" +
                       (instance.GameVersion.Length == 0 ? "" : " " + instance.GameVersion) + "]";
            var item = new ComboItem(instance.Id, text, instance.GameVersion, instance.Loader);
            InstanceCombo.Items.Add(item);
            if (instance.Id == suggestedInstance) InstanceCombo.SelectedItem = item;
        }

        if (InstanceCombo.SelectedItem is null && InstanceCombo.Items.Count > 0)
        {
            InstanceCombo.SelectedIndex = 0;
        }

        // A server pack never goes into a client instance.
        if (pack.Kind == ModpackKind.Server)
        {
            DeployRadio.IsChecked = false;
            OnlyImportRadio.IsChecked = true;
            DeployRadio.IsEnabled = false;
            InstanceCombo.IsEnabled = false;
            InstanceHintText.Text = "服务端整合包会安装到 serverpacks 目录，并生成 eula / server.properties / 启动脚本。";
        }
        else if (instances.Count == 0)
        {
            DeployRadio.IsChecked = false;
            OnlyImportRadio.IsChecked = true;
            DeployRadio.IsEnabled = false;
            InstanceHintText.Text = "本机还没有任何 Minecraft 实例：先到「版本」页下载安装一个版本，再回来把整合包加入实例。";
        }
        else if (suggestedInstance is not null)
        {
            var match = instances.FirstOrDefault(i => i.Id == suggestedInstance);
            InstanceHintText.Text =
                $"已按整合包声明的 {pack.GameVersion} / {(pack.Loader.Length == 0 ? "原版" : pack.Loader)} 匹配到实例 " +
                $"{match.Id}，mods 与 config 会复制到 " +
                $"{System.IO.Path.Combine("versions", match.Id)}（版本隔离开启时即为该实例的游戏目录）。";
        }
        else
        {
            InstanceHintText.Text = "没有与整合包声明的版本完全匹配的实例，已选择第一个可用实例。";
        }
    }

    public string FilePath { get; }

    public Modpack Pack { get; }

    /// <summary>True when the pack should be copied into the selected instance.</summary>
    public bool DeployToInstance => DeployRadio.IsChecked == true;

    public string SelectedInstanceId => (InstanceCombo.SelectedItem as ComboItem)?.Id ?? "";

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private sealed record ComboItem(string Id, string Text, string GameVersion, string Loader)
    {
        public override string ToString() => Text;
    }
}
