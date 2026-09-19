# 主题系统设计文档

> 状态：方案已敲定（2026-09-20，与 Leo 三轮问答确认），待实施。
> 本文档是施工依据。改方案先改这里。

## 1. 决策记录（Why）

| 决策点 | 结论 | 备注 |
| --- | --- | --- |
| 主题数量 | **5 个家族 × 浅/深双版本 = 10 套** | Leo 拍板一次全上。风险：每套都要调语义色 + 热力图 + 过 UI 测试断言，工作量大，已知情接受 |
| 风格参照 | Notion/Things 极简、Linear/Raycast 深色、Catppuccin、Apple 液态玻璃 | 加上现有的鼠尾草绿，共 5 个家族 |
| 默认主题 | **鼠尾草（现有绿色）** | 保持产品延续性，老用户无感迁移 |
| 语义色策略 | **每主题单独调校** | 语义不变（红=过期、绿=今天到期、灰=已完成），具体色值逐主题调 |
| 选择器形态 | **缩略色板画廊**（卡片标题栏按钮弹出）+ 托盘右键菜单双入口 | 画廊参考 Bear；弹出层复用截止时间面板的交互模式 |
| 深浅模式 | **记家族，深浅默认跟随 Windows 系统** | 另提供「锁定浅色 / 锁定深色」。设置存：主题家族 + 深浅模式 两个字段 |
| 液态玻璃路线 | **先仿后真** | v1 用渐变+高光描边仿玻璃观感（零架构改动）；真 DWM 亚克力模糊另开实验分支，成了再升级，败了不伤其他主题 |

## 2. 主题名单与草稿配色

色值均为**草稿**，实施时逐主题校准对比度（正文文字 vs 卡片底 ≥ 4.5:1）。

### 2.1 鼠尾草 Sage（默认）
气质：温和、安静。现有浅色版即经典；深色版是新贡献（墨绿底 + 青玉点缀）。

| 令牌 | 浅色 | 深色 |
| --- | --- | --- |
| CardBg | `#FFFEFB` | `#1E2622` |
| CardBorder | `#E2E8E0` | `#33413A` |
| TextPrimary | `#303A36` | `#DDE5DF` |
| TextSecondary | `#8A958D` | `#8A9890` |
| Accent | `#3C7865` | `#6FB89B` |
| AccentHover | `#2D6351` | `#5AA387` |
| HoverSurface | `#F1F5F0` | `#28322D` |
| InputBg | `#F4F6F0` | `#252E29` |
| 过期红 | `#C0504D` | `#E07870` |
| 到期绿 | `#4E9A5F`（现调校） | `#7FC98F` |
| 完成灰 | `#8D9891` | `#6B7A70` |
| 热力图 浅→深 | `#EDF1E8 → #9EB796` | `#2A3530 → #8FD3A8` |

### 2.2 石墨 Graphite
气质：Notion / Things 3 式极简，灰阶为主，强调色近黑（浅色）/近白（深色）。

| 令牌 | 浅色 | 深色 |
| --- | --- | --- |
| CardBg | `#FFFFFF` | `#191919` |
| CardBorder | `#E5E5E3` | `#2C2C2C` |
| TextPrimary | `#1F2328` | `#E8E8E6` |
| TextSecondary | `#8B8F94` | `#9B9B98` |
| Accent | `#37352F` | `#ECECEA`（深色版按钮为白底深字，Notion/Linear 惯例） |
| AccentHover | `#24231F` | `#FFFFFF` |
| HoverSurface | `#F5F5F4` | `#252525` |
| InputBg | `#F7F7F5` | `#222222` |
| 过期红 | `#C4554D` | `#E06C65` |
| 到期绿 | `#4C8A58` | `#6FBF82` |
| 完成灰 | `#9B9E9F` | `#6E7377` |
| 热力图 浅→深 | `#EFEFED → #55534E` | `#2A2A2A → #D8D8D5` |

### 2.3 午夜 Midnight
气质：Linear / Raycast。深色是主场（深蓝黑 + 电紫），浅色为冷白 + 同色系紫。

