**简体中文**

<div align="center">

<img src="Plain Craft Launcher 2/Images/icon.ico" alt="Logo" width="80" height="80">

# PCL-CE PRO

**Plain Craft Launcher Community Edition 的第三方修改版**

![.NET](https://img.shields.io/badge/.NET-10-512BD4?style=for-the-badge&logo=dotnet)
![Platform](https://img.shields.io/badge/Windows-10%2F11-0078D4?style=for-the-badge&logo=windows)

[上游 PCL](https://github.com/Meloong-Git/PCL) |
[PCL Community Edition](https://github.com/PCL-Community/PCL-CE)

</div>

---

## ⚠️ 重要声明

本项目是**第三方基于 PCL 与 PCL CE 独立进行的二次创作**，**与龙腾猫跃（PCL 作者）及 PCL Community 团队无隶属关系**，不代表官方立场，也不是官方后续版本。

- 本项目的名称、界面与行为由本项目维护者负责，与上游无关
- 使用中遇到的问题请反馈到本仓库，**不要**提交到 PCL 或 PCL CE 的官方仓库
- 本项目遵守 [PCL 存储库合理使用指南](https://github.com/Meloong-Git/PCL)，在关于页首位保留龙腾猫跃的署名与赞助链接

## ✨ 本版相对上游的改动

### 界面与体验

| 功能 | 说明 |
|---|---|
| 磨砂玻璃背景 | 对背景壁纸应用重模糊，提供「轻 / 标准 / 强」三档强度；附带细密噪点消除色带；无壁纸时使用内置渐变兜底 |
| 界面动效优化 | 磨砂层的模糊结果使用位图缓存，避免每个动画帧重算模糊；视频壁纸播放时自动关闭缓存以防画面冻结 |
| 首次使用向导 | 首次启动提供四步引导：欢迎页 → 主题（模式/配色/透明度/磨砂/背景动效）→ 壁纸 → 语言，带淡入上移动画 |
| 设置 · 详情页 | 原「软件更新」页改为「详情」，展示版本、构建分支、.NET 与系统信息、目录、启动次数 |
| 清除数据 | 详情页内可勾选清理缓存 / 日志 / 本机设置，二次确认后执行；设置重置通过标记文件保证强杀或崩溃后依然生效 |

### 稳定性与资源

| 功能 | 说明 |
|---|---|
| 内存优化 | 启动完成后自动裁剪工作集，降低常驻内存 |
| 离线登录 | 支持离线账户直接进入游戏 |
| 禁用自动更新 | 不检查也不提示更新，避免被上游版本覆盖 |

### 模组管理

| 功能 | 说明 |
|---|---|
| 模组冲突检查 | 在实例的模组页点击「检查模组」，检测前置缺失、重复同类、模组主动声明的不兼容；同一缺失前置会合并展示（「缺少 X，被 A、B 依赖」） |
| 下载前置检测 | 下载模组时若含必需前置，弹窗询问是否一并下载 |

## 🛠️ 构建

环境要求：

- Windows 10 / 11
- [.NET SDK 10](https://dotnet.microsoft.com/download)

```bash
# 还原并编译
dotnet build "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Release

# 发布为单文件
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" \
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true \
  -o ./publish
```

产物位于 `./publish/PCL-CE PRO.exe`。

## 📁 项目结构

```
├─ Plain Craft Launcher 2/     主程序（WPF 界面层）
├─ PCL.Launcher.Core/          启动器核心（游戏启动、下载、本地资源）
├─ PCL.Core/                   基础设施（配置、网络、本地化、日志）
├─ PCL.Core.Wpf/               WPF 相关基础组件与语言包
├─ PCL.Core.SourceGenerators/  配置项与本地化的源生成器
└─ PCL.Core.Test/              单元测试
```

## 🔒 许可证

本项目按目录采用不同许可证，与上游保持一致：

| 目录 | 许可证 |
|---|---|
| `Plain Craft Launcher 2/`、`PCL.Launcher.Core/` 等继承自 PCL 的部分 | [PCL 分发有限许可](PCL.Launcher.Core/LICENCE) |
| 其余所有目录（含本项目新增代码） | [Apache License 2.0](LICENSE) |

使用、修改或分发本项目前，请务必阅读 `PCL.Launcher.Core/LICENCE` 中的《PCL 分发有限许可》与《PCL 存储库合理使用指南》。要点包括：

1. 明确标注本作品为第三方二次创作
2. 公开源代码
3. 在关于页首位保留龙腾猫跃的署名与赞助链接
4. 保留 Minecraft 正版购买劝导
5. 界面主要色相保持蓝色

## 🙏 致谢

- **龙腾猫跃** —— Plain Craft Launcher 原作者，本项目的基础
- **PCL Community** —— PCL CE 开发团队，本项目的直接上游
- **WorkBuddy / Deepseek-V4.1-Flash / Aether极客** —— 本修改版的开发与设计支持
- 以及 PCL 与 PCL CE 的全部贡献者

本软件非 MINECRAFT 官方产品。未经 MOJANG 或 MICROSOFT 批准，也不与 MOJANG 或 MICROSOFT 关联。
