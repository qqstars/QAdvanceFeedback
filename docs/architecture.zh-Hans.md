# 架构文档

[English](architecture.md) · **简体中文**

> ## 维护规则——强制性规定，而非建议
>
> **每当下方描述的任何算法或机制发生变化，或者核心文件结构发生变化时，本文档必须同步更新。** 本文档中的逐文件地图和每一段算法描述，其存在的意义就是让下一位读者——无论是人类贡献者还是 AI 代理——可以直接信任它们，而不需要重新从源码中反推整个设计。如果你（不管是人类贡献者，还是正在对本仓库进行操作的 AI 代理）修改了某个文件的作用、移动了文件、新增或删除了文件，或者改变了某个算法的工作方式，**更新本文档是这次改动的一部分，而不是可以之后再补、或者干脆跳过的后续任务。** 任何改变了行为或文件结构、却没有同步更新本文档的提交/合并请求，都应当被视为未完成。在把下面的任何内容当作事实依据之前，请先对照实际的目录结构核实逐文件地图，而不是想当然地认为它是最新的——如果发现它不是最新的，请把修复它当作你手头工作的一部分，而不是事后才想起来的附加项。

本文档描述 `QAdvanceFeedback` 背后的分层模型，并把每一个实现文件映射到它所属的层。它存在的目的，是让贡献者（或者未来的作者本人）能够直接找到「这个行为归哪个文件负责」，而不必重新从代码中推导整个设计。

## 子系统算法——速查表