| 令牌 | 浅色 | 深色 |
| --- | --- | --- |
| CardBg | `#F7F8F9` | `#101216` |
| CardBorder | `#E3E5E8` | `#23262E` |
| TextPrimary | `#22262B` | `#E6E8EE` |
| TextSecondary | `#6E7480` | `#7C8290` |
| Accent | `#5E6AD2` | `#8A97FF` |
| AccentHover | `#4E57B8` | `#7582F0` |
| HoverSurface | `#EEF0F4` | `#1B1E26` |
| InputBg | `#FFFFFF` | `#171A20` |
| 过期红 | `#D95757` | `#F07070` |
| 到期绿 | `#3D9A63` | `#5FC98A` |
| 完成灰 | `#9AA0AB` | `#5C6370` |
| 热力图 浅→深 | `#E8EAF6 → #5E6AD2` | `#262B4D → #8A97FF` |

### 2.4 猫布奇诺 Catppuccin
直接采用官方调色板（Latte 浅 / Mocha 深），对比度已经社区验证。强调色取 Mauve。

| 令牌 | Latte（浅） | Mocha（深） |
| --- | --- | --- |
| CardBg | `#eff1f5` (base) | `#1e1e2e` (base) |
| CardBorder | `#ccd0da` (surface0) | `#313244` (surface0) |
| TextPrimary | `#4c4f69` (text) | `#cdd6f4` (text) |
| TextSecondary | `#6c6f85` (subtext0) | `#a6adc8` (subtext0) |
| Accent | `#8839ef` (mauve) | `#cba6f7` (mauve) |
| AccentHover | `#7a3dd6` | `#b38ef0` |
| HoverSurface | `#dce0e8` (surface1) | `#292c3c` |
| InputBg | `#e6e9ef` (mantle) | `#181825` (mantle) |
| 过期红 | `#d20f39` (red) | `#f38ba8` (red) |
| 到期绿 | `#40a02b` (green) | `#a6e3a1` (green) |
| 完成灰 | `#9ca0b0` (overlay0) | `#6c7086` (overlay0) |
| 热力图 浅→深 | `#dce0e8 → #8839ef` | `#313244 → #cba6f7` |

### 2.5 液态玻璃 Liquid Glass（仿）
气质：iOS 26 液态玻璃的**观感**——半透渐变底 + 顶部高光描边 + 柔光阴影，**不真透壁纸**。
可读性兜底（2026-09-20 第二次调整）：应 Leo 要求浅色版背景透明度走激进档——卡片渐变 **90/82→65/55%**、输入框 80→70%、图钉/空态 75→60%，文字仍全不透明。**65/55% 已低于原"≥80%"兜底线，属 Leo 明确要求的例外**；若日后反馈浅色壁纸下文字发虚，回调方向即此表。深色版维持 76/66% 不变。

| 令牌 | 浅色 | 深色 |
| --- | --- | --- |
| CardBg | `#A6FFFFFF`（65% 白）渐变至 `#8CF5FAFF` | `#C21C1C1E`（76% 黑）渐变至 `#A81C1C1E` |
| CardBorder | `#A6FFFFFF`（顶部高光 `#80FFFFFF`）| `#33FFFFFF` |
| TextPrimary | `#1C1C1E` | `#F2F2F7` |
| TextSecondary | `#6E6E73` | `#98989D` |
| Accent | `#007AFF` | `#0A84FF` |
| AccentHover | `#0062CC` | `#3B9BFF` |
| HoverSurface | `#73FFFFFF` | `#26FFFFFF` |
| InputBg | `#B3FFFFFF` | `#212C2C34` |
| 过期红 | `#FF3B30` | `#FF453A` |
| 到期绿 | `#34C759` | `#30D158` |
| 完成灰 | `#8E8E93` | `#636366` |
| 热力图 浅→深 | `#E3F0FF → #007AFF` | `#1C3A5E → #3B9BFF` |

### 不随主题变的东西
- **8 月 17 日红心**：所有主题下同一颗红心、同一句话。这是彩蛋，不是主题元素。
- 语言、提醒、存储逻辑与主题完全解耦。

## 3. 架构设计

现状：颜色硬编码在 `MainWindow.xaml`（50+ 处）和 `Logic.cs`（热力图色阶、截止状态色）里。

