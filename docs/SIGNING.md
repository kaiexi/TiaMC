# 代码签名与杀软误杀（TiaMC）

## 先说清楚：为什么"用微软官方签名"这件事做不到

代码签名证书是**发给一个经过身份验证的主体**的，签名里写的就是那个主体的名字与公钥：

* 想签成 `Microsoft Corporation`，必须有微软的**私钥**——这不是"能不能买"的问题，是根本没有；
* 拿别人的证书签名属于**伪造签名**，杀软和 SmartScreen 反而会更容易报毒，而且违法。

所以能做的只有三条路（按"最接近官方"排序）。这三条我都已经做成可直接跑的脚本：`tools/sign.ps1`。

---

## 路线 1：Azure Trusted Signing（微软自家签名服务，证书由微软签发）

这是**最接近"微软官方签名"的合法做法**：证书由微软 CA 签发，SmartScreen 信任度高。

1. Azure 门户开通 **Trusted Signing**，创建账户 + 证书配置文件（Certificate Profile）；
2. 完成**发布者身份验证**（个人：身份证明 + 地址；组织：营业执照等），微软人工审核；
3. 安装 `Microsoft Trusted Signing Client`；
4. 准备 `signing/metadata.json`（内容形如）：

```json
{
  "Endpoint": "https://neu.codesigning.azure.net",
  "CodeSigningAccountName": "<账户名>",
  "CertificateProfileName": "<证书配置名>"
}
```

5. 签名：

```powershell
.\tools\sign.ps1 -UseTrustedSigning -Metadata .\signing\metadata.json -Path .\dist
```

费用大约每月几美元；审核通过后就能长期使用。

---

## 路线 2：自己买 OV / EV 代码签名证书

* **OV**（约 100–300 美元/年）：SmartScreen 需要累积下载量才转正，但能显著降低杀软误报；
* **EV**（约 300–600 美元/年，通常需要 USB 硬件令牌或云 HSM）：**SmartScreen 立刻信任**，不积累；
* 从 SSL.com / Sectigo / DigiCert 等 CA 购买，需提供身份/公司材料。

签名（证书在证书存储里，EV 令牌插上即可）：

```powershell
.\tools\sign.ps1 -Thumbprint <证书指纹> -Path .\dist
```

或使用 PFX 文件：

```powershell
.\tools\sign.ps1 -Pfx .\cert.pfx -Password "***" -Path .\dist
```

---

## 路线 3：开源项目免费签名（SignPath Foundation）

SignPath Foundation 为**开源项目**提供免费代码签名（证书签发在 SignPath 侧，走他们的 CI）：

1. 项目满足条件（开源许可、公开仓库、有一定活跃度）；
2. 在 signpath.org 提交申请，通过后配置 GitHub Actions；
3. 每次 Release 由 SignPath 侧签名后再发布。

这条路不用花钱，但**签名发生在 SignPath 的构建流程里**，不是本地 `signtool`。

---

## 暂时没有证书时：减少误杀的实际做法

1. **上报误报**（免费且有效，尤其对 Microsoft Defender）：
   * Microsoft：<https://www.microsoft.com/en-us/wdsi/filesubmission>（选 software developer → 误报）
   * 卡巴斯基 / 360 / 火绒等：各家都有"误报申诉"入口，附上文件 SHA256 与下载页
2. **不要用自解压单文件**：TiaMC 现在发布的就是**文件夹版**（`PublishSingleFile=false`），比单文件自解压友好得多，这条已经做了；
3. **提供 SHA256**：Release 说明里已写明，用户校验后会更放心；
4. **行为上少踩雷**：启动器必然会下载 exe/jar（Java 运行时、游戏文件），这是启发式判定的主要来源。可在设置里关掉"自动补齐 Java"，改用已装 Java，减少"下载并执行"的组合动作；
5. **给用户一条加白说明**：把 `TiaMC.exe` 与游戏目录加入杀软排除列表（文档里已有排查段落）。

---

## 签名后的检查清单

* [ ] `signtool verify /pa /v TiaMC.exe` 显示 Successfully verified
* [ ] 重新打包 zip，并把新的 SHA256 写进 Release 说明
* [ ] 在干净机器（或虚拟机）上下载一次，确认 SmartScreen 不再弹"未知发布者"