下表与项目 [`README.zh-Hans.md`](../README.zh-Hans.md#4-技术细节) 中的表格相同。每个子系统都链接到本文档后面对应的「工作原理与设计原因」小节。

| 子系统 | 核心算法/机制 | 用途 |
|---|---|---|
| [**Wheel Lock Raw / Wheel Slip Raw**](#wheel-lock-rawwheel-slip-raw工作原理与设计原因) | 精确复现 SimHub 自身基于转速/车速的传统 iRacing 抱死与打滑公式，按标题在多个由能力标志位选择的分支专用模型间调度，再通过 Max/Min 轴间混合加前后轴加权混合，按轮组合并。 | 忠实、未归一化地复现 SimHub 自身广为人知的算法——本插件其余一切内容都建立在这个共同基准之上。 |
| [**Wheel Lock/Slip Normalizer**](#wheel-lockslip-normalizer工作原理与设计原因) | 依据一个按（游戏、车辆、数据源）学习的物理抓地力峰值参考值（一个刻意缓慢收敛的 EMA）重新缩放 Raw 的逐轮数值；通过锚定在「正处于物理极限」这类稀有、独立检测时刻的学习器按数据源交叉标定；并通过一套按分散度加权的冷/热混合机制，在实时证据与持久化数据之间混合。 | 让「80」在任何车上都意味着同一件事——「处于测得的抓地极限」——而不是一个含义会随车辆抓地力好坏而漂移的数字。 |
| [**Wheel Lock/Slip Projector**](#wheel-lockslip-projector工作原理与设计原因) | 把归一化后的 0-100 数值送入一条驾驶者可编辑的五锚点曲线，用单调三次插值平滑，外加一个可选的「达到最大值时脉冲」阶段。 | 把「这有多严重」这个数字，变成「它应该是什么样的体感」——唯一设计给震动器绑定使用的属性层。 |
| [**G-Force**](#g-force工作原理与设计原因) | 一套「持续 G 力水平」与「由变化率驱动的瞬态」相分离的洗出式模型，通过满足单位分割条件的分段线性函数映射到一条 3 级震动垫链条上；每款游戏/每辆车各自的最大值通过一个修剪样本池的鲁棒估计器，在一个实时滚动窗口内学习得到；车轮抱死/打滑可选择性地在其上叠加抖动，通过四种模式之一选择该抖动在各通道间的分配方式，并通过三种「手感」之一决定其波形与左右关系。 | 让座椅震动垫获得连续、有方向感的刹车/加速/过弯载荷体验，与车轮通道相互独立又彼此互补。 |
| [**Slip Angle（滑移角）**](#slip-angle滑移角工作原理与设计原因) | 从按游戏的 raw 数据表读取原生逐轮轮胎滑移角（自动晋升，单位按事件投票自动检测），否则使用车身侧滑回退；带前后轴等效换算的单一 Raw.All；按（游戏、车、数据源、轴、路面）的抓地曲线学习器，其 Max 只由经佐证的牵引力穿越教学，并有碰撞保护；三档路面；借用档位；15/30/60/80/100 单调归一化（15 位于推导出的起点角）；漂移衰减状态机。 | 告诉驾驶者轮胎何时进入理想滑移角窗口、何时越过它，同时不会在保持漂移时一路嗡鸣——并为 G 力抖动提供输入。 |

## 分层模型

遥测数据依次经过五层，再加上两个独立子系统（G 力，以及设置/持久化）。每一层都发布自己的一层属性（第 1-2 层除外，它们是内部层），并且只依赖它下方的层——绝不横向依赖，也绝不向上依赖。

### 第 1 层——遥测接口

**功能：** 定义与具体游戏无关的一帧遥测数据形状（`ITelemetryFrame`/`TelemetryFrame`）和一个采样（`ITelemetrySample`/`TelemetrySample`，当前帧 + 上一帧 + 经过的时间）。每一项读数都可以独立为 null，这一点非常关键：null 的含义是「这款游戏没有提供这个数值」，绝不是真实的零值。

**禁止依赖：** 任何东西。这是整个技术栈的最底层——没有 SimHub 类型，也不依赖其他任何层的类型。

### 第 2 层——SimHub 适配器

**功能：** 唯一允许知道 SimHub 自身类型名称（`GameData`/`StatusDataBase`/`FeedbackData`/`PluginManager`/`FeedbackCapabilities`）的地方。把 SimHub 的实时遥测数据映射到第 1 层的形状（`SimHubTelemetryAdapter`），并单独捕获一份逐轮原始诊断快照（`RawWheelTelemetrySnapshot`，由能力标志位把关，确保某款游戏的真实零值不会被误判为「不支持这个通道」——见 `RawWheelTelemetryBuilder`）。

**禁止依赖：** 不依赖它上方的任何东西。它可以引用 SimHub/GameReaderCommon 的类型（它是唯一被允许这样做的地方），但第 3 层及以上永远不会反过来引用 SimHub 类型。

**Slip Angle 输入：** `SlipAngleRawSources.cs` 是按游戏的 raw 数据表（1.2.1起只由`QAdvanceFeedback.SlipAngleSource.json`构建，见`SlipAngleSourceFile.cs`），`SlipAngleRawSourceReader` 通过查值委托读取它，因此数据表本身不依赖 SimHub、可链接编译进测试。`SimHubTelemetryAdapter` 在 `ITelemetryFrame` 上补充 Slip Angle 所需的车身输入（前向局部速度、偏航率、来自 `IsFlying` 的腾空、路肩、垂直 G、路面抓地提示）。

### 第 3 层——Raw 计算器

**功能：** `QAdvanceFeedback.Core.RawCalculator`——把一个遥测采样转换成发布的 `WheelLock.Raw.*`/`WheelSlip.Raw.*` 属性。`WheelSlipBranchSelector`（位于 `Core` 而非 `RawCalculator`，因为它只是对公开能力标志位的纯布尔优先级判断，不涉及任何具体公式）决定某款游戏支持哪种信号形态；`RawCalculatorEngine` 负责调度到匹配的公式，并持有每一个有状态的学习器。按设计未归一化——这里的「40」在不同车上含义不同，这个问题在上一层才会被修正。

**保真契约（1.0.7.0）：** 本层的职责是复现 SimHub ShakeIt Motors 插件实际发布的数值，使两者体感一致。自 1.0.7.0 起，标定（calibration）机制不再是“功能相似的自有实现”，而是 SimHub 自身实现的忠实移植，见 `Core\RawCalculator\Calibration\`：直方图及其自适应分桶阶梯、500 个正样本的百分位门槛、`Math.Max(1.0, Max * 0.9) * pct / 100` 的未成熟回退公式、`track;car;metric` 键、7000 点的写入上限，以及与预置数据固定 0.25 的混合比例，均来自 SimHub，并已对随包发布的 `SimHub.Plugins.dll` 逐一核实。**仅保留两处有意为之的偏离**，且均在调用处有说明：平滑滤波器经过 dt 修正（SimHub 使用固定的每帧系数，其时间常数会随游戏遥测帧率变化；本插件在 60Hz 下与其完全一致，在其他帧率下则保持一致行为）；以及最后一个分支的 legacy/非 legacy 子选择未由任何能力标志位暴露，因此总是假定为 legacy 变体。

**禁止依赖：** SimHub 类型（由测试项目强制执行——它把这个文件夹直接链接编译进一个不引用任何 SimHub 包的纯 net8.0 程序集，一旦有 SimHub 依赖悄悄混进来，测试构建会立即失败），以及第 4/5 层、G 力或设置层的任何内容。

### 第 4 层——Normalized（归一化）

**功能：** `QAdvanceFeedback.Core.Normalized`——把第 3 层的逐轮数值形状，与仅通过车速/油门/刹车/G 力学习到的、相对于本车的严重程度（`GripLearner`/`KeyedGripLearner`，按游戏+车辆+数据源分别建键，并有一套按路面和按数据源缩放的扩展）结合起来，从而让发布的区间在一辆经常拉 4G 的街机风格赛车和一辆经常拉 1.2G 的模拟风格赛车中含义相同。

**禁止依赖：** SimHub 类型，或第 5 层/G 力。可以依赖第 3 层的输出形状（`Corners`、`LegacyWheelLockSlipResult`）和第 1 层（`ITelemetrySample`）。

**Slip Angle** 是一套并列的独立栈，而不是本引擎中的第三个通道：`Core\SlipAngle\` 包含它的 Raw 计算（第 3 层角色）、门控、窗口学习器、归一化与路面分类（第 4 层角色），以及串联它们的每帧处理器。它与 Lock/Slip 共享 `Core\Normalized\` 中的四个核心——`CrossingGate`、`CorroboratedLevelEstimator`、`ColdStartTierResolver`、`ImpactGuard`——见下文「共享核心」。

### 第 5 层——Projected（映射）

**功能：** `QAdvanceFeedback.Core.Projection`——把第 4 层的输出送入一条驾驶者可编辑的单调曲线（`MonotoneCubicCurve`/`OutputProjector`），以及一个可选的「达到最大值时脉冲」阶段（`PulseGenerator`）。这是驾驶者应该绑定震动器的那一层。

**禁止依赖：** SimHub 类型。可以依赖第 4 层的输出形状。

Slip Angle 的第 5 层是 `SlipAngleProjectedEngine`（同一个 `OutputProjector`/`PulseGenerator`，使用 `ProjectionChannel.SlipAngle` 预设与 `ColdStartScale`）加上 `DriftAttenuator` 状态机。

### G-Force（G 力）

**功能：** `QAdvanceFeedback.Core.GForce`——一套独立的通道集合（完全不从第 3-5 层派生），建模了一种「洗出式」结构，把持续水平与由变化率驱动的瞬态分开，另外还有按游戏/按车辆学习的最大值（`GForceMaxLearner`）。

**禁止依赖：** 第 3/4/5 层或 SimHub 类型。（车轮抱死/打滑的「抖动」集成会读取第 5 层的输出，作为 G 力自身振幅的一个输入——这是唯一的刻意例外，详见 `GForceEngine.Compute` 自己的注释。Slip Angle 加入后，同一例外也涵盖最终映射后的滑移角，仅在开启「Integrate Slip Angle」时读取。）

### 设置/持久化

**功能：** `QAdvanceFeedback.Settings`（设置 POCO 加 WPF 设置界面）以及 `ConfigStore`/`RuntimeStore`/`Core.Runtime`（配置与学习状态的 JSON 持久化）。只读写普通的 double、枚举和字符串——绝不会把一个存活的 SimHub 引用嵌入到持久化对象里。

**禁止依赖：** 就任何需要做单元测试的部分而言，不依赖 SimHub 类型。WPF 视图是唯一必须依赖 WPF 的部分；自全 MVVM 设置页（2026-11-20）起，视图不再包含任何规则——设置页的每条规则都在 `Ui\ViewModels` 下不依赖 WPF 的视图模型中（net8 测试覆盖），`MvvmRuleTests` 保证视图只做接线（见「UI：MVVM（长期规则）」）。

## 算法细节——每个子系统的工作原理与设计原因

### Wheel Lock Raw / Wheel Slip Raw：工作原理与设计原因

`RawCalculatorEngine`（第 3 层）是本插件唯一复现 SimHub 自身传统 iRacing 车轮抱死/打滑公式的地方——通过反编译随包发布的 `SimHub.Plugins.dll` 来核实算法本身，确保算法完全一致，而不是靠猜测反推出来的。并不是每款游戏都会暴露同一种形状的车轮遥测数据（有些暴露真实的逐轮转速，有些只有踏板/车速/转速），因此 `WheelSlipBranchSelector` 会在每一帧决定使用 `DispatchBranchFormulas.cs`/`BrakeSpeedSlipModel.cs`/`BrakingVsSpeedModel.cs`/`WheelRotationLockFilter.cs` 中的哪一个分支专用公式——这纯粹是对 `RawWheelTelemetryBuilder` 为该游戏捕获的能力标志位的布尔优先级判断，绝非隐藏的公式选择。这种分支选择的意义在于：拥有丰富逐轮遥测数据的游戏能得到更精确的逐轮读数，而只有踏板/车速/转速的游戏依然能得到一个可用的、车辆级别的近似值，插件不需要强制要求每款游戏都提供同一种遥测形状。

两个通道在算法真正介入之前，都会先根据踏板位置进行门控（`LegacyThresholds`，可由车主配置，刻意偏离了 SimHub 自身硬编码的数值）——Wheel Lock 在刹车踏板超过阈值后触发；Wheel Slip 先检查一个（默认禁用的）刹车阈值，再检查油门阈值，这与 SimHub 自身内部不区分 Lock 和 Slip 的统一算法保持一致。

一旦四个车轮各自得到一个 0-100 的读数，`Aggregator`/`AggregationWeights` 就会用一套具有物理依据的两阶段加权混合，而不是简单的对称平均，把它们合并成 `Front`/`Rear`/`Left`/`Right`/`All`——因为载荷转移是车轮抱死/打滑体感应该反映的主要真实效应：刹车时重心前移，前轮承担主要抓地力，因此最重要；动力输出时，驱动轮才是打滑的那一个。

- **轴间混合：** `Front = Max(FL,FR)×WMax + Min(FL,FR)×WMin`（`Rear` 同理）——与顺序无关；哪个物理车轮更强并不重要。
- **左右/整车混合：** `Left = FL×WFront + RL×WRear`，`Right = FR×WFront + RR×WRear`，`All = Front×WFront + Rear×WRear`——与顺序有关；前永远是前。
- **仅 Wheel Slip 默认启用：** 一个下限（`result = Max(result, Max(参与的车轮)×SlipFloorFactor)`），确保单个强烈打滑的车轮不会被平均掉。

两个混合阶段都是简单的加权和，这让整条流水线从头到尾都保持连续——一个裸的 `Math.Max` 虽然同样不会产生数值跳变，但在交叉点处会产生比加权混合大得多的*斜率*不连续（也就是能感受到的「咔哒」感）。全部五个权重都可以按通道独立配置，并且刻意**不**强制要求它们相加为 1——如果驾驶者希望得到放大或衰减后的合并读数，就应该得到这个结果本身，而不是被悄悄「纠正」过的版本。完整推导与连续性证明见 `docs/aggregation-report.md`。

### Wheel Lock/Slip Normalizer：工作原理与设计原因

`NormalizedWheelLockSlipEngine`（第 4 层）存在的原因是：Raw 本身的 0-100 读数按设计是未归一化的——在一辆抓地力最多只能拉到 1.2G 的车上，「40」的含义和在一辆能拉到 4G 的车上完全不同。第 4 层的解决办法是：按**游戏 + 车辆 + 数据源**分别学习这辆车实际能达到的物理峰值，并用这个学到的峰值作为 Raw 读数重新缩放的参照。

- **`GripLearner`/`KeyedGripLearner`** 把这个学到的物理抓地力参考值，保存为一个刻意缓慢收敛的 EMA。这种缓慢收敛是有意为之的设计，而不是疏漏：一个收敛很快的学习器会把单次尖峰（一次碰撞、一次短暂的抱死）误当作这辆车真实极限的证据；被钉住的回归测试 `A_cold_start_never_publishes_higher_than_the_source_across_a_synthetic_braking_event` 正是为了防范这一点而存在的——曾经评估过一种收敛更快的估计器（用 `RobustBandEstimator` 替换，见 `docs/robust-auto-gforce-report.md` 第 3 节与 `docs/cold-start-convergence-report.md`），并且正是因为这个原因被否决：它对一个恒定的、非极限信号收敛得太快，快到会让普通的、远未触及极限的驾驶被误判为「正处于物理极限」。
- **`KeyedScaleLearner`** 按数据源分别做交叉标定（因为不同的数据源模式/表达式可能使用不同的原生量程），并且只锚定在极少数、独立检测到的「正处于物理极限」的时刻——而不是一条原始的噪声数据流——这也是它无需借助鲁棒估计器就能保持抗离群值能力的部分原因。 `CanonicalAtLimitAnchor`（物理极限读数最终被重新缩放到的规范值）现在是 **80** （`docs/anchor-rescale-report.md`——从 75 调整而来，与 Projector 顶部「最大抓地力」锚点的输入位置完全重合）。该报告同时修复了置信度机制中的一个真实缺陷，并确认了修复后的收敛效果，详见该报告。
- **`ColdWarmBlend`** 每一帧都会决定，对同一个键而言，应该更信任本局的实时证据，还是更信任持久化下来的历史数值，其权重依据的是实时证据自身的**分散程度**（变异系数），而不仅仅是样本数量——一个噪声很大的会话，即使已经积累了大量样本，也会逐渐倾向于「信任持久化/冷数据」；而一个稳定、可重复的会话，哪怕样本不多，也能很快获得信任。这两个因子都是平滑、饱和的函数，因此不存在某个样本数量或分散度的阈值，会让实时混合结果发生跳变。
- **`SurfaceLooseFraction`** 在密实/松散路面条件之间连续地混合学到的参考值，而不是在两个固定参考值之间切换。
- **哪些帧可以教学 SMax：穿越门与 95 硬上限（2026-11-19）。** 物理极限证据只能经由共享的 `CrossingGate` 进入 `ObserveAtPhysicalLimit`。一次穿越就是数据源上升而 G 下降。它教学的是这一段上升中抓地峰值处的快照；回溯只会停在本身也是从前一帧上升而来的帧上，因此上升起点那一帧平坦的数值永远不会成为快照。1 秒保持窗口内的每一帧都教学这个快照，绝不教学自身的实时值；快照不可用的穿越会被直接拒绝，而不是回退到实时值。每一个被教学的数值、每一个学习 / 发布 / 持久化的上限、每一个导入值，以及手动输入的 SMax 或自定义参考值，都不超过 **95**（`KeyedScaleLearner.MaxLearnedCeiling`）：读数为 100 的数据源表示车轮已抱死或空转，绝不是抓地极限。此修复解决的缺陷（穿越后的失控值把 SMax 学成 100/90/70）以及语料库前后对比数据见 `docs/slip-smax-crossing-gate-design.md` §10。

针对七份真实遥测日志的直接测量（`docs/cold-start-convergence-report.md`）表明，当前的收敛节奏已经达到数据本身能安全支持的最快速度——如果再加快，会以可测量的方式牺牲抵御瞬时过度报告的余量，而这正是这套设计要极力避免的风险。

### 关键数据点：手动 SMax/S90/S75 及其生效时机（1.0.7.0）

`Settings\KeyDataPointSettings.cs`、`Core\Normalized\ManualOverrideGate.cs`。

**学习从不受条件限制。** `AutoGenerate` 只决定哪些数值会被**发布**，并不控制观测本身。`ObserveAtPhysicalLimit`/`ObserveGeneral` 以及锚点学习器的弯道缓冲，都在 `ComputeChannel` 中手动数值那一段**之前**无条件执行。由此带来三个结果：手动模式下 `[学习值: xx.x]` 提示依然有意义；切回自动是立即生效的；手动数值永远不可能污染学习器已经掌握的内容。

**设置页与面板显示什么——唯一的显示规则，重启后同样适用（2026-11-19）。** `KeyDataPanelModel.LearnedKeyData` 是设置页（其 `KeyDataPageViewModel` 在每次 `PluginStateChanged` 时经宿主读取，驾驶时每秒一次）、两个 ShakeIt 面板以及一次性写入（seed）唯一的「学习值」来源：即引擎自身用作除数的上限 `KeyedScaleLearner.PublishedCeiling`，按 `KeyedGripLearner.MakeKey` 生成的**完全相同**的持久化键（游戏、车、数据源、路面）。Lock 的 S90/S75 来自其学习到的物理锚点比例（初始 0.90/0.70）；Slip 的为 SMax 的 0.90/0.70。重启后 SimHub 停在菜单、没有游戏运行时，插件使用上次显示关键数据时的（游戏、车）（`RuntimeDocument.LastKeyDataGame/Car`）。若 `QAdvanceFeedback.Parameters.json` 中存在该完全相同的键，就显示其持久化的 SMax/S90/S75；不存在时显示冷启动默认值。绝不显示其他游戏或车辆的数值（原先「该数据源在所有游戏和车辆中的最佳值」查找正因此被移除）。学习到的锚点比例现在也会被持久化：运行时缓存过去只复制旧的锚点字段，导致每次重启后 S90/S75 都回到 0.90/0.70。设置页上 Lock 自动模式的 S90/S75 与面板使用同一规则（物理比例）；手动模式下槽位为空时显示该数据源的默认值（`KeyDataPanelModel.Resolve`），与面板一致。

**Slip Angle 遵循同一规则（2026-11-20，`docs\slip-angle-design.md` §9.4）。** `Core\SlipAngle\SlipAngleKeyDataModel` 是它的 Slip Angle 版本：`LearnedWindow` = 完全相同的键（游戏、车、按当前身份档位确定的数据源身份、路面）的已发布窗口，并按数据源封顶（原生 / Custom 20°，车身角 30°）；学习器中没有该键时为空——绝不显示借用来的窗口。`Shown`：自动 = 学习值，否则默认值；手动 = 已存槽位，否则默认值。一次性手动写入写入的正是所显示的学习窗口（`SlipAngleDisplayRuleKeyData`）。页签、ShakeIt 面板与写入共用这一规则。为了重启后停在菜单时也能显示，插件把最后一次 Slip Angle 上下文保存在 `RuntimeDocument` 中——`LastSlipAngleGame`、`LastSlipAngleCar`、`LastSlipAngleTier`（身份档位的 `SourceNativeness` 名称），与 `LastKeyDataGame/Car` 并列——并在 Init 导入学习器之后恢复（`SlipAngleKeyDataContext`），因此无需运行游戏即可组成完全相同的键。旧文件没有这些字段：首次驾驶前显示默认值。

**数值按「槽位」存储，槽位 =（模式, 游戏, 数据源）。**

```
全局模式     ->  "global|src:<sourceIdentity>"
每游戏模式   ->  "game:<gameId>|src:<sourceIdentity>"
```

数据源**始终**是键的一部分，因为适用于 ShakeIt 数据源的数值并不适用于本插件自己的 Raw——两者量程不同。游戏只在每游戏模式下参与构键；不含游戏，正是「全局」的含义。两类命名空间共用一个字典，因此「全局 → 每游戏 → 全局」的往返切换，会原封不动地取回最初的全局数值。

**门控。** `ManualOverrideGate` 会一直扣住手动数值，直到该上下文**同时**满足：冷启动完成（`KeyedScaleLearner.CeilingHandoverConfidence` >= 0.95）**且**已累计 30 秒驾驶时间。只有车辆确实在移动的帧才会被计入，且任何超过 0.5 秒的单帧都会被截断——因此暂停、断点或切出游戏都无法凑满该门控。

**一次性写入发生在插件的每帧循环中**，而不是设置页面里——因为无论页面是否打开，它都必须发生。当某个槽位首次满足条件时，学习值会被写入，并直接调用 `ConfigStore.Save`（而非 `ApplySettings`，后者还会重建 Projected 引擎——那是不该在一帧中途做的事）。由于该闩锁是**按槽位**记录的，一个从未游玩过的游戏、或一个新选择的数据源，都会自行再次触发一次写入。插件会递增一个修订计数器（`KeyDataRevision`）并触发 `SettingsPageStateChanged`，使设置页（以及任何 ShakeIt 面板）重新加载被写入的槽位。

**默认值按数据源类型区分**，通过 `KnownSourceColdStartReference.Classify` 解析：对本插件自己的 Raw 与 ShakeIt 数据源，Lock 为 85/75/60，Slip 为 75（按数据源**类型**查找：带算法指纹的 ShakeIt 身份得到同样的出厂默认值）。**未知**数据源（脚本、NCalc 表达式）刻意**不给任何默认值**——对一个量程从未被测量过的信号，任何猜测都称不上诚实；这类通道会继续发布学习值，直到某个槽位被写入为止。

**仅最大抓地力 / 仅最佳点**模式会隐藏下方两个锚点，并在后台按顶部数值的 0.90/0.70 推导它们，因此切回三点映射时不会发现它们已经过时或次序错乱。Slip 始终采用推导方式——它没有原生的 90%/75% 抓地力测量——这也是它随包默认「仅最佳点」的原因。

**手动模式下的恢复按钮**（设置页上的 `KeyDataPageViewModel.ResetCommand`）：对 `KnownSourceColdStartReference.Classify` 能识别的数据源，写入随包提供的默认值，并将该槽位标记为已写入，使一次性学习值写入不会立刻覆盖驾驶者刚刚选择的内容。对**未知**数据源，没有可写入的默认值，因此改为调用 `KeyDataPointSettings.ClearSlot`——数值与已写入闩锁一并清除——从而重新武装一次性写入，并让输入框保持空白，直到真实证据到来。与页面上其他恢复操作一样，点击即刻持久化。

**自动模式下 Slip 不再被强制为「仅最大抓地力」**（作者改变主意，v1.0.8）：自动模式按固定比例由学习到的 SMax 推导 S90/S75，因此三点映射对两个通道都成立，选择框在两种模式下都可用，两个通道默认都采用三点映射。（原先的 `EnforceSlipPatternForAutoMode` 早已不起作用，随旧设置页一并删除。）

**两张曲线图**（数据由 `CurveEditorViewModel` 提供——曲线、标记、源数值图的取样点；由 `Ui\Views\NormalizedCurveGraph` / `GraphFurniture` 绘制）跟随设置页草稿中的数值，因此未应用的修改也会立即反映。左图为 归一化 -> 映射，虚线标记位于关键数据点在归一化坐标上的规范位置（S75 -> 30、S90 -> 60、SMax -> 80，即四段曲线自身的节点表），「仅最大抓地力」模式下仅显示 SMax。右图为 源数值 -> 映射 的完整链路；由于关键数据点与投影器是复合关系，它没有闭式解，因此按每 5 取样并连线。两张图共用同一套网格：每 20% 高度一条 25% 黑度的横线，0% 处改用纯黑作为基线，横向每 20% 一个 3px 刻度。

**「恢复默认」从不丢弃这些数值。** `QAdvanceFeedbackSettings.RestoreDefaults` 会把两个通道的 `KeyDataPoints`（所有槽位，以及自动/每游戏两个开关）一并带过重置；而三个通道级的数据源重置方法，只会触碰数据源字段。

### Wheel Lock/Slip 数据源：ShakeIt 内置、按通道的 Raw 回退、内部数据源学习（2026-11-19）

设计：`docs/shakeit-source-design.md`（ShakeIt 数据源）与 `docs/internal-source-learning-design.md`；交付总结：`docs/shakeit-source-report.md`。

**ShakeIt 内置只通过我们的原生效果读取 ShakeIt 的算法。** 导出属性路径已不复存在（ShakeIt Motors 导出配置、`MotorsExportAvailabilityProvider` 与 `MotorsExportAvailabilityResolver` 均已移除；`docs/shakeit-export-guide.md` 已退役）。ShakeIt 处理我们的任一 QAdvanceFeedback 效果（WheelLock、WheelSlip、四个 G-Force 效果、Clutch Bite、Slip Angle）时都会调用 `ShakeItBuiltInTap.Sample`。每次调用都会盖一个心跳戳（无论是否在游戏中）；在游戏中，它用 ShakeIt 交给它的游戏数据计算 ShakeIt `WheelsLockContainer` / `WheelsSlipContainer` 的隐藏副本，按通道发布数值，并附带 ShakeIt 自己对抱死与打滑分别给出的 `GetIsSupportedOnCurrentGame(caps)` 结论。

**检测**（`Core\ShakeItEffects\ShakeItSourceAvailability.cs`、`Integration\ShakeIt\ShakeItProfileInspector.cs`）：
- *已设置* = 任一 ShakeIt 插件（Bass Shakers、Motors）的当前配置文件中，至少有一个我们的效果实际处于启用状态（自身开关及所有上级分组均启用）——由 UI 线程上 1 Hz 的 `DispatcherTimer` 通过 SimHub 公开 API 读取（每个插件单独保护；健康子系统 `ShakeItProfile`）——**且**心跳不超过 2 秒。
- *驾驶中* = 游戏运行、未暂停、不在菜单，并满足 ShakeIt 自身的 FeedbackData 门控。
- 每个通道一个状态机：**NotSetUp / SetUpIdle / Supported / NotSupported**。Supported 要求驾驶中 tap 数值新鲜。NotSupported 来自 ShakeIt 报告游戏不支持（保持到切换游戏），或连续 3 秒驾驶中数值过期（收到第一组新鲜数值即恢复——裁定 R41）。经 0.5 秒去抖的一对状态（`ShakeItSourceStateBroadcaster`）发布为 `QAdvanceFeedback.ShakeItSourceState`，并触发 `SettingField.SourceAvailability`。

**按通道解析**（`Core\ShakeItEffects\ShakeItSourceResolution.cs`，每帧依据实时状态）：Supported 且 tap 新鲜 → 使用 tap 的数值，学习键为**规范 ShakeIt 身份**（`ShakeItSourceResolution.ShakeItIdentity`：四个 Plain 形式的旧导出属性名，与 2026-11-19 之前的键逐字节相同，再加上算法指纹）。其他任何情况（未设置、游戏中空闲、不支持、过期）→ 该通道四个车轮全部使用 Raw 的数值，学习键为 **`RawSourceFallback.RawIdentity`**——从不按单轮回退，也绝不把 Raw 数值学进 ShakeIt 键。Viper 被阻断时走同一条 Raw 路线。`QAdvanceFeedback.ShakeItSourceBlocked(channel, isLock)`（读取已发布状态；仅在 NotSetUp / NotSupported 结论时为真，空闲时从不为真）是设置页的「遮蔽」输入，与 `ViperSourceBlocked` 并列。预设数据源（ShakeIt 或 Viper）在通道被遮蔽时应用，保存的是它自己的预设行（`WheelChannelSettings.KeepPresetSourcesWhileShadowed`）。

**界面。** 当状态不是 NotSetUp，或它是当前显示或已保存的数据源时，列表中才列出 ShakeIt 选项（未设置时标为「ShakeIt 内置（未设置）」；隐藏的选项同时被禁用）。提示文字：「未设置——请在当前 ShakeIt 配置文件中添加一个 QAdvanceFeedback 效果。正在使用原始数值。」与「此游戏不支持——正在使用原始数值。」，仅在当前显示的数据源为 ShakeIt 时出现。规则位于 `Ui\ViewModels\ShakeItSourceViewModels.cs`（`ShakeItSourceUi`、`ShakeItSourceStatusViewModel`）；面板通过可选的宿主能力 `IShakeItSourceStateHost` 读取状态；设置页会在运行时 `SourceAvailability` 广播到来时重新采样，因此在菜单中也能跟随变化。

**ShakeIt 算法区块**（`Settings\ShakeItAlgorithmSettings.cs`，挂在 `WheelChannelSettings.ShakeItAlgorithm` 上；规则在 `Core\ShakeItEffects\ShakeItAlgorithm.cs`；VM 为 `ShakeItAlgorithmViewModel`；仅当数据源为 ShakeIt 内置时在页签与面板上显示，经五个 `SettingsHub` 字段同步）：
- Lock：旧版 iRacing 算法（开）、ShakeIt 抱死灵敏度（50，0–100，步长 5，各处均对齐到步长）、仅抱死模式（开）。Slip：旧版 iRacing 算法（开）、抱死时静音打滑（关）。
- 隐藏的过滤器跟随我们的阈值：Lock 刹车过滤器 = round(Lock 刹车阈值)；Slip UseBrakeFilter = 刹车阈值 < 100 且油门阈值 > 0；Slip UseThrottleFilter = 油门阈值 > 0（油门阈值为 0 时我们的「或」门始终打开，因此两个过滤器都关闭）。
- 静音由我们模拟：只要我们隐藏的 Lock 数值中任一车轮 > 0，该帧 Slip 四个车轮全部读为 0（不模拟 100 ms 保持）。
- 指纹追加在 ShakeIt 身份之后：`#alg=legacy:X,sens:N,lockOnly:X`（Lock）、`#alg=legacy:X,mute:X`（Slip）；默认值时为空。出厂关键数据默认值按数据源类型查找（`ShakeItAlgorithmFingerprint.BaseIdentity` 去掉指纹）；手动槽位、Auto 标志与学习都带指纹作键。数据线程每帧构建不可变的 `ShakeItAlgorithmSnapshot`（未变化时复用），并交给 tap（`ShakeItBuiltInTap.Configure`）；这一步在会话门控**之前**执行，因此在菜单中所做的修改会在第一个游戏帧之前到达 ShakeIt 的线程。
- **tap 快照标记**：tap 会给每帧的数值标上计算时所用的快照（`ShakeItTapValues.Algorithm`；快照同时包含算法设置与阈值过滤器）。`ShakeItSourceResolution.TapComputedUnder`（按引用比较）是「tap 新鲜」的一部分，因此来自旧快照的 tap 视为不新鲜，该通道会以 Raw 键读取 Raw，直到 ShakeIt 按新设置产生数值为止（修改踏板阈值后也会如此，约持续一帧）。旧设置下的数值因此永远不会进入新的带指纹键。

**内部数据源学习规则**（`NormalizedWheelLockSlipEngine`，`docs/internal-source-learning-design.md`）：
- 每个数据源只在自己的键下学习。**Plugin Internal（`PluginInternalSourceIdentity.For(isLock)`，等于 `RawSourceFallback.RawIdentity`）每帧都学习**，无论是否被选中：当其他数据源处于激活状态时，一个「内部镜像」会用第 3 层 Raw、以内部键自己的穿越门与极限基准历史，重复每一个学习步骤。当内部键就是激活键时只喂一次。
- 「所配置数据源变安静」的相对回退与内部键比较。原先始终被喂的隐藏键 `__layer3_raw_fallback__` 已退役；`InternalSourceKeyMigration` 在加载时一次性把它并入内部键（内部为空 → 采用；两者都有 → 保留内部；隐藏条目删除）。
- 不按数据源分键的共享状态（物理 g 参考、路面支持、Slip Angle 路面分类器）跟随车辆运动与内部数据源，每帧只观测一次。
- 通道的激活身份一旦变化，共享穿越门、共享极限基准与回退偏差都会被清除，上一个数据源的 Lock 锚点过程会在它自己的游戏/车辆下结束（若上一个数据源就是内部键则不结束）。
- Slip 从 Lock 跨通道借用冷启动依据时，只配对同一种数据源类型（`CrossChannelSourcePairing`）：内部 ↔ 内部始终配对，ShakeIt ↔ ShakeIt、Viper ↔ Viper 按类型配对，自定义 / 混合不借用。配对输入是一个不可变、以 Volatile 发布的快照。
- 四行都是我们 Raw 属性（Plain）的自定义通道（`RawSourceFallback.MatchesRaw`）即视为 Plugin Internal。四行恰为旧 ShakeIt 导出属性名的自定义通道会生成 ShakeIt 身份字符串，因而与 ShakeIt 共用同一个键。复现任一出厂预设的自定义通道都不会应用自定义冷启动参考值：当 `RawSourceFallback.MatchesShippedPreset` 与 Raw 身份、规范 ShakeIt 身份（去掉指纹后比较）或出厂 Viper NCalc 预设匹配时，`ApplyCustomColdStart` 会跳过它——因此自定义冷启动 SMax 永远不会进入 Raw、ShakeIt 或 Viper 的键。

### Wheel Lock/Slip Projector：工作原理与设计原因

`ProjectedWheelLockSlipEngine`（第 5 层）是唯一设计给硬件绑定使用的属性层。它存在的目的，是把「这有多严重，数值上」（第 4 层的职责）和「它究竟应该是什么样的体感」（这一层的职责）分开，让驾驶者可以只调整体感，而不必触碰底层的学习机制。

- **`OutputProjector`/`MonotoneCubicCurve`** 实现了一条五锚点曲线（Start/Powerful/Ideal/Max Grip/End——第一个锚点在 v1.0.6.9 重构（`docs/v1068-rework-report.md`）中由「Slightly」更名，前提是 Normalized 30/60 两个锚点已通过验证：接近 30 现在表示一次有力的刹车/油门操作开始——足够但尚未达到理想；保持在 30-60 区间可获得良好效果；保持在 60-80 区间则可获得理想效果）——每个锚点的输入位置和输出强度都可以独立编辑——并用单调三次插值平滑，专门确保输出值不会随着输入值上升而*下降*。一条普通（非单调）的样条曲线可能会在锚点之间发生超调和下凹，驾驶者会把这种下凹感受成「震动器在情况正在变糟的那一刻反而松了劲」；而一条分段线性曲线虽然能避免下凹，但会有明显的折角感。单调三次插值正是那个既能得到平滑曲线、又绝不牺牲「情况变糟时绝不松劲」这一保证的机制。出厂默认的锚点位置（30/60/80/100）已通过数值验证，确保「接近极限」落在 80、「完全抱死/打滑」正好落在 100（`docs/refinements-report.md`）。 已精确重新缩放到 80（`docs/anchor-rescale-report.md`）：`KeyedScaleLearner.CanonicalAtLimitAnchor` 从 75.0 调整为 80.0，使其与这条曲线自身顶部（「最大抓地力」）锚点的输入位置（两个通道出厂 Curve 预设中原本就是 80）完全重合，并修复了置信度斜坡中的一个结构性缺陷，使真正处于极限的读数现在能实际收敛到 80，而不只是名义上的常量；该改动同时把这个锚点从「严重」改名为「最大抓地力」，以准确描述它现在的含义：处于测得的极限，而不是已经超过极限。
- **每个设定点自身的平滑范围**（`ProjectorSettings.SlightlyFlattenRange`/`ModerateFlattenRange`/`CriticalFlattenRange`，默认 3/2/2）。三个命名锚点各自拥有可编辑的半宽；`OutputProjector.AcceptSetpointWithFlatten` 会在「锚点输入 ± 范围」处插入至多两个隐藏控制点，其输出仅朝该侧真实相邻锚点的直线方向偏移 20%（`FlattenBleedFraction`）——这就是把每个锚点处的尖锐拐角变成一小段近乎平坦平台的机制。**范围为 0 时会完全省略这两个隐藏点**，而不是在偏移量为零的位置创建它们——零偏移点并不等价，因为重复/接近重复的 x 值会扰动单调三次拟合自身计算出的切线，即便这些点与锚点重合。每个范围都会被独立限制在到该侧真实相邻锚点距离一半以内，因此即使在极端设置下，两个相邻平台也永远不会交叉或重叠——在出厂的 62/78 理想/最大抓地力阈值下（见下文），理想-最大抓地力间距为 16，因此任一范围一旦超过 8 就会独立限制在 8，使两个平台恰好在中点（70）相遇却永不越过彼此。在 Linear 预设下会完全跳过平滑（该预设必须保持一条精确的直线）。
- **理想/最大抓地力曲线输入阈值从 60/80 移动到 62/78**（与上文的平滑范围 2 配对），使每个平台自身的*边缘*——而非锚点本身——正好落在共享的 60/80 区间边界上：`62 - 2 = 60`，`78 + 2 = 80`。这只是投影层的偏移；Raw、Normalized、每个学习器以及 `KeyedScaleLearner.CanonicalAtLimitAnchor`（80）都不受影响——Normalized 自身的关键点仍然是 60 和 80，相应的耦合测试现在断言的是顶部锚点自身的*平台边缘*（`CriticalInput + CriticalFlattenRange`），而不再是原始阈值本身，与 `CanonicalAtLimitAnchor` 重合。曲线编辑器的标签（「Powerful (30)」「Ideal (60)」「Max Grip (80)」）展示的是这个 Normalized 区间值，而不是「原始值」这一列显示的 62/78 阈值——这是刻意写死的括号数字，并非从阈值字段生成，因此即使可编辑的阈值位于别处，它依然能表达「平台到达该命名区间」这一含义。WheelLock 自身的最大抓地力锚点输出是一个独立配置的数字，不受这次阈值调整影响——**自 1.0.6.0 起该值为 60，而非 80**（详见 docs/release-1060-report.md）；本条目描述的锚点*输入*位置（62/78）不受此影响。
- **可配置的起始/结束输出值**（`ProjectorSettings.StartOutput`/`EndOutput`，默认 0/100）取代了原先写死的数值。两者都是持续的下限/上限，而不是一次性的跳变：任何小于等于 `StartInput` 的输入都会精确读出 `StartOutput`，任何大于等于 `EndInput` 的输入都会精确读出 `EndOutput`。因此非零的 `StartOutput` 会在整个通道启用期间产生一个持续的基础嗡鸣（踏板触发阈值仍然完全控制是否启用该通道），而不仅仅是抬高了斜坡的下限。配置的起始/结束输出值与某个命名锚点自身输出发生冲突时（例如 `StartOutput` 高于第一个锚点，或 `EndOutput` 低于最后一个锚点），都会通过每个控制点本就要经过的同一个非递减限制来解决——绝不会被拒绝或抛出异常，并且四种组合都已记录和测试。冷启动设备体感缩放的振幅除数（`ColdStartScale.ApplyAmplitudeScale`）依然刻意保持绝对值 100，而不会跟随配置的 `EndOutput`——它衡量的是「这次震动相对设备自身绝对 0-100 量程有多大」，而不是驾驶者设定的上限。
- **`PulseGenerator`/`PulseSettings`** 实现了可选的「达到最大值时脉冲」阶段——在 100 与一个可配置的最小值之间交替，而不是持续保持在满值，适合希望持续的抱死/打滑感觉比一成不变的嗡嗡声更紧迫的驾驶者。200 毫秒（5 Hz）的最小半周期间隔由插件本身强制执行，而不仅仅是设置界面上的限制，因此即使手动编辑配置文件，也无法悄悄塞进一个更快的脉冲。

### G-Force：工作原理与设计原因

`GForceEngine`（一个独立子系统，完全不从第 3-5 层派生）借鉴了经典的洗出式/体感线索（motion-cueing）平台的设计思路：它把驾驶者正在承受的*持续*水平，与到达这个水平的*运动过程*区分开来，因为一个用来提示加速度的装置，需要同时表现当前的 g 值，以及它是多快达到这个值的，体感才会真实可信。

- **行程/位置模型。** 刹车和加速被建模为两个相互独立、非负的「行程」信号，各自把一个幅度项（`|G| / maxG`，截断到 [0,1]，代表当前存在多少「能量」）与一个变化率项（G 值上升或下降的速度）结合起来——因此*上升*中的 G 力会比同样大小的*静止*G 力，让体感在其震动垫链条上走得更远；而*下降*中的 G 力则会按下降速度成比例地收回。每个行程信号通过满足单位分割条件（在任意一点之和恰好为 1）的分段线性「帽子」函数，映射到各自的 3 级震动垫链条上（刹车：Back Low → Bottom Rear → Bottom Front；加速：Bottom Rear → Back Low → Back Top）——这正是保持整个扫掠动画连续、行程从 0 移动到 1 的过程中任何一个震动垫都不会出现阶跃变化的原因。幅度项（而非行程/位置项）才是决定总体输出能量的因素，因此真正的 0G 帧，无论行程/位置如何，在每一个震动垫上的输出都恰好为 0。
- **`GForceMaxLearner`/`RobustBandEstimator`** 通过把最近的样本按降序排列，剔除最高的约 5% 作为可能的离群值，再从剩余样本中截取一段大约占剩余部分 10% 的样本池（并保证样本池至少有 10 个宽度），然后把这个样本池自身的最大值与均值按 75%/25% 混合，来学习每款游戏+每辆车各自的最大 G 值（用于 AUTO 模式）——「非常接近样本池中的最大值，但仍然受到均值的影响」。这个过程运行在一个真正的 2 分钟实时滚动窗口上，没有任何最小样本数量的门槛（`TryEstimate` 只在样本数为零时才会失败），并且只有在第二个相近的读数确认之后，高于当前最大值的候选值才会被采纳，因此单次碰撞尖峰绝不会被误判为这辆车真正的峰值。完整规格说明，以及关于这种估计器为何适合 G 力（但在 Normalizer 自己的学习器中，出于各自不同的原因被评估并否决）的实测依据，见 `docs/robust-auto-gforce-report.md`。
- **`GForceShake`** 可选择性地在普通 G 力水平之上叠加一个左右交替的抖动，只要对应一侧的 Wheel Lock/Slip Projected 数值不为零就会生效——抖动的*宽度*会随着车轮当前抱死/打滑的严重程度增大，而它的*中心*始终锚定在普通的 G 力数值上，因此启用这个功能永远不会造成突兀的跳变。这是「G-Force 不依赖第 3-5 层」这一原则唯一的刻意例外——详见 `GForceEngine.Compute` 自己的注释。

  **区间放置。** `band = level × contribution`，`half = band / 2`，波形以该水平为中心。若区间会越出 0-100，则采取**平移而非压缩**，由单一表达式 `effectiveCentre = Clamp(centre, half, 100 − half)` 完成——一个表达式同时覆盖三种情形（恰好放得下、上端越界、下端越界），因此区间的*宽度*始终得以保留。只有当区间比整个量程还宽（`half > 50`）时，任何平移都无法容纳，此时中心固定为 50 并改为截断输出。

  **单一驱动、单一振荡器——两者都至关重要（1.0.8）。** 驱动量始终是 `Math.Max(lockContribution, slipContribution)`，且在任何模式分支*之前*只计算一次；同时全部 8 个震动垫与两路车轮信号共用唯一的 `_shakePhaseSeconds`。因此抱死驱动与打滑驱动的抖动**在构造上**必然同相位——其中一个处于最大值时另一个也必然处于最大值——两者之间的切换只改变区间宽度，绝不改变波形所处的位置。没有任何模式会把抱死路由到一组震动垫、把打滑路由到另一组；表面上的「路由」只是一种衍生现象，因为 `band = level × contribution` 会让水平为 0 的通道保持静默。`GForceEngineShakeModeTests` 对这两条性质均有守护，并已通过变异测试验证（`Max`→`Min`：12 项失败；对单个通道施加 21 ms 相位偏移：2 项失败，其中包括「各震动垫同步摆动」这一守护）。

  **`ShakeApplyMode`（1.0.8）** 只决定各通道的区间*来源*，绝不改变抱死与打滑的合并方式：

  | 模式 | 区间来源 | 行程 | 横向偏置 | 默认强度 / 阀值 |
  | --- | --- | --- | --- | --- |
  | `HigherOfGForceOrLockSlip`（**出厂默认**，列在首位） | 该通道自身的水平，但**上限**由车轮抬高 | 以该水平为中心 | 应用 | 1.2 / 60 |
  | `PerChannel`（即 1.0.8 之前的全部行为） | 该通道自身的水平 | 以该水平为中心 | 应用 | 1.5 / 5 |
  | `AllChannelsGForce` | **当前活跃链条的终端**通道水平，应用于全部 8 个 | 以该水平为中心 | 应用 | 1.3 / 60 |
  | `AllChannelsLockSlip` | `100 × contribution`——完全忽略 G 力 | **从 0 起** | **不**应用 | 1.0 / 30 |

  **阀值为何分三档**（5 / 60 / 30，v1.0.9 修订）：取决于低车轮值在各模式下**做了什么**。在逐通道频带上，一点抱死只是给本就存在的动画增加一点幅度，因此可以很早就放行；在全通道频带上，它是同时垫在八个震动垫下的下限；而在组合模式下，它会抬高**每一个通道的上限**——通向同一种「永久背景嗡鸣」的另一条路，也正是阀值存在的意义。这就是实际体验后把该模式从 5 提到 60、而不是沿用逐通道值的原因。

  `AllChannelsGForce` 按**方向**选择终端通道（刹车→BottomFront，加速→BackTop），而不是按抱死/打滑哪个更大：否则刹车中的打滑会选中 BackTop，而它在刹车时水平为 0，恰恰会在最需要抖动时让它消失。因此该终端通道的输出与 `PerChannel` 完全一致。`AllChannelsLockSlip` 刻意不应用横向偏置，因为该模式的全部意义就在于输出只取决于抱死/打滑。

  **`HigherOfGForceOrLockSlip` 就是 PerChannel 加上一个被抬高的上限（2026-09-07 重新定义）。** 它完全运行 PerChannel 的路径——相同的中心、相同的频带、相同的「两侧都跟随较强一侧」横向规则——只改变一件事：

  ```
  low  = PerChannel 的下限，不变
  high = Min(100, Max(PerChannel 的上限, 100 × contribution))
  ```

  因此最小值始终与 PerChannel 完全一致，只有上限被车轮抬高。它以往是在八个震动垫共享的一个频带上从零起振，那会完全丢弃逐通道的 G 力形状；重新定义的意义在于：车轮安静时保留那套动画并在其上叠加一点幅度，而车轮越过自身极限时则把每个通道的上限都推到 100，此时无论 G 力如何抖动都占主导。`GForceShake.ApplyRange` 就是为此而存在：两端现在来自不同的地方，已无法再用一个中心加一个频带表达。

  **现在只有 `AllChannelsLockSlip` 从零起振。** 它的频带就是车轮数值本身，若采用居中摆动，完全抱死时也只会掩到半强度，读起来是一阵响亮的嗡鸣而不是抖动。其余三种保留居中摆动，因为在那里频带是对既有水平的*调制*，抖动开始时不允许产生跳变。`UsesLockSlipWave` 已相应收紧。

- **波形自 1.0.8 起是「正弦 + 极值停留」（`GForceShake.SineHoldWave`）。** 在停留比例为 `h`、周期恒为 `1/f` 的一个周期内，单个震动垫的归一化位置为：

  | 占周期的比例 | 波形 |
  | --- | --- |
  | `[0, h/4)` | 停留在最大值 |
  | `[h/4, h/4 + (1−h)/2)` | 半余弦，最大 → 最小 |
  | `[…, … + h/2)` | 停留在最小值 |
  | `[…, … + (1−h)/2)` | 半余弦，最小 → 最大 |
  | `[1 − h/4, 1)` | 停留在最大值 |

  其中三点至关重要：
  - **周期恒为 `1/f`。** 停留占用的是周期*内部*的比例，而不是额外追加的时间，因此在任何停留设置下，「10 Hz」始终意味着每秒十次完整的「最大—最小—最大」行程。停留越大只会更**锐利**，绝不会更慢。
  - **两段最大值停留各为半长**（`h/4 + h/4`），而只被经过一次的最小值获得完整的 `h/2`。于是两个极值的总停留时间同为 `h/2`——这正是波形对称、且每个周期都以最大值开始并以最大值结束的原因。
  - **在 `h = 0` 时它就是一条标准余弦**，完全没有平台段。`h` 上限为 90% 而非 100%，因为零长度的爬升沿会形成硬件无法跟随的瞬时方波。

  它取代了此前的梯形波（线性爬升沿）加上一份独立的正交副本。梯形波中那个随 sustain 变化的相位偏移与正交分量都已移除；`SineHoldWave` 是插件中唯一的波形，两个震动垫的区别仅在于**何时**走这条波形。

- **`ShakeFeeling`——成对两侧的关系（1.0.8，`GForceShake.FeelingPair`）。** 同一条波形，三种领先/跟随偏移：

  | 手感 | 偏移 | 停留 |
  | --- | --- | --- |
  | `OppositePhase`（**出厂默认**） | 半个周期 | 按配置值 |
  | `SamePhase` | 无偏移——两侧完全相同 | 按配置值 |
  | `Blending` | 周期的 `h/4`（在其固定停留下即八分之一周期） | **固定为 0.5**，忽略设置 |

  `Blending` 的偏移量正是领先侧自身开头那段最大值停留，因此在相位 0 处，跟随侧恰好位于其下降段的起点，而领先侧仍在保持——两侧都从最高点开始，其中一侧已经开始下降。由于这一特征本身就是**以停留比例定义**的，`Blending` 固定使用自己的停留值（`EffectiveHold`），UI 也随之隐藏该控件，而不是让一个设置项悄无声息地不起作用。

  **出厂默认频率派生自 `DefaultShakeFeeling`**，而非写死，因此全新安装与「恢复默认」都会落在出厂手感自己会选择的那个频率上（当前为 OppositePhase → **5 Hz**）。它曾一度是字面量 5.0，于是全新安装显示「反相」却是 5 Hz，驾驶者一碰下拉框就跳到 10——与两个抖动强度当初完全相同的漂移。

  **选择某个手感会把 `ShakeFrequencyHz` 设为 `DefaultShakeFrequencyFor`**——**`OppositePhase` 为 5 Hz，另两种为 10 Hz**（作者实际体验后修订，最初一版的分配正好相反）。这是作者的明确要求：「set the frequency as 10HZ (Even the user override to their own frequency value) …… if enabled 'Blending' …… set the Shake Frequency as 5HZ instead.」覆盖手动调好的值正是被要求的行为，而非副作用。它只在下拉框的 `SelectionChanged` 中触发，绝不在加载路径中触发，因此全新安装在真正选择过手感之前始终保持出厂的 5 Hz——较早的「默认 10 Hz → 5 Hz」与这条要求因此可以同时成立。

  **停留值并不像频率那样按手感设默认值。** 两种锁相手感共用同一个配置值；`Blending` 根本不接受默认值——它**固定**使用 `BlendingHoldFraction`（0.5），UI 也会隐藏该数值框，因此不存在需要交给驾驶者的按手感停留值。

  **在 G-Force 页签上选择 `ShakeApplyMode` 会把抖动强度和触发值重置为该模式自己的默认值**（作者，**2026-09-07**，`GForceShakeViewModel.Mode` 与 `GForceSettings.DefaultShakeScaleFor` 的注释均引用此日期）。`Ui\ViewModels\GForceShakeViewModel` 的 `Mode` 属性在驾驶者选择时把 `DefaultShakeScaleFor(mode)` 写入车轮抱死、车轮打滑**以及滑移角**三个强度，把 `DefaultShakeTriggerFor(mode)` 写入「抖动起始阈值」；`Load` 加载时绝不触发。选择振感（feeling）同理会改写频率（`DefaultShakeFrequencyFor`），并在 Blending 时隐藏保持值。`GForceSettings.ShakeApplyMode` 属性本身不改动强度，只有页签上的模式选择会（ShakeIt 的 G 力面板没有模式选项）。各模式强度：**1.2**（`HigherOfGForceOrLockSlip`，出厂默认）、**1.5**（`PerChannel`）、**1.3**（`AllChannelsGForce`）、**1.0**（`AllChannelsLockSlip`）；触发值依次为 60 / 5 / 60 / 30。

  历史（理由依然成立，故保留）：2026-09-06 作者曾要求**移除**这一重置，因为 `contribution = scale × wheel/100` 在四种模式中计算方式完全相同（不同的只是它所乘的对象）。2026-09-07 实际体验得出了每种模式各自不同的强度与触发值，而只有在切换模式时应用，才能用上这张按模式的表——因此会再次有意覆盖手动调好的数值。（本文档较早的版本仍描述 2026-09-06 的状态；代码一直遵循 2026-09-07。）

  **`PadRange` 不再应用 `PeakExcursionFactor`。** 「正弦 + 停留」波形在任何手感、任何停留下都完整覆盖 0..1，因此单个震动垫的行程**就是**频带本身。该系数原本存在，是因为正交混合确实会压缩行程——这也正是标称 70 的频带在出厂混合值下实际只摆动 35 的原因。

  从完全静止开始的抖动，仍然会**从四个拐点中距离两侧当前取值最远的那一个起振**，从而主动宣告自己，而不是缓缓淡入。`PhaseForCorner` 通过在整个周期上**搜索 720 个相位**来找到落在该拐点的相位，而不是用闭式公式推导：在三种手感、可变停留与 `reversed` 的组合下，没有任何单一的偏移表达式对所有情形都成立，而且并非每个拐点都可达（`SamePhase` 只可能停在 `BothLow` 或 `BothHigh`），因此该搜索返回的是最接近的可达相位。`ChooseStartCorner` 以**总绝对行程**对四个拐点打分——取绝对值而非带符号差，因为在过弯偏置下振动垫可能落在即将到来的区间*之外*，此时带符号项会变成负数，从而把某个拐点评价得还不如原地不动。打分依据拐点的真实输出值（`CornerOutputs`），且**参考频带与模式相关**：两种由车轮驱动的模式，其频带仅由车轮数值决定，若在这些模式下改用振动垫当前的水平（无 G 力的静默之后为 0），四个拐点会全部塌缩到同一点。

  遍历方向（`reversed`，即交换由哪一侧领先）在相邻两次抖动之间交替，使「停止后又很快重新开始」读作同一段节奏；本次会话的第一次抖动没有可交替的对象，因此由**行程较短**的一侧先动。

- **扫动可以在刹车过程中重新触发，且仅限于快速**上升**（2026-09-06）。** `AdvanceStageProgress` 以往只在链路失活时重置进度，因此在直道上拖着一点刹车就会让扫动跑完，之后每一次重踩看到的进度都已经是 1——作者的反馈是「即使先以很小的减速 G 力持续一段时间，之后的快速减速也不会触发动画」。现在由三条规则控制重新触发：
  - **仅在无动画进行时**（`stageProgress >= 1.0`）。正在进行的扫动绝不会被拦截重启，否则体验上是卡顿而不是一个新事件。
  - **仅在上升时。** 重新触发所用的差值是**带符号**的，因此减速 G 力快速回落（松开刹车）不会触发任何动画。而行程的*速率*仍然使用绝对值，所以松刹时扫动依旧以匹配的速度收尾。
  - **一个自动缩放的阀值**：`RetriggerRatioRatePerSecond = MaxStageProgressPerSecond × RetriggerStrictness`，单位是**比值**/秒而非原始 g。换成绝对量就是 `maxG / 扫动时长 × 严格系数`，正是作者自己的推导：最大 G 力较低的车（刹车更温和、绝对差值更小）只需成比例更小的差值即可获得动画；而高 G 力的车不会被每一次小幅踩刹反复触发。`RetriggerStrictness` 现为**面向驾驶者的设置项**（2026-09-07——这是此处唯一只能靠实际体验定下来的数字），默认 **1.2**，限制在 0.1-5.0。在出厂的 0.2 秒最快扫动下，默认值的含义是「踏板幅度足以在 167 毫秒内走完这辆车的整个刹车区间」。数值越大越严格；下限不取 0，因为严格度为零就意味着阀值为零，任何上升都会重新触发——而这正是该设置要避免的失控。因此 `RetriggerRatioRatePerSecond` 采用即时计算而非存储，这样会话中途按下 Apply 就能在下一帧生效。

  重新触发的扫动还会**重置动画峰值**，否则新动画会沿用旧事件的最高水位，使第二次更温和的踩刹听起来和第一次一样响。

- **起始震动垫会在峰值上停留**（`StartHoldFraction` = 0.25）。作者对旧波形的判断是对的：远端震动垫只在 `p = 0` 的一瞬处于峰值，之后只会下降；而**中间**震动垫是先升至峰值、再从峰值回落——因此中间垫在最大值附近停留的时间大约是开场那个垫的两倍。现在开场关键帧会在扭动的前四分之一被冻结，三关键帧行程则在剩下的四分之三中完成，这既给了远端垫一个真正的平台，也如作者所要求的那样把整个行程略微延后。

- **抖动期间两侧均跟随较强的一侧（`ShakePadPair`）。** 此前横向偏置是在抖动*之后*分别乘到每个振动垫上。在真实的过弯载荷下，这会让两侧的取值区间不再重叠——实测为 75.9-100 对 32.5-51.4，于是较响的一侧**从未改变**：每周期 0 次交换，完全不存在左右交替，只是两个高度不同、幅度不等的独立摆动。改为两侧同取 `level × Max(leftFactor, rightFactor)` 后，每周期恢复为 2 次交换。过弯提示在抖动持续期间被抑制，抖动一结束立即恢复——这是经过权衡后接受的代价：在抓地力极限处，"你正在抱死"比"你正在右转"更重要。

- **横向自 1.0.8 起是摩擦圆，而不再是乘数。** 此前是 `pad × (1 ± gain·bias)`，且以一个任何设置都无法触及的硬编码 `LateralReferenceG`（1.6 g）为基准。两处失效：在重刹下较强的一侧本就已达 100，没有余量可供倾斜，所有的不对称只能靠较弱一侧下降来体现；同时它还破坏了抖动的左右交替（见下文「跟随较强一侧」）。现改为**可叠加的余量**：

  ```
  combined = sqrt(rLong² + rLat²)
  level_ch = rLong × 该通道自身的分级形状        （终端形状 = 1.0）
  boost_ch = (combined − rLong) × 该通道的横向分配
  L / R    = clamp(level_ch ± boost_ch)
  ```

  `GForceLateralFrictionTests` 端到端复现了作者给出的实例：72% 刹车配合 70% 横向 → 合成 100.42%，余量 28.4 → Bottom Front 86.2/57.8、Bottom Rear 57.3/14.7、Back Low 46.4/0。

  六个按通道的分配值**刻意与纵向的侧重相反**——刹车 50/75/100（front/rear/low），加速 100/75/50（rear/low/top）。最响的振动垫承担最少的过弯分量，因此拖刹读作提示向后、向一侧移动，而不是所有通道原地变响。当前链条不驱动的振动垫分配为 0。在完全没有纵向 G 时，采用**上一个活跃链条**的分配（作者决定），使纵向 G 在弯中衰减时提示不会跳变。

  `LateralReferenceG` 与 `LateralBiasGain` 已**移除**，由新的「最大过弯 G」设置与六个分配值取代。三个输出比例（加速/刹车/横向，默认 100%）只作用于各自的分量，绝不作用于 `combined`，因此摩擦圆始终是一个诚实的物理量。

- **学习得到的最大值，以及它们的下限（1.0.8）。** 三个轴共用同一个 `RobustBandEstimator`——8 g 硬性剔除、2 分钟滚动窗口、跳过最高的 5%、取其余部分的接下来 10%（最少 10 个样本）、报告 `0.75 × 池最大值 + 0.25 × 池均值`。**它并非严格的百分位**，但接近 P95：在一份真实的 F1 2025 日志中，原始 p95 为 2.589 g，估计器给出 2.510 g。横向另有一点不同：它没有方向可供门控——加速与减速是共享同一条轴的两个不同最大值，而左转与右转则是同一个抓地力问题——因此它在每一个有效帧上观测 `|latG|` 并保留其中最高者，与车辆当时的纵向动作无关。

  **`MinLearnedAccelMaxG` / `MinLearnedDecelMaxG` / `MinLearnedLatMaxG` = 0.5 g**，而实际生效的下限是 **`Min(该常量, 驾驶者自己填写的 Fixed*MaxG)`**（`FloorFor`）。仅作用于**学习值**——手动填写的 `Fixed*MaxG` 保持原样，而在完全没有证据时固定默认值本就生效。因此下限恰好只覆盖一种情形：证据真实但低得不合理，也就是几乎没有过弯（或没有刹车）的一段会话，否则会学到约 0.1 g，使提示在极轻微的输入下就饱和。

  这个 `Min` 很关键：如果驾驶者填写的值**低于** 0.5，那就等于明确表示这条轴上就是需要这么低的数值，因此下限应当让路，而不是反过来覆盖它。填 1.5 时下限仍为 0.5，学习器收敛到 0.3 也会被托在 0.5；填 0.2 时下限降为 0.2，同一个学习器就可以一路下探到 0.3。

  **AUTO 始终从填写值起步，并向实测值双向逼近。** `MaxRamp` 在第一次调用时以 `Fixed*MaxG` 作为 `_lastPublished` 的种子，随后向学习器的估计值收敛——它比较的是 `|target − lastPublished|`，因此在构造上就与方向无关。填 1.5 而实际最大值为 1.2 就向下走到 1.2；填 0.8 而实际为 2.4 就向上走。小于 25% 的变化立即生效，更大的变化在 2 秒内渐变；已在进行中的渐变只按已用时间继续，而不会重新检查阈值（否则一次正在收敛的渐变会突然跳变）。

  **为什么绝对下限是正确的，而不是一种高估。** 曾有异议认为它会错报真正的低抓地力内容：雪地上的车约 0.4 g，卡车或许只有 0.2 g，以 0.5 为基准归一化会让全力操作读数低于满量程。作者的回答终结了这一争论——**这正是应有的结果。** 真实的卡车本就不会产生强烈的 G 力过渡，座椅震动垫不应假装它会；低于约半个 g，本来就没有值得报告的强力事件。这是相对于「纯粹按车辆归一化」的一次有意取舍：高于下限时，提示表示「已达**这辆车**的极限」；低于下限时，它表示「此处力度不大」。保留三个独立常量（今日取值相同），因为各轴物理特性不同（刹车与过弯受抓地力限制；加速受动力限制，跨度从满载卡车约 0.2 g 到 F1 起步约 1.5 g）。

  **横向上曾尝试过「最小观测阈值」并已移除。** 这段推理值得保留，因为这个想法很有诱惑力：由于没有方向门控，直线行驶帧会撑大样本池，把 5%–14.5% 的排名窗口整体下移，可测量地拉低了估计值（0.15 g 阈值下 2.500 → 2.689 g，+7.6%）。但把阈值从 0 扫到 0.8 g 是**单调且无拐点**的——估计值一路上升（0.8 g 时 +24%），因为剔除底部总会把样本池窗口上移。这说明它是一个决定「参考值坐落在分布中多高」的**手感**旋钮，不存在可推导的正确值，而非降噪滤波器。况且它对输出的影响本就不大（整个范围内左右差异约变动 4 点），且偏向保守：学习到的最大值越高，`rLat` 越小，左右差异反而**更窄**。下限已经处理了真正重要的那种情形，而且远比阈值更容易预期，因此该阈值被直接移除而非继续调参。

  纵向各轴本来也不需要这道阈值：在同一份日志上实测，0.15 g 只让减速提升 +0.7%、加速提升 +0.9%，因为它们的方向门控已经排除了那些空闲帧。

- **为什么纯左右交替无法被感知，以及「混合」为何最终变成了下拉框（1.0.8）。** 最初抖动是 100% 差分的：`L = c + h·w`，`R = c − h·w`，因此 `L + R = 2c`，这一对的**总量**从不变化。两个振动源互不相关，功率相加，体感强度按 `sqrt(L² + R²)` 变化——而纯左右交替几乎不会扰动这个量。实测一段真实数据：左右差值峰峰值摆动 **62** 点，体感强度只摆动 **13** 点，调制深度仅 **23%**；即便在饱和的 100/0 摆动下，理论上限也只有 **29%**。抖动确实存在、幅度很大，却无法被感知。

  第一版修正是一个 **Both-sides blend（双侧混合，%）** 数值框，把频带中比例为 `m` 的部分交给波形的正交副本。它确实有效——在 `m = 0.5` 时体感深度从约 11% 提升到约 40%——但它是**错误的控件形态**：那个滑动条上真正在体感上有区别的只有三个点；该机制还要付出一个把振动垫行程减半的 `PeakExcursionFactor`；而且由于正交分量本身自带停留，配置的停留必须在中段被抵消掉（`EffectiveSustain = 配置值 × 2 × |0.5 − m|`）。

  于是滑动条变成了 `ShakeFeeling` 的三个具名选项，正交分量、行程系数与停留抵消也随之一并消失：同一条 `SineHoldWave` 的两份不同相位副本即可复现全部三种手感，且每一份都完整覆盖整个频带。此处保留对已退役机制的记录，是因为它背后的**实测结论依然成立**——`OppositePhase` 正是出于上述原因而刻意成为三者中最含蓄的一种；感受不到它的驾驶者应当被引导去改用 `SamePhase` 或 `Blending`，而不是一味调大 scale。

  有一处细节在重写后被保留下来：**最终的 L/R 钳位无条件执行**，因为波形运算可能落在 0-100 之外若干个 ULP（曾观测到 `100.00000000000003`）。

  设置项与引擎属性一度比机制本身活得更久，成为**只写状态**——`ApplyTo` 仍在把 `ShakeBlendPercent` 推入 `GForceEngine.ShakeBlend`，却再没有任何地方读回它，因此持久化的混合值实际上毫无作用。两者现已删除；旧的配置文件只是多带一个被忽略的键。

- **Test Effect 效果测试面板（1.0.8；自 2026-11-20 起为 `GForcePageViewModel` 承载的共享 `GForceTestViewModel`）。** 一个可拖动圆球的 G 力控制板与一条抱死/打滑滑条，把合成出来的帧送入设置页自己持有的一个**私有 `GForceEngine` 实例**，使驾驶者无需在赛道上真的制造一次抱死，就能设置好 ShakeIt 的各项强度。

  **它会写入真实属性。** 只驱动私有引擎，只会让界面读数动起来：在没有游戏运行时 `DataUpdate` 会在发布任何内容之前就返回，因此 `QAdvanceFeedback.GForce.*` 读到 null，而 ShakeIt——这个面板存在的全部理由——什么也收不到。`QAdvanceFeedback.PublishTestEffect` 通过实时路径所用的同一个 `PropertyPublisher.UpdateGForce` 写入（其属性是该数组之上的拉取式委托，因此无论有没有游戏在跑，写入都会生效），并置位一个标志让 `DataUpdate` 跳过自己的发布。传入 null 即清除接管；开关关闭与页面卸载都必须这样做，否则关闭后的页面会把输出永久钉死。

  **控制板方位**（作者，2026-09-06）：圆球标记的是载荷被甩向的方向，因此**上方为刹车、下方为加速，向左拖动表示右转**。两个轴在进入遥测帧时都会取反。

  此外还有两条性质是它存在的全部意义，两者均有守护：
  - **本页所有 G 力设置都会实时生效——但写入的是临时对象。** 测试运行在设置页**草稿**中 G 力设置的一次性副本上（含未应用的修改，以及 Wheel Lock 与 Wheel Slip 强度），并只应用到它自己的引擎，从不触碰插件的实时设置。（旧设置页曾在此调用完整的保存方法，把所有尚未提交的修改推入正在运行的插件：一次无人请求的 Apply。）测试运行期间做的修改也会立即生效。隐藏 G-Force 页签会停止测试。
  - **它的任何内容都不会被保存**（作者要求）。其状态只存在于 `GForceTestViewModel` 中，不属于草稿，因此既不会让页面变为「未应用」，也不会被 Apply 写入（`GForcePageViewModelTests.The_test_effect_runs_on_the_unapplied_page_values_and_never_marks_dirty`）。
  - **关闭即彻底无输出。** 开关关闭时控件整体折叠、不渲染任何标签，也不产生任何模拟帧——面板在关闭状态下绝不允许驱动驾驶者的电机。

  抖动区域的**预览图**是同一思路的静态版本：它直接调用 `FeelingPair`，绘制当前设置下两侧震动垫一秒内的动作。两处界面都没有重新实现波形，画出来的就是插件自身会输出的结果。

- **触发门限与抖动节奏（1.0.8，`GForceEngine.AdvanceShake`）。** 四条规则，目的都是让抖动读起来是一段连续的节奏，而不是一个每当车轮数值抖动一下就重新开始的信号：
  1. **阈值**（默认 5，作用于**未经缩放**的 `Max(lock, slip)`，因此它与驾驶者在车轮页签上看到的是同一个数字——调高缩放绝不会让抖动更早触发）。低于该值不会起振；它同时是整个整合功能的**软开关**：此时所有模式都会输出与 `IntegrateWheelLockAndSlip` 为 false 完全一致的结果——纯 G 力、无抖动，两种由车轮驱动的模式也不例外，因为它们已没有可比较的对象。
  2. **单一节奏。** 一旦运行，相位只会前进；车轮数值的变化只改变频带宽度，别无其他。
  3. **走完当前周期。** 掉出阈值后，抖动会把已经开始的那个周期走完。该时长以**显式余数**计算，而**非** `Ceiling(elapsed) × period`——相位是通过反复 `+= dt` 累加得到的，因此当掉落恰好发生在周期边界上时，除法会得到 `5.9999…`，`Ceiling` 随即返回抖动**当前就站在**的那个边界，导致收尾立刻结束，恰恰切断了这条规则本应保全的那一次完整动作。
  4. **在收尾期内重新触发会保持既有节拍。** 只有从完全静止开始的抖动才会重新对齐。

  收尾期内保持的是**最后一次高于阈值**的驱动量，而非当前低于阈值的那个值，从而让这次动作以驾驶者正在感受到的幅度收尾，而不是在结束时萎缩下去。
- **设置面板「Auto detected」（自动检测）读数——过期快照修复。** `SettingsControl.RefreshGForceLearnedText` 过去只在构造时（`LoadFromSettings`）以及 Accel/Decel 模式下拉框的选项真正发生变化时才会被调用——从未有过定时刷新。经过端到端追踪：`GForceSettings.SetCurrentGameAndCar`/`ObserveAccelG`/`ObserveDecelG`/`GetLearnedMax`/`TryGetCurrentAccelAutoDetected`/`TryGetCurrentDecelAutoDetected` 全部使用同一个 `(gameId, carId)` 键，而真正驱动实时 G 力严重度路径的 `EffectiveAccelMaxG`/`EffectiveDecelMaxG` 则是每一帧遥测都重新查询——这条链路从未出现过过期或错配（从 1.0.6.5 到当前版本逐字节比对完全一致）。唯一的问题只是这段设置面板文本是一次性快照：如果驾驶者在开始跑圈前打开了面板（此时正确显示「暂无数据」），并让面板一直保持打开，那么即便之后确实积累了真实证据，这段文字也永远不会更新——这是**纯粹的界面（cosmetic）问题**，从来不是键/通道/学习器的错配，也从未影响任何实际生效的行为路径。修复方式是每秒刷新一次——自 2026-11-20 起为 `GForcePageViewModel` 的 `ITicker` 定时器，只在 G-Force 页签显示时运行（隐藏页签即停止，再次显示时重新启动并立即刷新）。

### Slip Angle（滑移角）：工作原理与设计原因

设计见 `docs\slip-angle-design.md`（含勘察后的修订）。遥测勘察见 `docs\slip-angle-spike-report.md`。实现记录、裁定（R1–R39）与偏离见 `docs\slip-angle-report.md`。本节是英文版对应小节的完整摘要；常量与公式以英文版和源码为准。

**用途。** 柏油路赛车的最快技术，是让轮胎滑移角保持在侧向抓地力峰值对应角度之下。SimHub 自带的「Traction loss」只是整车车身侧滑：没有阈值，不学习，只能报告已经发生的甩尾。Slip Angle 是第三个车轮通道，沿用 Wheel Lock / Wheel Slip 的同一模式：Raw → Normalized → Projected，配有设置页签、原生 ShakeIt 效果和 Test Effect。它的表现是：

- 轮胎进入理想窗口时震动增强；
- 越过极限后达到最强；
- 驾驶者有意保持漂移时逐渐减弱，因此 WRC 赛段或漂移不会全程满强度嗡鸣。

它只有一个输出通道：`SlipAngle.Projected.All`。

**每帧流水线**（`SlipAngleFrameProcessor.Process`，由 `QAdvanceFeedback.SlipAngle.cs` 在 Lock/Slip Projected 之后、G-Force 之前调用），顺序如下，并由测试固定：

1. 观察偏航符号（`YawSignDetector`）；
2. 门控（`SlipAngleGate`）；
3. 选择数据源（原生 / 回退 / 自定义）；
4. `SlipAngleRawCalculator`，使用**上一帧**的 k；
5. `ImpactGuard`；
6. 路面分类；
7. `KeyedSlipAngleLearner.Observe`；
8. 学到的窗口、成熟度与借用档位；
9. 手动关键数据门控与一次性种子；
10. `SlipAngleNormalizer`；
11. `SlipAngleProjectedEngine`；
12. `SaveIfDue`。

处理器与学习器只属于数据线程（学习器的 getter 会改变状态）。UI、CSV 与 ShakeIt 效果读取的，是每帧生成一次的不可变快照。该文件有自己的 try/catch：出错时向 `HealthSubsystems.SlipAngle` 报告并发布空结果，Lock/Slip/G-Force 不受影响。之后连续 300 个正常运行帧会清除该条目（`Core\Health\HealthRecoveryTracker`），偶发一次故障不会留下永久提示。

**数据源分级。**

- **原生**：`SlipAngleRawSources.cs`（项目根目录，属于第 2 层，但不依赖 SimHub，链接编译进测试）按游戏列出四个完整的SimHub属性名（左前、右前、左后、右后，原始索引已重排）、单位与变换。
  - **1.2.1起数据表没有内置行，只由`QAdvanceFeedback.SlipAngleSource.json`构建**（`SlipAngleSourceFile.cs`）。解决方案根目录的副本作为嵌入资源编译进DLL（`SlipAngleSourceCompiled`），在`PluginsData\Common`中的文件缺失时用于创建它，也是页面「恢复默认」的依据。
  - `SlipAngleSourceStore`在Init时以及运行的游戏变化时读取Common中的文件（手动修改无需重启SimHub即可生效；被删除的文件会重新创建），并通过`SlipAngleRawSourceTable.Use`启用解析后的`SlipAngleSourceCatalog`；数据表版本变化时，读取器会丢弃各游戏的缺失/失败判定。
  - 无法解析或含有不完整条目的文件会被整体拒绝且绝不覆盖：本次会话使用自带副本，健康状况（`SlipAngleSourceFile`）说明原因，页面编辑器关闭。
  - `id`指定游戏：`Game1|Game2`、通配符`*` / `?`，或从`regex:`到id末尾的正则表达式（`SlipAngleGameIdPattern`）；精确名称优先于模式，否则使用第一个匹配的条目；只有精确ID的条目表示“没有来源”。保存时，设置与另一条目相同的游戏会加入该条目的id，已被模式以相同设置覆盖的游戏不单独占用条目（`SlipAngleSourceStore.Place`）。
  - 路面：读取器用`surfaceValues`翻译条目的`surface`数值（sealed / loose / lowGrip，`SlipAngleInput.WheelSurface`）；没有时，处理器使用SimHub的`WheelOnLooseSurface*`标志（`SurfaceBucketMap.FromSimHubLoose`：三个以上车轮为松散路面，本次会话见过松散路面后，0-1个车轮才视为柏油）；否则为未知并保持。
  - 每个条目：`id`、`slipAngle`（四个属性名）、可选`unit`（degrees / radians / ratio1 / ratio100；省略为自动检测）、`transform`（direct / atanOfTangent / atan2PatchVelocity）及`longitudinal`、`surface`、`provisional`、`verticalAcceleration`、`note`。保存时保留未知字段。自带的单位即1.2.1之前代码中写明的单位：LFS、Drift21、rF2、LMU、NASCAR系列为弧度；KartKraft为度；Forza游戏为ratio1。代码中不再硬编码任何游戏相关内容（2026-11-24）：配置中的单位列表（`SlipAngleSourceUnits`）初始为空（已移除KartKraft / Forza默认条目，`SettingsHub.SlipAngleHardCodedUnit`即条目的`unit`）；只有条目写明`verticalAcceleration: true`时才读取SimHub的垂直加速度（Rennsport，以及只含该标志的`IRacing`条目）。Slip Angle的自定义来源（`SourceMode` / `CustomSources`、当前来源下拉框）已移除，插件内置是唯一来源。页签上的**恢复此游戏的来源**只对编译副本中有条目的游戏显示（`SlipAngleSourceStore.HasCompiledDefault`），并立即写入文件，无需应用。使用车身角度的原因（`SlipAngleSourceState`）：无来源、检查中、属性没有数据、用户选择了车身角度（不显示警告）。匹配级别（2026-11-24）：精确的单个游戏名、列表中的游戏名、`?`通配符、`*`通配符、正则，然后按文件顺序（`SlipAngleGameIdPattern.Level`）；保存时总会把游戏名写入第一个设置相同的条目。每轮角度的**来源单位**即条目的`unit`（应用时保存）；速度条目始终为弧度，没有`unit`。**此游戏的来源类型**是该游戏在插件配置中的覆盖设置（绝不写入JSON），默认值为JSON中的设置；JSON中注明的单位优先于配置列表。
  - 设置页面的Slip Angle页签编辑当前游戏的条目（插件内置，「有每轮轮胎角度时使用」）：四个SimHub属性名（带属性选择器），由页面的应用写入（`SlipAngleTabViewModel.ApplyTelemetrySource`）。四个属性名设置该条目（新游戏得到不含单位的普通条目，即自动检测），四个全空则移除滑移角来源（有路面路径的条目保留路面路径），一到三个则拒绝保存。只有编译副本中有滑移角条目、且文件中的条目已不同的游戏才显示恢复默认。计算型条目在页面上只读。
  - **角度来源**（`SlipAngleChannelSettings.AngleSource`，Hub字段`SlipAngleAngleSource`）：「始终使用车身角度」时读取器不读取任何游戏的轮胎字段（`SlipAngleRawSourceReader.AlwaysCarAngle`），关键数据按车身角度标识存取，页签与ShakeIt面板上的每轮来源和单位下拉框都会隐藏。
  - 原生直读：ACC、AC Rally、AC EVO、F1 23/24/25、Forza（FM7/FM8/FH4/FH5/FH6）、LFS（正切值）、Wreckfest 2、PMR、Drift21（弧度）、KartKraft、Bus Bound、Rennsport。
  - 原生计算：`atan2(横向接地点速度, |纵向地速|)`，适用于 rFactor 2、LMU，以及暂定的 NASCAR Heat 系列。
- **原生晋升**（`SlipAngleNativeSupport`）：需要 5 秒行驶中（≥ 30 km/h）的有限、合理（≤ 45°）且非恒定的数值；会话内只晋升、不降级。**45° 规则（R14）**：
  - 单位已固定的行，超过 45° 的帧视为中性；
  - 单位未验证的行，只有在单位仍未判定且 |横向 G| < 0.3 g 时，超过 45° 的帧才累计 1 秒封锁。
- **单位自动检测——按「事件投票」（R6→R16）**：
  - 事件：|横向 G| ≥ 0.8 g 连续 ≥ 0.3 s，并在 < 0.5 g 时结束；
  - 一个事件中 ≥ 70% 的帧数值 ≥ 1.0 时，投「度」；
  - 至少 3 个事件、≥ 2/3 一致时作出判定；
  - 未判定时不晋升；
  - 已接受的残余风险（R17）：前三个事件中有两次打转，可能误判一个会话。
- **回退**：只算后轴，`|atan2(Vlat, max(Vfwd, 1 m/s))|`。勘察后删除了 `− b·r` 偏航项（R12），因为各游戏偏航符号不统一。不在原生表中的游戏都使用回退：AC、AMS2、iRacing（未验证）、R3E、EA WRC、GT7 等。
- **自定义**：四个逐轮角度字段。
- **Plugin Internal 始终学习（2026-11-19，`docs/internal-source-learning-design.md`）**：无论选中哪个数据源，Plugin Internal 的原始角度（原生或回退）**每帧**都会计算并在自己的键下学习，选中自定义时也一样。共享的路面分类器每帧只由 Plugin Internal 的角度与内部键的曲线推进一次；自定义只在被选中时、在同一路面混合下、以自己的键学习。只有选中的数据源会发布输出。
- 原生与回退是**不同的数据源身份**，互不共享学习结果。
- **橙色回退警告**（R19）：仅当数据源为 Plugin Internal 且去抖后的档位为 Fallback 时显示。档位为 Unknown 且没有 Raw.All 时，只显示状态文字「此游戏没有滑移角数据」。
- **关键数据的身份档位（`SlipAngleIdentityTier`）**：无游戏 / 暂停 / 菜单会立即把去抖档位清为 Unknown——对警告是对的，但 Unknown 对应 Native 槽位。因此设置中枢、页签和 ShakeIt 面板读写的关键数据由另一个值 `CurrentSlipAngleIdentityTier` 选择：当前游戏本次会话中最后一次「已确认」的档位。它在暂停 / 菜单 / 故障期间保持不变，运行中跟随已确认（去抖后）的档位变化，切换游戏时重置。因此在回退档位的游戏中暂停时所做的修改会写入 Fallback 槽位。

**偏航符号学习（`YawSignDetector`，R12）。** 稳态弯中 `lat G ≈ k·V·r/g`，可学习的只有相对符号 k ∈ {+1, −1}：

- 投票条件：着地，≥ 30 km/h，|lat G| ≥ 0.3 g，|V·r| ≥ 0.1 g；
- 以 3 秒为一个窗口，70% 判定，之后需 80% 才翻转；
- k 未知时，或 |dVlat/dt| > 0.15 g 时，不做偏航一致性检查。

**四轮 → 一个 Raw.All。**

1. 轴角 = 同轴两轮 |α| 的均值；压路肩的车轮排除在外。
2. 前轴乘以k，换算到后轴尺度（2026-11-22，所有者要求；`SlipAngleBalanceEstimator`）：k为车辆接近极限时（按速度段换算的横向G达到过弯第98百分位数的85%以上；需先有120个过弯帧，超过6g的尖峰忽略）后轴与前轴角度之比的中位数，在累计120个此类帧之前向1收缩。学习闸门拒绝的帧（维修区、回放、重新开始、腾空、落地晃动、帧时间异常）、碰撞（轻微或严重）、侧滑（车身角）、超出数据源角度上限的车轴（打转）以及前轴小于0.3°的帧都不参与。按游戏、车辆、数据源和路面分别学习。没有自身k依据的路面，立即以同一游戏、车辆、数据源在其他路面上的k为起点，否则正好为1（2026-11-22，所有者要求；`SlipAngleBorrowRules.KScope`），不做渐变；之后由自身依据接管。不借用其他车辆或游戏。随该路面的All键以单一数值保存，重启后继续学习。限制在 **0.3**–2.0（此前为0.5；F1实测0.29–0.46），路面有自身依据后以每秒0.2做斜率限制。

   借出方无需已确认峰值，只需有自身学习且置信度大于0（2026-11-22；FH6从不确认峰值，因此此前从未借出）；借用的起点 = 出厂值 + 置信度 ×（借出方窗口 − 出厂值），30%置信度只移动30%。在获得自身数据之前，起点随借出方的置信度一起变化；从文件恢复的键保留其保存的窗口。期间指示灯为左右两半（右半为借出方置信度，左半为红色）。

   借用规则集中在`SlipAngleBorrowRules`（2026-11-22）：起始窗口——滑移率数据源可借用同一游戏、同一数据源的任意车辆和路面，角度类数据源（度、弧度、车身角度、自定义）只能借用同一游戏、车辆、数据源的其他路面；k——只能借用同一车辆的其他路面；按速度段的横向G——从不借用，按游戏、车辆、数据源和路面分别学习，每种路面都学习。从不跨游戏借用。游戏读取器都不读取轮胎配方，因此不在规则中。没有前后两轴的数据源（后备模式、只有单轴的自定义数据源）不使用k。在所有者的日志上回放：F1在第一个极限弯内（26.3→27.1秒）达到0.43并保持在0.39–0.49，FH6保持在1.00–1.10。它取代了需要两轴都确认峰值的`Best_rear / Best_front`——F1的后轴在八分钟内只穿越一次，k整节都停在1。
   每根车轴的两个车轮取平均；压在路肩上、无法读取或离地（≤0.05°而另一侧≥2°）的车轮不计入。
3. `Raw.All = Max(α_F′, α_R) × 0.85 + Min(α_F′, α_R) × 0.15`（权重可编辑）。
4. 只有一个轴可用时取该轴（R18）；回退模式下 = α_R。

**门控与着陆抖动。**

- 触发：Any（默认，刹车 ≥ 5% 或油门 ≥ 10%）/ Brake / Throttle。门关闭时输出 0，但漂移状态机继续运行。
- 车速：10→20 km/h 线性淡入；学习需要 ≥ 30 km/h，并通过 `TelemetryLearningGate`；学习**不**要求踩踏板。
- **腾空只看 `IsFlying > 0`（R5）**：赛车游戏从不写入 `FlightIsOnGround`。
- 着陆抖动：`min(1, 飞行时长/MaxFlight) × MaxWobble`，默认 **MaxFlight 1000 ms / MaxWobble 500 ms**（2026-11-19 互换，`docs/slip-angle-design.md` §9.7；200 ms 的飞行现在对应 100 ms 抖动）。期间冻结学习，用 S 曲线淡入，并保持漂移计时清零。
- 小跳跃排除：仅在提供垂直 G 的游戏（iRacing、Rennsport）生效（R7）。

**窗口学习器（`SlipAngleWindowLearner` / `GripCurveBins`）。** 每个上下文有 front / rear / all 三个目标，键为 `game|#|car|#|source/target|#|surface`。

- **抓地曲线**：
  - 0–30° 分成 0.25° 一格；每格保存横向 G 的衰减 P90，前 20 个样本精确播种；
  - **只在被访问时更新**，因此之后的谨慎驾驶不会侵蚀已确认的峰值；
  - Best 与 Min 使用的 peakG 取 **Max 以下**的最高格（R20）；
  - 平台（≥ 97% peak）的中点 = Best；上升段首次达到 90% 处 = Min；缺少数据时 Min = Best × 5/8。
- **Max 只由牵引力穿越教学**：
  - 使用 `CrossingGate`（`.SlipAngle` 参数：上限 45°，上升 0.5°，横向 G 下降 −0.05 g，保持 1 s，无坍塌检查，无饱和车轮保护）；回溯只停在上升段内部，快照不可用的穿越会被拒绝，与 Lock/Slip 相同；
  - **学习到的 Max 上限（2026-11-19）**：原生轮胎滑移角为 20°，车身角回退为 30°（自定义数据源按 20°），适用于每个被教学的 α、快照、先验缩放、发布 / 持久化 / 借用的窗口，以及手动输入的 Max（`SlipAngleWindowLearner.MaxCapDegFor`、`SlipAngleWindow.CappedAt`）。45° 的门上限与合理性边界不变：只有 45° 以内的 α 参与学习，但每个 α 都会被映射，超过 End（1.5 × Max）的值即为 100；
  - 候选条件：|lat G| ≥ 85% peakG，并通过曲线合理性检查（≤ 115%）；
  - 事件流程：检测 → 5 帧后决定佐证 → 教学起始快照 → 保持自然结束才算「完成」；
  - 期间任何被排除的帧都会丢弃该事件，并**回滚**它已教学的内容。
- **佐证与「水平纪元」（R21–R23）**：只有一个更早、**独立**、已完成的事件，同时满足以下条件，才能佐证新事件：
  - 位于容差带内（上调 30% / 下调 15%，下限 0.2°）；
  - 位于当前水平的**同一侧**；
  - 在水平最近一次移动**之后**完成。

  因此单次假穿越既不能抬高也不能拉低 Max，第一次穿越本身也不会教学 Max。
- **峰值确认（锁存）**：需同时满足以下条件，确认后持久保存：
  - ≥ 3 个已完成事件两两相差在 15% 以内；
  - 曲线顶部不再上升；
  - 已存在 Max。

  未确认时，发布的是先验窗口（借用的，否则出厂 5/8/10°）乘以只升不降的系数。
- **穿越门关闭（R29/R30）**：与 Lock/Slip 的开关一致，同样走快照 / 回滚 / 延迟线流程。
- **碰撞保护**：
  - `ImpactGuard`：任一轴 > 8 g 不教学；> 3 g 的跳变作废前 5 帧并封锁 1.5 s；偏航残差 ≤ 0.3 g；
  - 5 帧回溯延迟线；
  - 事件回滚；
  - 曲线合理性检查；
  - 佐证；
  - 这些保护只影响学习，从不影响输出。
- **成熟度** = `clamp01(min(观测/50, 弯道/3))`：
  - 观测 = ≥ 70% peakG 的帧，每秒最多计 10 个；
  - 弯道 = 超过 70% 后又回落到 40% 以下；
  - 成熟度驱动 `ColdStartScale` 与手动关键数据的生效，与 Lock/Slip 相同。
- 窗口质量在未确认时上限为 0.6。学习永不停止。

**路面三分档。** sealed / loose / low-grip。

- 游戏上报的路面只在含义有文档时使用（R13）：rFactor 2 的 `mSurfaceType`，GT7 的 `surfaceType`（仅按名称）；
- 游戏不上报路面时，路面为「未知」，从不猜测（2026-11-22，所有者要求：「NO NEED TO GUESS」），也不切换档位：此类游戏（FH6、F1、AC等）的全部学习都在起始（sealed）档。此前的抓地推断在FH6的柏油路上每2–5秒就在sealed与loose之间来回切换，最终停在low-grip，学习被分散到三个档位：指示灯随新档位变红、数值不断变化、柏油路窗口偏低。载入时会丢弃此类游戏的loose / low-grip档（窗口、k、速度段），下次保存时写回不含它们的文件；
- 1 秒迟滞，1.5 秒混合；学习只进入活动档，发布时按权重混合。

**借用档位、冷参考锁存、持久化的已发布窗口（`KeyedSlipAngleLearner`）。**

- 借用档位通过 `ColdStartTierResolver` 解析：Tier 4 同车异路面 / 3 同游戏异车 / 2 异游戏 / 1 出厂值。
- 借来的窗口同时作为先验和冷参考。本键确认的那一帧，冷参考锁存为刚才发布的窗口，本会话内不再更换。
- 确认后按 `ColdWarmBlend` 混合，权重平滑增长：10 次会话穿越且质量 ≥ 0.5 时达到 1。
- `QAdvanceFeedback.Parameters.json` 新增 `SlipAngleLearners`（`RuntimeDocument` 版本 12），保存每个键最后发布的窗口。没有新驾驶数据时，重启后发布的值完全不变。

**关键数据与归一化。**

- Raw.All 上只有一组 Min/Best/Max，沿用 `KeyDataPointSettings` 的槽位模型；Auto/PerGame 标志**按数据源**分别保存（R24）。
- 范围 0.5–45°，步长 0.1，间隔 ≥ 0.2°，出厂 5/8/10°。
- Auto 下编辑框只读。手动值需满足 `ManualOverrideGate` 才生效（R28）。
- N(α)：(0,0)、(Start,15)、(Min,30)、(Best,60)、(Max,80)、(End=1.5×Max,100) 的单调三次曲线。Nᵤ 在 End 之后按每 0.5×Max 增加 20 点继续延伸，只供漂移状态机使用。
- **起点值（2026-11-19，`docs/slip-angle-design.md` §8）**：`SlipAngleWindow.Start` = ⌊0.5 × Min⌋ 整度，由 Min 推导（不保存、不可编辑）。为 0 时（Min < 2°）**省略**该节点，曲线照旧为 (0,0) → (Min,30)。反函数 `SlipAngleNormalizer.SourceFor` 按同一组节点二分。页签与 ShakeIt 面板显示只读的一行「起点：2°（最小值的 50%）」（`SlipAngleStartText`），或「起点：—（最小值低于 2°）」；页签左图仅在窗口有起点时显示位于 15 的虚线起点标记。

**投影与漂移衰减。**

- 预设：输入 30/60/80，输出 10/20/60，平滑范围 3/6/3（2026-11-22，所有者要求；此前为 30/62/78、10/30/60、3/2/2）；起点输入 **15** → 0（2026-11-19：投影器的起点输入与归一化器的起点节点重合，低于起点的角度投影为 0），End 100 → 100。锚点正好位于学习到的 Best/Max 的映射位置（60/80），因此 Best/Max 在曲线上的读数正好是 20/60，各自位于自身平台的中间。「恢复所有默认设置」和重新选择 Curve 预设都会得到这条曲线；仍保存着旧出厂曲线且未修改的配置会在载入时迁移到新曲线，驾驶者修改过的曲线保持不变。「Linear」预设保留当前起点，因此画出 15 → 100 的直线。
- 顺序：投影 → 成熟度缩放 → 漂移衰减 → 着陆/车速淡入 → 踏板门 → 脉冲。
- `DriftAttenuator` 有 Normal / Drifting / Recovering 三个状态。默认值：Hold 250 ms，稳定范围 20 点，降至 40%，用时 120 ms，γ 2，平滑 50 ms，摆动余量 8 点，确认 80 ms，清除所需油门增量 20 点，清除所需油门开度 90%。
- **作者规则（2026-11-20，`docs/slip-angle-design.md` §9.7）：**
  - **漂移起点**（`SlipAngleDriftStart`）：角度 `Max(1.2 × Max, Min(30°, 1.5 × Max))` 对应的 Nᵤ，不低于 80（Max 15° → 22.5° = 终点）。此前从 Max（Nᵤ 80）开始计数，过于敏感。
  - **锁定输出：** Drifting 中输出 = `Min(factor × calcPeak, calc)`，`calcPeak` 为进入漂移以来的最大 calc；进入 Recovering 时锁定 `locked = factor × calcPeak`，此后输出 = `Min(locked, calc)`。角度回落时不再对下降的数值重复缩放：100 降到 40 后保持 40，直到 calc 低于 40 再跟随 calc。
  - **油门清除：** 漂移标志开启且角度 ≤ Max（平滑 Nᵤ ≤ 80）时，每帧 `ref = Min(ref, 油门)`（首帧以当前油门开始）；当 `油门 > ref + 油门增量` 或 `油门 ≥ 油门开度` 时清除标志。角度回到 Max 以上时保留 ref，清除时丢弃。没有油门读数时不清除；Test Effect 没有油门，因此不使用此规则。
- **升级规则（R25，最终评审）**：保持窗口内的上升量取 `max(年轻期原始上升, 平滑上升)`，超过稳定范围即判为打转，保持满输出。年轻期原始上升只从 EMA「年轻」时（自构造 / Reset / 着陆播种起 < 3τ）采集的样本开始计算：刚播种的 EMA 会滞后于打转的起始而低估它。EMA 热身后只看平滑上升，因此搓板路、路肩带来的逐帧原始噪声不会阻止已保持的漂移被识别。τ = 0 时原始与平滑是同一序列。
- Drifting 状态没有「升级退出」（R26）。dt 上限为 `min(0.1 s, HoldSeconds / 2)`（即使最短 0.05 s 的保持也需要两帧）。
- 间隙：`SlipAngleFrameProcessor.NoteGap`（无游戏 / 暂停 / 菜单）会重置漂移衰减器，与 `Configure` 和切换游戏一致。
- 已知后果：在默认参数下，比约 80 点/秒更慢的打转被当作漂移（作者待办）。

**发布属性、ShakeIt 效果、测试。**

- `SlipAngle.Raw.All`（度，不可用时为 null）、`SlipAngle.Normalized.All`、`SlipAngle.Projected.All`，外加追加在末尾的 33 个 `Diag.SlipAngle.*` 诊断属性。
- `QAdvanceFeedbackSlipAngleContainer`：类型名冻结；显示名「Slip Angle」（R37）；单一输出；默认增益 75%（R38）；40/25 Hz；超过 500 ms 的帧输出 0。
- Test Effect：一个 0 → 3×Max 的滑条（宽 264 px，以适应 ShakeIt 面板），中点有「100%」刻度。它运行真实的归一化、投影与漂移衰减，漂移时圆点从 Max 缓降（R39）。测试期间 Raw/Normalized 读数为 0 或 null。

**G-Force 集成。**

- `IntegrateSlipAngle` 默认开启，旧配置文件缺少该字段时也按开启加载（R31）。`WheelSlipAngleShakeScale` 默认 `DefaultShakeScaleFor(mode)`。
- 门控：`(锁死/打滑开启 && Max(lock, slip) ≥ T) || (滑移角开启 && angle ≥ T)`。
- 驱动：各已开启贡献（值 × 各自强度）中的最大值。
- 下游的模式、波形、手感、收尾与重新触发都不变。G-Force 测试新增滑移角滑条。

### 共享核心（行为保持不变地提取）

| 核心 | 提取自 | Lock/Slip | Slip Angle | 证明 |
|---|---|---|---|---|
| `CrossingGate` + `CrossingGateParameters` | `SlipCrossingGate` / `LockCrossingGate`（现为保存出厂参数的薄包装） | `.Slip` / `.Lock` | `.SlipAngle` | `CrossingGateEquivalenceTests`：每个包装 200 种子 × 3000 帧 + 阈值边界流，对照 `Frozen\FrozenSlipCrossingGate` / `FrozenLockCrossingGate` |
| `CorroboratedLevelEstimator` + `CorroborationParameters` | `GripLearner` 的自适应峰值逻辑 | `GripLearnerDefaults` | Max（度） | `CorroboratedLevelEstimatorEquivalenceTests`：200 种子 × 5000 值，对照 `FrozenGripLearnerCorroboration` |
| `ColdStartTierResolver` | `KeyedScaleLearner` 的借用档位扫描 | `KeyedScaleLearner.ResolveTier` | `KeyedSlipAngleLearner` | `ColdStartTierResolverEquivalenceTests`：300 种子 × 600 次操作，对照 `FrozenColdStartTierResolution` |
| `ImpactGuard` + `ImpactGuardParameters` | 新增 | 未接入（有 `Disabled`） | `.SlipAngle` | `ImpactGuardTests`（没有旧代码可冻结） |

`QAdvanceFeedback.Tests\Frozen\` 中的冻结副本是刻意保留的测试基准（R1）。所有原有的 Lock/Slip 测试未经修改全部通过。

### Lock/Slip 新增：测试分组滑条与回退警告

- **分组滑条**（`TestGroupSliders`、`KeyDataNormalizationInverse`）：
  - Mono 显示「单通道」，FrontRear 显示「前/后通道」，LeftRight 显示「左/右通道」，Corners 不显示；四个车轮滑条始终保留；
  - 分组值由计算得到：`N⁻¹(Aggregate(N(各轮)).<组>)`，`N⁻¹` 用二分法求能达到目标的最小源值；
  - 圆点 x = 分组值，y = 由四轮计算的投影，因此总是落在曲线上（属性测试保证）。
- **回退警告**（`BranchNativeness`、`NativenessBroadcaster`、`FallbackWarningViewModel`）：
  - 选中的 Raw 分支若是车速/转速估算，在 Wheel Lock/Slip 页签和对应 ShakeIt 面板显示橙色提示；
  - 仅 Plugin Internal 数据源显示；
  - 0.5 秒去抖；
  - 没有游戏运行时隐藏。

## SimHub 依赖型 vs. 纯净/可测试型

这条边界正是让本插件几乎整体都能在不启动 SimHub 进程的情况下完成单元测试的关键：

- **SimHub 依赖型**（引用了 `SimHub.Plugins`/`GameReaderCommon`，无法在真实 SimHub 宿主之外构造）：`QAdvanceFeedback.cs`（插件的组合根）、`SimHubTelemetryAdapter.cs`、`PropertyPickerLauncher.cs`、`SimHubScriptEditor.cs`、`SimHubExpressionEvaluator.cs`、`WheelSourceResolver.cs`、`QAdvanceFeedback.SlipAngle.cs` / `QAdvanceFeedback.Clutch.cs`（组合根的 partial）、`ViperAvailabilityProvider.cs`、`Integration\ShakeIt\` 下的全部文件、`Settings/SettingsControl.xaml(.cs)`（薄宿主）、`QAdvanceFeedback.SettingsPage.cs` 与 `Settings/NumericEditor.xaml(.cs)`。`Ui\Views\` 中的 WPF 视图不引用 SimHub 类型，但因为是 WPF，net8 测试同样不编译它们。
- **纯净/可测试型**（纯 C#，完全不引用 SimHub）：`Core\` 下的一切（第 1/3/4/5 层、G 力、Slip Angle、离合器），`Ui\Mvvm\` 与 `Ui\ViewModels\`、`SlipAngleRawSources.cs`，加上设置 POCO 和 `ConfigStore`/`RuntimeStore`（它们把日志记录抽象为普通的 `Action<string>` 委托，而不是直接引用 SimHub 的日志对象）。

测试项目（`QAdvanceFeedback.Tests`）通过把这些纯净文件直接作为源码链接编译进一个零 SimHub 包引用的 net8.0 程序集来强制这一点，而不是引用已构建好的 net48 插件 DLL——一旦有 SimHub 依赖悄悄混入了本应保持纯净的文件，测试构建会立即失败，而不只是一个没人检查的运行时假设。

## 逐文件地图

已于 2026-11-19 对照实际目录核实（分支 `feature/slip-angle`）。`QAdvanceFeedback\` 下的每个 `.cs` / `.xaml` 文件都列在下面。

### `QAdvanceFeedback\`（组合根、第 2 层、SimHub 相关辅助类）

| 文件 | 层 | 用途 |
|---|---|---|
| `QAdvanceFeedback.cs` | 组合根 | `IPlugin`/`IDataPlugin`/`IWPFSettingsV2` 入口，每帧把各层串起来。 |
| `QAdvanceFeedback.SettingsPage.cs` | 组合根（partial） | 设置页：唯一的 `SettingsPageViewModel`，在第一次 `GetWPFSettingsControl` 时创建，在 `End()` 中释放；SimHub 每次请求的视图都是绑定到它的薄 `SettingsControl`。`SettingsPageStateChanged` 取代了插件对页面的所有推送调用。 |
| `QAdvanceFeedback.SlipAngle.cs` | 组合根（partial） | Slip Angle 组合：读取第 2 层输入，每帧调用一次 `SlipAngleFrameProcessor`（Lock/Slip Projected 之后、G-Force 之前），发布结果、维护 UI 快照、去抖档位（警告）与身份档位（关键数据槽位）、持久化学习器；自带 try/catch。 |
| `QAdvanceFeedback.Clutch.cs` | 组合根（partial） | 离合器结合点：解析离合器输入、发布 `Clutch.Bite.Output`、学习结合点。 |
| `SimHubTelemetryAdapter.cs` | 2 | 把 SimHub 的 `GameData` 映射为第 1 层的 `TelemetrySample`，捕获原始诊断快照，并提供 Slip Angle 所需的车身输入。 |
| `ITelemetryAdapter.cs` | 2（契约） | `SimHubTelemetryAdapter` 实现的接口。 |
| `SlipAngleRawSources.cs` | 2（纯净） | 按游戏的滑移角 raw 数据表（路径、单位、变换、raw 路面路径，只由来源文件构建）及 `SlipAngleRawSourceReader`；不依赖 SimHub，链接编译进测试。 |
| `SlipAngleSourceFile.cs` | 2（纯净） | `QAdvanceFeedback.SlipAngleSource.json`（1.2.1）：条目/文档类型、经校验的`SlipAngleSourceCatalog`、编译进DLL的副本（`SlipAngleSourceCompiled`）及`SlipAngleSourceStore`（创建、游戏变化时重新读取、页面保存、无效文件回退）。只依赖Newtonsoft，链接编译进测试。 |
| `KnownSourceColdStartReference.cs` | 第 4 层支持 | 依据组合 `SourceIdentity` 判断本插件对哪种输入源有冷启动实测依据。 |
| `ViperAvailabilityProvider.cs` / `ViperStore.cs` | 设置相关 / 持久化 | viper4gh CalcLngWheelSlip 插件的实时检测（故障记入健康子系统 `ViperAvailability`）；`QAdvanceFeedback.Viper.json` 读写。 |
| `WheelSourceResolver.cs` | 第 4 层输入 | 把数据源字段（属性、JavaScript 或 NCalc）解析为实时 0-100 读数。ShakeIt 内置数据源从不经过它（读 tap，或按通道回退到 Raw）。 |
| `PropertyPickerLauncher.cs` / `SimHubScriptEditor.cs` / `SimHubExpressionEvaluator.cs` | 设置相关 | 属性选择器、脚本编辑器与表达式求值的 SimHub 反射辅助类。 |
| `PropertyPublisher.cs` | 发布边界 | 注册所有发布的 SimHub 属性（依赖 SimHub 的一半）。 |
| `PropertyPublisher.State.cs` | 发布边界 | 不依赖 SimHub 的一半：所有字段、`Update*`（含 `UpdateSlipAngle`）、快照与 CSV 行；链接编译进测试。 |
| `CsvExportWriter.cs` | 诊断 | 开启「导出会话为 CSV」时写出所有发布属性。 |
| `ConfigStore.cs` / `RuntimeStore.cs` | 持久化 | 设置与学习状态的 JSON 读写（含 `SaveSlipAngleLearners`）。 |
| `ShakeItCalibrationJson.cs` / `ShakeItPrecalibrationConverter.cs` | 持久化 | 第 3 层 ShakeIt 标定类型的短名序列化规则；SimHub 预标定的一次性导入。 |

### `QAdvanceFeedback\Core\`（第 1 层 + 共享基础组件）

| 文件 | 层 | 用途 |
|---|---|---|
| `ITelemetryFrame.cs` / `TelemetryFrame.cs` | 1 | 与游戏无关的一帧遥测（Slip Angle 新增：前向局部速度、偏航率、腾空、路肩、垂直 G、路面抓地提示）。 |
| `ITelemetrySample.cs` / `TelemetrySample.cs` | 1 | 当前帧 + 上一帧 + 经过时间。 |
| `Corners.cs` | 共享 | 四轮数值结构，固定 FL/FR/RL/RR 顺序。 |
| `ClampMath.cs` / `MathHelpers.cs` | 共享 | 发布边界的限幅与安全转换；第 3 层公式的映射辅助。 |
| `AggregationWeights.cs` / `Aggregator.cs` / `WheelAggregate.cs` | 共享 | 把四个车轮值合成为 Front/Rear/Left/Right/All 的轴/侧混合。 |
| `ILegacyWheelLockSlipEngine.cs` / `LegacyWheelLockSlipResult.cs` / `WheelLegacyResult.cs` / `LegacyThresholds.cs` | 3（契约） | `RawCalculatorEngine` 的公开契约与踏板阈值。 |
| `WheelSlipBranchNames.cs` / `WheelSlipBranchSelector.cs` | 3（选择） | 分支名称常量与纯能力优先级选择器。 |
| `BranchNativeness.cs` | 3（分类） | 选中的 Lock/Slip 分支是原生信号、车速/转速回退估算还是未知——驱动橙色回退警告。 |
| `NativenessBroadcaster.cs` | 发布边界 | 发布 Lock/Slip 原生性，稳定 0.5 秒后才通知；无游戏时立即清除。 |
| `RawWheelTelemetrySnapshot.cs` / `RawWheelTelemetryBuilder.cs` | 2/3 边界 | 逐轮原始遥测与能力快照，及其 null 与零的区分逻辑。 |
| `IValueDistributionLearner.cs` / `OnlineDistributionLearner.cs` | 3/4 | 学习器契约；按数据源缩放标定用的流式均值/方差学习器。 |
| `PublishedPropertyNames.cs` / `AllPublishedProperties.cs` | 发布边界 | 所有发布属性名（Slip Angle 的追加在末尾）。 |
| `TelemetryLearningGate.cs` | 4/G 力/Slip Angle | 共享的「此帧能否作为学习证据」门（维修区/回放/重启）。 |
| `AccelerationUnits.cs` | 2 | m/s² 与 G 的换算。 |
| `RobustBandEstimator.cs` | 共享（G 力） | `GForceMaxLearner` 使用的鲁棒样本池估计器。 |
| `ColdWarmBlend.cs` | 共享 | 按分散度加权的冷/热混合，供 `GripLearner`、`KeyedScaleLearner` 与 Slip Angle 发布窗口使用。 |
| `KeyedTelemetrySupport.cs` | 2/3 边界 | 按游戏、凭持续证据晋升 `WheelOnLooseSurfaceFrontLeft` 支持，永不降级。 |

### `QAdvanceFeedback\Core\RawCalculator\`（第 3 层）与 `Calibration\`（ShakeIt 标定移植，1.0.7.0）

| 文件 | 用途 |
|---|---|
| `RawCalculatorEngine.cs` | 每帧调度并持有所有有状态学习器/滤波器。 |
| `BrakeSpeedSlipModel.cs` / `BrakingVsSpeedModel.cs` / `DispatchBranchFormulas.cs` / `WheelRotationLockFilter.cs` / `StreamingPercentileLearner.cs` | 各分支公式与滤波器（详见英文版）。 |
| `Calibration\ICalibrationData.cs` / `CalibrationData.cs` / `PreloadedCalibrationData.cs` / `CalibrationDataProvider.cs` / `GameCalibrationBounds.cs` / `TimeMovingAverage.cs` | SimHub 标定机制的忠实移植（详见英文版）。 |

### `QAdvanceFeedback\Core\Normalized\`（第 4 层与共享核心）

| 文件 | 用途 |
|---|---|
| `NormalizedWheelLockSlipEngine.cs` / `NormalizedWheelLockSlipResult.cs` | 第 4 层引擎及结果。 |
| `CrossingGate.cs` | **共享核心**：参数化的穿越门（`.Lock` / `.Slip` / `.SlipAngle`，`TeachingWeightMode`），窗口的开启已证明与提取前的门逐位一致；自 2026-11-19 起被教学的数值有意不同（回溯停在上升段内、无实时回退、Lock/Slip 的 `LearnCeiling` 为 95），见 `docs\slip-smax-crossing-gate-design.md` §10。 |
| `SlipCrossingGate.cs` / `LockCrossingGate.cs` | `CrossingGate` 的薄包装；其公开常量是权威来源（`CrossingGateParameters.LockShipped` / `SlipShipped` 由它们构建）；仍为两个类，便于单独关闭一侧。 |
| `CorroboratedLevelEstimator.cs` | **共享核心**：从 `GripLearner` 提取的证据加权、需佐证的水平估计器；Lock/Slip 用 g，Slip Angle 的 Max 用度。 |
| `ColdStartTierResolver.cs` / `ColdStartTier.cs` | **共享核心**：从 `KeyedScaleLearner` 提取的借用档位解析器；档位枚举与 `ColdStartTierFloors`。 |
| `ImpactGuard.cs` | **共享核心（新增）**：碰撞/接触保护；Slip Angle 启用，Lock/Slip 未接入。 |
| `GripLearner.cs` / `KeyedGripLearner.cs` / `GripLearnerKeyMigration.cs` | 相对本车学习的峰值参考及键迁移。 |
| `KeyedScaleLearner.cs` / `LockAnchorLearner.cs` | 按数据源缩放标定（Slip 的跨通道借用经解析器 `SetCrossChannelLockIdentityResolver` 决定）；仅 Wheel Lock 的 S75/S90 锚点学习。 |
| `PluginInternalSourceIdentity.cs` | 每个通道的内部（Plugin Internal）键，等于 `RawSourceFallback.RawIdentity`；引擎每帧都喂它。 |
| `InternalSourceKeyMigration.cs` | 纯净的一次性加载迁移：把已退役的 `__layer3_raw_fallback__` 键并入内部键（缩放、锚点、抓地）。 |
| `CrossChannelSourcePairing.cs` | 纯净：Slip 键可以从哪个 Lock 键借用冷启动依据（仅同类型数据源）。 |
| `ManualOverrideGate.cs` | 手动关键数据的生效门（30 秒行驶 + 交接置信度 ≥ 0.95），Slip Angle 原样复用。 |
| `SourceIdentity.cs` / `SurfaceLooseFraction.cs` / `LongitudinalDirectionResolver.cs` / `AchievedMotion.cs` | 数据源身份、Lock/Slip 松散路面权重、纵向方向、诊断用 G 值分级。 |

### `QAdvanceFeedback\Core\SlipAngle\`（Slip Angle 栈）

| 文件 | 用途 |
|---|---|
| `SlipAngleInput.cs` / `SlipAngleUnit.cs` | 一帧 raw 输入（null 表示不可用）；单位枚举。 |
| `SlipAngleNativeSupport.cs` | 原生晋升、45° 封锁、按事件投票的单位检测。 |
| `YawSignDetector.cs` | 学习每款游戏的相对偏航符号 k。 |
| `SlipAngleGate.cs` | 触发方式与阈值、车速淡入、腾空、着陆抖动、小跳跃排除。 |
| `SlipAngleRawCalculator.cs` / `SlipAngleRawResult.cs` | 逐轮 → 轴均值 → 前轴 × k → Max/Min 混合为 Raw.All；车身侧滑回退。 |
| `GripCurveBins.cs` | 0.25° 分格的衰减 P90 抓地曲线、平台/Best/Min 与上升检查。 |
| `SlipAngleWindow.cs` | 以度为单位的 Min/Best/Max 窗口（End = 1.5 × Max），推导出的起点（⌊0.5 × Min⌋，Min < 2° 时为空），出厂 5/8/10。 |
| `SlipAngleStartText.cs` | 页签与面板上只读的「起点：N°（最小值的 50%）」/「起点：—（最小值低于 2°）」一行。 |
| `SlipAngleWindowLearner.cs` | 单键学习器：穿越教学 Max、佐证与水平纪元、延迟线与回滚、峰值确认、成熟度、窗口质量。 |
| `KeyedSlipAngleLearner.cs` | 按 (游戏, 车, 源/目标, 路面) 的学习器集合、借用、冷参考锁存、热权重、k、持久化。 |
| `SurfaceBucketClassifier.cs` | 三档路面：只使用游戏上报的路面（rF2/GT7）；否则为未知，从不猜测或切换。切换车辆与切换游戏一样会重新开始（2026-11-22）。 |
| `SlipAngleNormalizer.cs` | N(α) 与 Nᵤ(α)，含 (Start, 15) 节点；反函数 `SourceFor`。 |
| `SlipAngleDriftStart.cs` | 漂移起点角 `Max(1.2 × Max, Min(30°, 1.5 × Max))` 及其 Nᵤ（不低于 80）。 |
| `ISlipAngleKeyData.cs` | 处理器所需的关键数据接口（Core 不依赖 Settings）。 |
| `SlipAngleFrameProcessor.cs` | 纯净的每帧流水线。 |
| `SlipAngleIdentityTier.cs` | 选择关键数据身份（Native / Fallback 槽位）的档位：当前游戏本次会话中最后一次已确认的档位，暂停 / 菜单时保持，切换游戏时重置。 |
| `SlipAngleKeyDataModel.cs` | Slip Angle 关键数据的唯一显示规则（与 Lock/Slip 相同）：完全相同键的封顶窗口（`LearnedWindow`）、默认值、输入框显示什么（`Shown`）、写入所显示的值（`TrySeed`、`SlipAngleDisplayRuleKeyData`），以及显示上下文（`SlipAngleKeyDataContext`，持久化最后的游戏/车/档位，用于重启后的显示）。 |
| `SlipAngleFrameResult.cs` / `SlipAngleStatus.cs` | 每帧不可变结果；UI 状态。 |
| `SlipAnglePublishedNames.cs` | 三个产品属性与 33 个诊断属性（CSV 顺序）。 |

### `QAdvanceFeedback\Core\Projection\`（第 5 层）

| 文件 | 用途 |
|---|---|
| `ProjectedWheelLockSlipEngine.cs` / `ProjectedWheelLockSlipResult.cs` | 第 5 层引擎及结果。 |
| `OutputProjector.cs` / `MonotoneCubicCurve.cs` / `PiecewiseCurve.cs` / `ProjectorSettings.cs` / `ProjectorAnchorEditor.cs` | 可编辑曲线及其设置/编辑辅助（新增 `ProjectionChannel.SlipAngle` 预设）。 |
| `PulseGenerator.cs` / `PulseSettings.cs` | 可选的「最大值时脉冲」。 |
| `ColdStartScale.cs` | 冷启动体感缩放，Lock/Slip 与 Slip Angle 共用。 |
| `SlipAngleProjectedEngine.cs` | Slip Angle 第 5 层顺序：投影 → 成熟度缩放 → 漂移 → 淡入 → 踏板门 → 脉冲。 |
| `DriftAttenuator.cs` / `DriftSettings.cs` | 漂移衰减状态机及其参数。 |

### `QAdvanceFeedback\Core\GForce\`、`Clutch\`、`ShakeItEffects\`、`Viper\`、`Health\`、`MotorsExport\`、`Localization\`、`Runtime\`

| 文件 | 用途 |
|---|---|
| `GForce\GForceEngine.cs` / `GForceOutput.cs` / `GForcePublishedNames.cs` / `GForceMaxLearner.cs` / `GForceShake.cs` / `ShakeFeeling.cs` / `ShakeApplyMode.cs` | G 力引擎（新增 `IntegrateSlipAngle`、`WheelSlipAngleShakeScale`）、最大值学习、抖动、手感与模式。 |
| `Clutch\ClutchBiteEngine.cs` / `ClutchBiteCurve.cs` / `ClutchBiteDetector.cs` / `ClutchBiteLearner.cs` / `ClutchLearningParameters.cs` | 离合器结合点。 |
| `ShakeItEffects\NativeEffects.cs` / `ToneDefaults.cs` / `EffectRouting.cs` / `FrameSnapshot.cs` / `SlipAngleEffectOutput.cs` / `GraphPoint.cs` / `TestAwareOutputGate.cs` / `TestEffectSession.cs` / `ShakeItTapPropertyNames.cs` / `ShakeItTapValues.cs` | 原生 ShakeIt 效果中不依赖 SimHub 的部分：效果清单与默认增益（Lock/Slip/Slip Angle 75%）、音调默认值、路由、500 ms 过期规则、Slip Angle 输出、测试会话、SimHub 自带抱死/打滑值（含按通道的游戏支持与游戏 id）。 |
| `ShakeItEffects\ShakeItSourceAvailability.cs` | 按通道的 ShakeIt 数据源状态机（NotSetUp / SetUpIdle / Supported / NotSupported）、0.5 秒广播器，以及可测试的配置文件检查逻辑（`IShakeItProfileReader`、`ShakeItProfileInspection`）。 |
| `ShakeItEffects\ShakeItSourceResolution.cs` | 纯净的按通道路由（Configured / ShakeIt 内置 / Raw 回退）、一帧的数值与身份、规范 ShakeIt 身份。 |
| `ShakeItEffects\ShakeItAlgorithm.cs` | ShakeIt 算法默认值、阈值 → 过滤器映射、指纹（`#alg=`）、静音模拟、不可变输入/快照。 |
| `Viper\ViperAvailabilityResolver.cs` / `ViperDocument.cs` / `ViperPropertyNames.cs` / `ViperSupportedGames.cs` | viper4gh 数据源支持。 |
| `Health\HealthRegistry.cs` / `HealthEntry.cs` / `HealthSeverity.cs` / `HealthSubsystems.cs` / `SafeCall.cs` | 健康状态注册表。子系统：`ScriptEditor`、`PropertyPicker`、`ExpressionEvaluator`、`ViperAvailability`（2026-11-19 由 `ShakeItExport` 更名）、`ShakeItEffects`、`ShakeItProfile`（2026-11-19 新增）、`ClutchBite`、`CapabilityDetection`、`ConfigPersistence`、`RuntimePersistence`、`CsvExport`、`BackgroundFlush`、`TelemetryAdapter`、`PropertyPublish`、`Init`、`Shutdown`、`SettingsUi`、`SlipAngleSource`、`SlipAngle`。 |
| `Health\HealthRecoveryTracker.cs` | 连续 N 个正常帧后清除某子系统的条目（Slip Angle：300）。 |
| `MotorsExport\MotorsExportPropertyNames.cs` | 旧 ShakeIt Motors 导出属性名的格式。已不再读取：它用于构建规范 ShakeIt 数据源身份并提供 `WheelSuffixes`。（`MotorsExportAvailabilityResolver.cs` 已于 2026-11-19 随导出路径一并移除。） |
| `Localization\Strings.cs` / `StringTableEn.cs` / `StringTableZhHans.cs` | 中英文字符串表；由 `LocalizationTests` 保证一致。 |
| `Localization\StringTable{En,ZhHans}.Areas.cs` 及 `.Page` / `.LockSlip` / `.Curve` / `.GForce` / `.General` / `.Panels` / `.SlipAngle.cs` | 按区域拆分的 partial 字符串文件（每个区域的键放在自己的文件中，两种语言同一区域）；`.Areas.cs` 汇总添加。 |
| `Runtime\RuntimeDocument.cs` / `RuntimeCache.cs` / `AtomicFile.cs` | 学习状态文档（版本 12 新增 `SlipAngleLearners`）、缓存、原子写入。自 2026-11-19 起保存快照也保留抓地学习器的 `RaiseCandidateG/Hits`、`LowerCandidateG/Hits` 与 `QuietStreak`。重启显示上下文（新增字段）：`LastKeyDataGame/Car`（Lock/Slip）与 `LastSlipAngleGame/Car/Tier`（Slip Angle）。 |

### `QAdvanceFeedback\Settings\`

| 文件 | 用途 |
|---|---|
| `QAdvanceFeedbackSettings.cs` | 根设置对象（Lock/Slip/SlipAngle/GForce/Clutch/General）。 |
| `WheelChannelSettings.cs` / `WheelSourceSet.cs` | Lock/Slip 单通道设置（含 `ShakeItAlgorithm`、`KeepPresetSourcesWhileShadowed`）；按数据源模式归档。 |
| `ShakeItAlgorithmSettings.cs` | ShakeIt 算法区块的持久化设置（Lock：旧版算法、灵敏度、仅抱死；Slip：旧版算法、静音）及 `Snapshot`。 |
| `SlipAngleChannelSettings.cs` / `SlipAngleKeyDataSettings.cs` | Slip Angle 通道设置；按槽位保存的 Min/Best/Max，按数据源的 Auto/PerGame 标志。 |
| `GForceSettings.cs` / `GForceReadout.cs` / `GForceTestFrame.cs` | G 力设置（按模式的 `DefaultShakeScaleFor`/`DefaultShakeTriggerFor`）、读数标签、测试帧（含滑移角滑条）。 |
| `ClutchBiteSettings.cs` / `GeneralSettings.cs` | 离合器与常规设置。 |
| `SourceMode.cs` / `ScriptType.cs` / `SourceButtonMode.cs` / `ShakeItSourceOptions.cs` / `RawSourceFallback.cs` / `DefaultWheelSources.cs` | 数据源相关枚举与选项（ShakeIt 仅在已设置或被选中时提供）、Raw 回退（`RawIdentity`）、`MatchesRaw`（自定义行等于 Raw 的行）、`MatchesShippedPreset`（身份等于 Raw、ShakeIt（去掉指纹）或出厂 Viper 预设；这样的自定义通道不应用自定义冷启动）、默认源文本。 |
| `KeyDataPointSettings.cs` / `KeyDataPointDefaults.cs` / `KeyDataPanelModel.cs` | Lock/Slip 关键数据点。 |
| `KeyDataNormalization.cs` / `KeyDataNormalizationInverse.cs` | 关键数据归一化及其二分法反函数 `N⁻¹`。 |
| `LockSlipTestEngine.cs` / `TestBalls.cs` / `TestGroupSliders.cs` / `SlipAngleTestEngine.cs` | Lock/Slip 与 Slip Angle 测试计算、圆点位置、分组滑条。 |
| `SourceToProjectedGraph.cs` / `SlipAngleSourceToProjectedGraph.cs` | 「源值 → 映射值」曲线（Slip Angle：Raw.All 0 → End，单位度）。 |
| `SettingsHub.cs` / `ConfigFileWatcher.cs` | 页面与 ShakeIt 面板的双向同步（含五个 ShakeIt 算法字段；车轮页签会在运行时 `SourceAvailability` 广播时重新采样）；`SwitchSource`——集线器与设置页「数据源」下拉框共用的唯一数据源切换；磁盘配置监视。 |
| `NumericEditor.xaml(.cs)` | 数值编辑控件。 |
| `SettingsControl.xaml` / `SettingsControl.xaml.cs` | `GetWPFSettingsControl` 返回的控件：内容为 `Ui\Views\SettingsPageView`（`x:Name="Page"`）的薄宿主，代码后置恰好是 `InitializeComponent(); DataContext = viewModel;`（MvvmRuleTests）。旧的 3,651 行代码后置及其 partial（`.Sync`、`.GForceTest`、`.GForceShake`、`.SlipAngle`、`.Clutch`、`.ShakeItSource`）、`ApplyDirtyState.cs` 与 `PageFieldRefreshPlan.cs` 已于 2026-11-20 删除（`docs\full-mvvm-report.md`）。 |

### `QAdvanceFeedback\Ui\`（MVVM）与 `Integration\ShakeIt\`

| 文件 | 用途 |
|---|---|
| `Ui\Mvvm\ObservableObject.cs` / `RelayCommand.cs` / `IUiDispatcher.cs` / `ITicker.cs` / `LeadingColumns.cs` / `IActivatable.cs` | MVVM 基础组件；视图出现/消失时的 `Activate`/`Deactivate`。 |
| `Ui\ViewModels\SettingsDraft.cs` | 设置页的草稿 / 基线 / 实时模型，按 JSON 叶子合并（`IsDirty`、`AdoptExternal`——ShakeIt 优先——、`Apply`——只写入编辑过的叶子、原地写入——、`ReplaceDraft`、带变更路径的 `Reloaded`）。 |
| `Ui\ViewModels\SettingsPageViewModel.cs` / `SettingsPageContext.cs` / `PageSectionViewModel.cs` | 设置页：草稿、贯穿整个生命周期的集线器订阅、Apply / 全部恢复默认、未应用状态与横幅、各页签视图模型；各区块共享的上下文（草稿、宿主、调度器、定时器、对话框、剪贴板、`Guard` / `ReportFailure`）；区块基类。 |
| `Ui\ViewModels\ISettingsPageHost.cs` / `IWheelChannelPageHost.cs` / `IKeyDataPageHost.cs` / `IGeneralPageHost.cs` / `PageServices.cs` | 设置页对插件的需求：`ISettingsPageHost`（`ApplySettings`、`CreateDefaultSettings`、`PluginStateChanged`、`LogError`）、可选的区域宿主接口与组合接口 `IPluginSettingsPageHost`；`IDialogService`、`IClipboard`（以及未使用的 `IPropertyPicker` / `IScriptEditor`）。 |
| `Ui\ViewModels\WheelChannelPageViewModel.cs` / `TriggerThresholdViewModel.cs` / `SourcesViewModel.cs` / `SourceRowViewModel.cs` / `CustomReferenceViewModel.cs` / `AggregationViewModel.cs` / `KeyDataPageViewModel.cs` / `CurveEditorViewModel.cs` / `PulseViewModel.cs` | Wheel Lock / Wheel Slip 页签（同一个类，`IsLock`）：触发阈值、数据源（唯一切换路径、Raw 遮蔽、Viper 游戏按钮、ShakeIt 状态与算法区块）、四行数据源、Custom 冷启动参考、车轮组合、关键数据（面板规则、700 ms 防抖立即保存、从不经过 Apply）、曲线编辑器与两张图的数据、脉冲。 |
| `Ui\ViewModels\GForcePageViewModel.cs` / `GForcePageAxisViewModel.cs` / `GForcePageShakePreview.cs` / `GeneralViewModel.cs` / `HealthListViewModel.cs` | G-Force 页签（最大 G 行与读数——显示时每秒刷新——、承载的抖动区块、预览数据、承载的测试）；General 页签及其健康列表。 |
| `Ui\ViewModels\IPanelHost.cs` / `ITestEffectHost.cs` / `PanelViewModelBase.cs` / `FallbackWarningViewModel.cs` | 面板/测试 VM 与插件的接口；橙色回退警告。 |
| `Ui\ViewModels\ShakeItSourceViewModels.cs` | `IShakeItSourceStateHost`（可选的宿主能力）、`ShakeItSourceUi`（纯净的列表/标签/提示规则）、`ShakeItSourceStatusViewModel`、`ShakeItAlgorithmViewModel`。 |
| `Ui\ViewModels\WheelChannelEffectViewModel.cs` / `KeyDataViewModel.cs` / `LockSlipTestViewModel.cs` | Wheel Lock/Slip 面板、关键数据、测试（分组滑条）。 |
| `Ui\ViewModels\SlipAngleTabViewModel.cs` / `ProjectorEditorViewModel.cs` / `SlipAngleEffectViewModel.cs` / `SlipAngleTestViewModel.cs` | Slip Angle 设置页签、锚点行、ShakeIt 面板与测试。 |
| `Ui\ViewModels\GForceEffectViewModel.cs` / `GForceTestViewModel.cs` / `ClutchViewModels.cs` | G 力面板与测试；离合器。 |
| `Ui\ViewModels\GForceShakeViewModel.cs` | G-Force 页签的抖动区块：两个 Integrate 开关、共享设置及其启用状态、三个强度、选模式时的强度/触发值重置、选振感时的频率改写；与 `GForceSettings` 互相加载/保存。 |
| `Ui\Views\*.xaml(.cs)` 及 `ClutchGraph.cs` / `NormalizedCurveGraph.cs` / `GraphFurniture.cs` / `TestControls.cs` / `TrackAlignment.cs` / `LabelColumnSync.cs` / `WpfInfrastructure.cs` | 视图（`WheelChannelEffectView`、`GraphView`、`SlipAngleTabView`、`SlipAngleEffectView`、`SlipAngleGraphView`、`GForceEffectView`、`GForceTestView`、`ClutchBiteView`）、图形控件与纯布局行为。 |
| `Ui\Views\ViewBehaviors.cs` / `WpfPageServices.cs` / `SettingsPageView.xaml(.cs)` / `WheelChannelPageView.xaml(.cs)` / `WheelChannelKeyDataView.xaml(.cs)` / `WheelChannelCurveView.xaml(.cs)` / `WheelChannelPulseView.xaml(.cs)` / `GForcePageView.xaml(.cs)` / `GForcePageShakePreviewGraph.cs` / `GeneralPageView.xaml(.cs)` | 附加行为 `ViewModelLifetime.Activate`、`EventCommand.Loaded` / `DropDownOpened`；WPF 版 `IDialogService` / `IClipboard`；设置页外壳（`MainTabs`、横幅、Apply / 恢复）；车轮页签（一个视图用两次）及其关键数据、曲线、脉冲部分；G-Force 页签及抖动预览图；General 页签。 |
| `Integration\ShakeIt\PluginPageHost.cs` | 设置页眼中的运行中插件（`IPluginSettingsPageHost`）：转发到插件自身的成员（数据源阻断、Viper 文档、学习值显示规则、手动门控、Apply、ShakeIt 导入、健康、版本），并持有 SimHub 属性选择器 / 脚本编辑器 / 求值器。 |
| `Integration\ShakeIt\QAdvanceFeedbackWheelChannelContainers.cs` / `QAdvanceProjectionFilter.cs` / `QAdvanceFeedbackSlipAngleContainer.cs` / `QAdvanceFeedbackGForceContainers.cs` / `QAdvanceFeedbackClutchBiteContainer.cs` / `QAdvanceFeedbackEffect.cs` / `ShakeItEffectSupport.cs` / `ShakeItBuiltInTap.cs` / `PluginPanelHost.cs` / `ShakeItProfileInspector.cs` | 原生 ShakeIt 效果中依赖 SimHub 的部分（`ShakeItBuiltInTap`：心跳、游戏支持、按算法快照配置隐藏容器、静音模拟；`PluginPanelHost` 实现 `IShakeItSourceStateHost`；`ShakeItProfileInspector`：在 UI 线程以 1 Hz 读取两个 ShakeIt 插件的当前配置文件，用于「已设置」检查）。 |

### `QAdvanceFeedback.Tests\Frozen\`（测试基准）

| 文件 | 用途 |
|---|---|
| `FrozenSlipCrossingGate.cs` / `FrozenLockCrossingGate.cs` / `FrozenGripLearnerCorroboration.cs` / `FrozenColdStartTierResolution.cs` | 提取前出厂代码的逐字副本；等价性测试要求共享核心与之逐位一致。不得为通过测试而修改。 |

## UI：MVVM（长期规则）

作者的全项目规则（2026-11-17），适用于所有 UI：插件设置页与原生 ShakeIt 效果面板。英文版 `docs\architecture.md` 同名章节为完整说明。

- **视图模型持有状态与规则**（`Ui\Mvvm\ObservableObject`、`RelayCommand`），**视图绑定到它们**。
- **代码后置只做接线**：恰好是 `InitializeComponent(); DataContext = viewModel;`。生命周期与少数没有可绑定属性的事件由 XAML 中声明的附加行为处理（`Ui\Views\ViewBehaviors.cs`）：`v:ViewModelLifetime.Activate="True"`（Loaded -> `Activate`，Unloaded -> `Deactivate`）、`v:EventCommand.Loaded` / `DropDownOpened`。XAML 中不写事件处理特性。`MvvmRuleTests` 对所有视图（含 `Settings\SettingsControl.xaml(.cs)`）强制执行。
- **线程与时间**：视图模型只在 UI 线程上改动绑定状态（`IUiDispatcher`）；定时器与当前时间来自 `ITicker`。

**插件设置页已完全遵循此规则（2026-11-20，`docs\full-mvvm-plan.md`，报告 `docs\full-mvvm-report.md`）。**

- **视图模型树**：`SettingsPageViewModel`（每个插件一个：第一次 `GetWPFSettingsControl` 时创建，`End()` 时释放；持有 `SettingsDraft`、集线器订阅、Apply / 恢复、未应用状态与横幅）下有 `LockTab` / `SlipTab`（`WheelChannelPageViewModel`）、`SlipAngleTab`、`GForceTab`、`ClutchTab`、`GeneralTab`。
- **草稿 / 应用模型（`SettingsDraft`）**：实时 = 插件的设置对象；草稿 = 页面编辑的深拷贝；基线 = 草稿上次同步时的实时状态，按 JSON 叶子比较。未应用 = 草稿与基线有任一叶子不同。任何来源的集线器事件以及显示页面时，都把实时与基线不同的叶子同时写入草稿与基线——**ShakeIt 一侧的修改优先并立即显示**，即使覆盖同一字段的未应用修改；其他未应用修改保留。Apply 只把页面上编辑过的叶子原地写入实时设置，然后由插件 `ApplySettings()` 应用、保存并只广播一次。即时生效的内容（关键数据、Viper 游戏列表、ShakeIt 导入、离合器学习参数、Slip Angle 关键数据）直接写入实时设置，草稿随后采纳，页面保持「已应用」。订阅与页面视图模型同寿，因此页面首次显示之前或隐藏期间的修改，打开时就已在页面上。
- **宿主与服务**：视图模型从不接触 SimHub 或 WPF，经 `ISettingsPageHost` 访问插件，并按需转换为可选的区域接口；插件通过组合接口 `IPluginSettingsPageHost`（`Integration\ShakeIt\PluginPageHost`）一次实现全部。插件不再向页面推送，而是触发 `SettingsPageStateChanged`（换游戏、1 Hz 学习值读数、关键数据写入、ShakeIt / 原生性变化），视图模型按与 ShakeIt 面板相同的显示规则重新读取。对话框与剪贴板为 `IDialogService` / `IClipboard`；页面操作失败时写入 SimHub 日志、记入健康注册表，并立即显示在 General 页签上。
- **按页签的生命周期**：页签的订阅与定时器只在页签显示时运行，显示时补齐：车轮页签的插件状态 / 可用性监听与 1 秒回退警告、G-Force 读数定时器（隐藏页签同时停止测试）、General 页签的健康列表重读。
- **字符串**：每个区域的 UI 字符串放在自己的 partial 字符串文件中（`Core\Localization\StringTable{En,ZhHans}.<Area>.cs`）。

## 设置截图采集规则（长期规则）

`docs\images\settings-*.png` 与 `docs\images\shakeit-*.png`（在两个 README 中被引用）由仓库内的 `tools\screenshot-harness\` 渲染（不属于 `QAdvanceFeedback.sln`；构建/运行步骤见其 `ScreenshotHarness.csproj` 头部注释）——它加载已构建好的 `QAdvanceFeedback.dll`，像 SimHub 一样向插件请求设置页（`GetWPFSettingsControl`：绑定到 `Ui\Views\SettingsPageView` 的薄 `SettingsControl`，`x:Name="Page"`），并按标签页渲染为 PNG；选中标签页后先运行调度器 600 ms，使 MahApps ToggleSwitch 的滑块（加载后由绑定设置）停止动画；`--out <目录>` 把对比用的一组图片写到别处而非 `docs\images`。Apply/Restore 按钮行是 `SettingsPageView.xaml` 中 `MainTabs` 的一个 `DockPanel.Dock="Bottom"` 同级元素——它位于 `TabControl` 之外，因此无论当前选中哪个标签页，逐标签页的截图都不会包含它。

按标签页划分的采集规则（未来每次重新生成截图都应遵循，无需再次提醒）：

- **Wheel Lock、Wheel Slip、Slip Angle、G-Force、Clutch**——这些标签页内容较长。只截取当前选中 `TabItem` 的内容（其 `ScrollViewer` 的内容元素），不包含上方的标签条和下方的按钮行，使整个标签页的设置内容都能完整地放进一张图片，不被裁切。
- **General**——足够短，即使包含外层框架也不会丢失内容。改为截取完整的 `SettingsControl`：标签条、General 标签页的内容，以及 Apply/Restore 按钮行都包含在内。
- **原生 ShakeIt 效果**——`shakeit-wheellock|wheelslip|gforce|clutch|slipangle.png`（另有 `shakeit-wheellock-test`：Front/Rear 路由下运行的 Lock 测试，显示分组滑条；以及 `shakeit-clutch-test`），各自带 `.zh-Hans.png` 中文版本（`Strings.Culture` = zh-Hans），由 `tools\screenshot-harness\ShakeItEffectCaptures.cs` 渲染：效果面板，加上 WheelLock/WheelSlip/Slip Angle 在 ShakeIt 响应曲线位置的只读图，四周 12 px 留白。

在这两种情况下，都要按渲染目标自身的完整自然尺寸进行测量/排布（`Measure` 时高度设为 `PositiveInfinity`，随后在得到的 `DesiredSize` 上进行一次显式的 `Arrange`），而不是采用预览窗口宿主恰好施加的高度——如果跳过这个显式的重新 `Arrange` 步骤，`ScrollViewer` 的视口会裁切较长的内容，而 `DockPanel` 中负责填充的子元素又会拉伸以填满一个过大的宿主窗口，导致按钮行上方出现一段空白。

输出文件名（注意是 `settings-gforce.png`，而不是 `settings-g-force.png`——采集工具是从标签页标题文字推导文件名的，G-Force 这一个需要单独改名以匹配 README 中的链接）：`settings-wheel-lock.png`、`settings-wheel-slip.png`、`settings-slip-angle.png`、`settings-gforce.png`、`settings-clutch.png`、`settings-general.png`。

建立这条规则那次工作的完整依据、验证证据和像素尺寸：`docs\screenshot-capture-rule.md`。

## 韧性模型与健康状态注册表（长期规则）

本插件作为第三方，与所有其他已启用的插件、ShakeIt 以及各种仪表盘共享同一个存活的 SimHub 进程——我们自己代码中的一次故障绝不能传播到 SimHub 自身的调度流程或其他插件中去。关于哪些 SimHub 入口点在设计上是/不是异常安全的完整反编译证据，见 `docs\pipeline-exception-safety-report.md`；本小节是这方面持久有效的总结，以及把降级状态呈现给驾驶者、而不是让它悄无声息地发生的健康状态注册表设计。

**已加固的边界，端到端：**

- 每一个 `IPlugin`/`IDataPlugin`/`IWPFSettingsV2` 入口点（`Init`、`DataUpdate`、`End`、`GetWPFSettingsControl`）都被包裹在自己的顶层 try/catch 中，每种不同的故障只记录一次日志（绝不按帧记录），并且从不重新抛出异常——`Init` 尤其重要，因为 SimHub 自身的 `EnablePlugin`（延迟/手动启用路径）调用它时完全没有自己的 try/catch（已通过反编译确认）。
- 每一个发布到 SimHub 的属性（`PropertyPublisher.Register` 中的 `AttachDelegate` 调用）都通过 `PropertyPublisher.AttachSafe` -> `Core.Health.SafeCall.Value` 进行包裹，因此某个取值逻辑抛出异常时，只会让那一个属性降级为 SimHub 自身的「无值」，而不会传播到正在读取它的任何仪表盘/ShakeIt 效果/其他插件——`PropertyEntry.Evaluate()`/`PropertyEntryWrapper.GetValue()` 本身就是不受保护的 SimHub 基础机制（已反编译确认），因此本插件不能指望 SimHub 会替它捕获一个抛出异常的取值逻辑。
- 每一个对未文档化 SimHub 内部机制的反射包装（`SimHubScriptEditor`、`PropertyPickerLauncher`、`SimHubExpressionEvaluator`）都只解析一次、缓存结果，并且一旦失败就在本次会话剩余时间内永久降级为「不可用」——绝不会在下一帧/下一次点击时重试并再次抛出异常。`SimHubTelemetryAdapter.CaptureRawTelemetry` 自己对 `GetFeedbackCapabilities` 的调用（这是一个真实的 API，不是反射，但同样依赖一个未文档化的 SimHub 形状）也采用相同方式加固。
- 所有文件 I/O（`ConfigStore`、`RuntimeStore`、`CsvExportWriter`）在文件缺失、损坏、被锁定或权限不足时，都会降级为使用默认值/停止记录，而不是抛出异常。
- `RuntimeStore` 后台刷新用的 `Timer` 回调（`FlushTick`）是这里最危险的一个类：在 .NET Framework 下，直接发生在这个原始线程池线程上的未处理异常可能会终止整个 SimHub 进程。它（以及现在同样在该线程之外的 `Task.Run` 中独立运行的 `WriteAtomic`）都被一个宽泛的、兜底的 `catch (Exception)` 完整包裹。
- 设置界面的构造由 `GetWPFSettingsControl` 自身的防护覆盖；此后的每个页面操作（Apply、Restore all defaults、关键数据重置、脚本编辑器/属性选择器的操作按钮、集线器驱动的重新加载）都在 `SettingsPageContext.Guard` 中运行——完整保留旧页面 `SafeUiAction` 的行为：写一行 SimHub 日志（`ISettingsPageHost.LogError`）、记录一条 Degraded 级的 `SettingsUi` 健康条目并刷新健康列表——因为在构造完成之后很久才被调用的绑定或命令，其上游本来不会有任何东西去捕获异常。
- 异常的遥测数据（NaN/无穷大/负值或过大的 `dt`、null 的 `GameData`/`NewData`/`OldData`、缺失车辆或游戏 id）在两端都受到防护：`DataUpdate` 自己的 null/状态检查会在到达 Core 之前就短路处理，而每一个 Core 引擎也都独立对自己的输入做有限性检查（见 `AbsentTelemetryTests`/`DtNormalizationTests`/`ClampMathTests` 等测试）——单独任何一道防护本身就足以防止异常抛出，因此这是刻意的双重保险，而不是一个单点故障。

**健康状态注册表（`QAdvanceFeedback.Core.Health`）：** 一个小型、纯净、不依赖 SimHub 的注册表（`HealthRegistry`、`HealthEntry`、`HealthSeverity`、`HealthSubsystems`），上面所有被加固的边界都会从各自的 catch 块内向它上报——绝不主动上报，也绝不在每一帧都宣称「一切正常」，这正是让「注册表中完全没有记录」成为健康状态的原因。每一条记录都包含子系统名称、严重程度（`Degraded`/`Failed`）、一个用于生成简短的、驾驶者可读的「这对你意味着什么」提示的本地化键（在显示时通过 `Strings.Get` 解析，绝不会写死成英文）、原始异常详情（供提交问题报告使用，刻意不做本地化）、首次发生的时间，以及可能的根本原因是否是 SimHub 更新移动/重命名/改变了本插件所依赖的某个东西（`IsSimHubCompatibilityIssue`）——这是车主特别要求要被明确点名、而不是笼统显示为一个不透明失败的唯一情形。再次上报同一个子系统（例如某个取值逻辑每一帧都在抛出异常）只会更新已有那条记录的时间戳/发生次数，而不会让注册表不断增长——这正是即使在持续故障下，也能维持「只记录一次日志，而不是按帧记录」的原因。

**设置界面呈现方式（General 标签页，「Plugin health」分组）：** 当 `HealthRegistry.Snapshot()` 为空时，只显示一行且不显眼的提示（「All systems normal - nothing to report.」），因此在正常情况下不会带来任何视觉干扰。否则，每个降级的子系统都会显示一行加粗的警告——一个驾驶者可读的子系统名称加上它的影响说明，`Degraded` 用橙色，`Failed` 用暗红色——并且对于任何被标记为 SimHub 兼容性问题的记录，还会附加一句通俗易懂的提示「此功能需要针对你的 SimHub 版本进行更新」，而不是显示一段原始异常信息。一个「Copy details for a bug report」按钮（仅在确有内容可报告时显示）会把每条记录的技术细节（子系统、严重程度、时间戳、发生次数、异常文本）复制到剪贴板，方便车主粘贴到问题反馈中。这个列表在设置页视图模型创建时读取，每次显示 General 页签时重读，并在任何页面操作失败之后（`SettingsPageContext.ActionFailed`）再次刷新，因此点击操作过程中发生的故障无需重新打开标签页就能立刻反映出来（`GeneralViewModel` / `HealthListViewModel`）。

**已知的未加固路径，明确说明：** SimHub 自身的 `PluginManager.GetPropertyValue`，以及最终到达 `PropertyEntryWrapper.GetValue()` 的 NCalc/公式引擎调用链，已通过反编译确认本身就是异常安全/实践中安全的（见 pipeline-exception-safety 报告）——本插件既不会也不能去修补 SimHub 自身的基础机制。如果某个其他调用方（另一个插件、ShakeIt 自身的内部逻辑）在没有经过 SimHub 自身包装的情况下，直接调用了 `PropertyEntry.Evaluate()`/`PropertyEntryWrapper.GetValue()`，那仍然是真正未加固的——这超出了本插件的能力范围，本文档也不声称已经修复了这一点。

## 「Private」曾经所在的位置

`Core\RawCalculator\` 下的一切，加上 `SimHubTelemetryAdapter.cs`，曾经存放在一个被隐藏、被 git 忽略的 `Private\` 文件夹中，位于两个项目之外，并由一个基于反射的工厂（`AlgorithmFactory`/`PrivateTypeResolver`）在运行时解析它们，在其缺失时回退到惰性桩实现（`InertTelemetryAdapter`/`InertLegacyWheelLockSlipEngine`）。这种拆分方式，以及背后的整套机制，现在已经不存在了——完整的历史和理由见 `docs\clean-room-restructure-report.md`。

## 模拟宿主（`tools\mock-host`）

一个独立的WPF程序（net48），不需要SimHub即可运行真实插件。与`tools\screenshot-harness`不同，它属于`QAdvanceFeedback.sln`，并排在第一位，因此Visual Studio按F5时会启动并调试它。插件自身的DLL构建不变；模拟宿主只额外生成它自己的exe。它有四个页签：
- **Plugin Settings**：`GetWPFSettingsControl`。
- **ShakeIt Effects**：用**Add Effect**添加八个原生效果，显示每个效果自己的`EditControl`和图表，并保存到`PluginsData\Common\MockHost.ShakeItProfile.json`。
- **Game Simulation**：游戏ID、车辆、踏板、速度、G值、偏航、每个车轮的速度 / 滑移 / 路面标志、游戏报告的能力，以及任意SimHub属性。这些数据以60 Hz在后台线程送入`DataUpdate`。
- **Language**：插件页面和效果编辑器使用英文或简体中文（立即重建；页面唯一的视图模型会被释放并重新创建）。

插件文件写入`<exe目录>\PluginsData\Common`（SimHub的相对存储路径）。

SimHub的`PluginManager`和ShakeIt容器的构造函数需要只有SimHub才有的DLL，因此创建时不运行构造函数。`MockGameManager`提供`FeedbackCapabilities`。Init中的ShakeIt配置检查器需要GongSolutions.WPF.DragDrop，模拟宿主改用一个空的检查器来完成这一步。

`MockHost.exe --capture <目录> [秒数]`会把每个页签及每个已添加的效果保存为PNG。

完整说明、文件和限制见`docs\mock-host.md`。