### 3.1 新增 `Theme.cs`
- `ThemeFamily` 枚举：`Sage / Graphite / Midnight / Catppuccin / LiquidGlass`
- `LightDarkMode` 枚举：`FollowSystem（默认）/ Light / Dark`
- `ThemePalette`：上表全部令牌的具名字段（约 14 个 + 热力图色阶数组）
- 内置注册表：5 家族 × 2 = 10 个静态 palette 实例
- `Current` 当前 palette + `ThemeChanged` 事件

### 3.2 XAML 改造
- 所有硬编码色值改为 `{DynamicResource ThemeXxx}`，资源键与令牌一一对应
- 切换主题 = 替换 `Window.Resources` 里的画刷（代码置办），DynamicResource 自动刷新
- 液态玻璃主题额外注入：背景渐变画刷 + 顶部高光 Border（各主题该高光透明即可，模板不变）

### 3.3 代码侧颜色
- `Logic.cs` 保持纯逻辑：热力图色阶、截止状态色改为**接受 palette 参数**（不读全局状态，可测试性不变）
- ViewModel 里所有 `*Brush` 属性改从 `Theme.Current` 取，订阅 `ThemeChanged` 触发刷新

### 3.4 设置持久化（`tasks.json` 的 settings 区）
新增两个字段（缺省 = 鼠尾草 + FollowSystem，向后兼容旧数据文件）：
```json
"themeFamily": "sage",
"lightDarkMode": "followSystem"
```
跟随系统：监听 `SystemEvents.UserPreferenceChanged`，读注册表 `HKCU\...\Personalize\AppsUseLightTheme`；程序运行中系统切深浅时即时跟随（仅 FollowSystem 模式）。

### 3.5 切换入口（双入口，状态同源）
1. **卡片标题栏**：最小化按钮左侧加调色板图标按钮 → 弹出画廊浮层（复用 DuePickerPopup 模式）：
   - 5 个主题家族各一张迷你预览卡（底色块 + 强调色圆点 + 对勾示意 + 三语主题名）
   - 底部三态切换：跟随系统 / 锁定浅色 / 锁定深色
   - 点击预览即换，所见即所得
2. **托盘右键菜单**：新增「主题」子菜单（5 家族单选）+「深浅」子菜单（三态单选），与「语言」菜单同级

### 3.6 三语文案
`Localization.cs` 新增：5 个主题名、画廊标题、深浅三态、托盘菜单项。主题名建议：
Sage 鼠尾草/鼠尾草/Sage；Graphite 石墨/石墨/Graphite；Midnight 午夜/午夜/Midnight；Catppuccin 猫布奇诺/貓布奇諾/Catppuccin；Liquid Glass 液态玻璃/液態玻璃/Liquid Glass。

## 4. 验证计划

1. **纯逻辑**：`tests\StorageTests.cs` 加——10 套 palette 全部令牌非空；设置字段序列化/反序列化/缺省兼容（旧 tasks.json 无主题字段能正常读）。
2. **UI 测试**：`tests\UiTests.cs` 加——遍历 10 套 palette 切换不抛异常、所有 DynamicResource 键可解析；画廊浮层开合；托盘菜单与画廊状态一致；模拟系统深浅切换（FollowSystem 模式）。
3. **人工校准**：每套 palette 过一遍——正文对比度、过期红/到期绿在各自底色上的辨识度、热力图深浅端点不发灰、深色下阴影不脏。
4. **回归**：现有 UI 测试的视觉断言按主题参数化重跑（10 套 × 现有断言，这是「5 个全上」的主要成本）。

## 5. 风险与未决项

| 风险 | 应对 |
| --- | --- |
| 10 套一起调，后期疲劳导致质量掺水 | 实施顺序仍按 鼠尾草→石墨→午夜→猫布奇诺→液态玻璃 逐个打磨，只是同一版本发布 |
| 液态玻璃深色版底不透明度 76%，花桌面下可能显脏 | 人工校准环节实机看；必要时提到 85% |
| Win10 上玻璃高光描边效果打折 | 可接受降级，不阻塞发布 |
| 真·DWM 模糊实验 | 独立分支，不动 AllowsTransparency 主线；成功后再评估是否只给 LiquidGlass 家族启用 |

## 6. 不做的事（明确排除）

- 不做自定义取色器 / 用户自定义主题（v1 只有内置 10 套）
- 不做真毛玻璃（见 §1 液态玻璃路线）
- 不做动画过渡（主题切换瞬时生效；渐变动画留给以后）
