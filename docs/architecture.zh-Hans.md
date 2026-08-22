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
| [**G-Force**](#g-force工作原理与设计原因) | 一套「持续 G 力水平」与「由变化率驱动的瞬态」相分离的洗出式模型，通过满足单位分割条件的分段线性函数映射到一条 3 级震动垫链条上；每款游戏/每辆车各自的最大值通过一个修剪样本池的鲁棒估计器，在一个实时滚动窗口内学习得到；车轮抱死/打滑可选择性地在其上叠加左右交替抖动。 | 让座椅震动垫获得连续、有方向感的刹车/加速/过弯载荷体验，与车轮通道相互独立又彼此互补。 |

## 分层模型

遥测数据依次经过五层，再加上两个独立子系统（G 力，以及设置/持久化）。每一层都发布自己的一层属性（第 1-2 层除外，它们是内部层），并且只依赖它下方的层——绝不横向依赖，也绝不向上依赖。

### 第 1 层——遥测接口

**功能：** 定义与具体游戏无关的一帧遥测数据形状（`ITelemetryFrame`/`TelemetryFrame`）和一个采样（`ITelemetrySample`/`TelemetrySample`，当前帧 + 上一帧 + 经过的时间）。每一项读数都可以独立为 null，这一点非常关键：null 的含义是「这款游戏没有提供这个数值」，绝不是真实的零值。

**禁止依赖：** 任何东西。这是整个技术栈的最底层——没有 SimHub 类型，也不依赖其他任何层的类型。

### 第 2 层——SimHub 适配器

**功能：** 唯一允许知道 SimHub 自身类型名称（`GameData`/`StatusDataBase`/`FeedbackData`/`PluginManager`/`FeedbackCapabilities`）的地方。把 SimHub 的实时遥测数据映射到第 1 层的形状（`SimHubTelemetryAdapter`），并单独捕获一份逐轮原始诊断快照（`RawWheelTelemetrySnapshot`，由能力标志位把关，确保某款游戏的真实零值不会被误判为「不支持这个通道」——见 `RawWheelTelemetryBuilder`）。

**禁止依赖：** 不依赖它上方的任何东西。它可以引用 SimHub/GameReaderCommon 的类型（它是唯一被允许这样做的地方），但第 3 层及以上永远不会反过来引用 SimHub 类型。

### 第 3 层——Raw 计算器

**功能：** `QAdvanceFeedback.Core.RawCalculator`——把一个遥测采样转换成发布的 `WheelLock.Raw.*`/`WheelSlip.Raw.*` 属性。`WheelSlipBranchSelector`（位于 `Core` 而非 `RawCalculator`，因为它只是对公开能力标志位的纯布尔优先级判断，不涉及任何具体公式）决定某款游戏支持哪种信号形态；`RawCalculatorEngine` 负责调度到匹配的公式，并持有每一个有状态的学习器。按设计未归一化——这里的「40」在不同车上含义不同，这个问题在上一层才会被修正。

**禁止依赖：** SimHub 类型（由测试项目强制执行——它把这个文件夹直接链接编译进一个不引用任何 SimHub 包的纯 net8.0 程序集，一旦有 SimHub 依赖悄悄混进来，测试构建会立即失败），以及第 4/5 层、G 力或设置层的任何内容。

### 第 4 层——Normalized（归一化）

**功能：** `QAdvanceFeedback.Core.Normalized`——把第 3 层的逐轮数值形状，与仅通过车速/油门/刹车/G 力学习到的、相对于本车的严重程度（`GripLearner`/`KeyedGripLearner`，按游戏+车辆+数据源分别建键，并有一套按路面和按数据源缩放的扩展）结合起来，从而让发布的区间在一辆经常拉 4G 的街机风格赛车和一辆经常拉 1.2G 的模拟风格赛车中含义相同。

**禁止依赖：** SimHub 类型，或第 5 层/G 力。可以依赖第 3 层的输出形状（`Corners`、`LegacyWheelLockSlipResult`）和第 1 层（`ITelemetrySample`）。

### 第 5 层——Projected（映射）

**功能：** `QAdvanceFeedback.Core.Projection`——把第 4 层的输出送入一条驾驶者可编辑的单调曲线（`MonotoneCubicCurve`/`OutputProjector`），以及一个可选的「达到最大值时脉冲」阶段（`PulseGenerator`）。这是驾驶者应该绑定震动器的那一层。

**禁止依赖：** SimHub 类型。可以依赖第 4 层的输出形状。

### G-Force（G 力）

**功能：** `QAdvanceFeedback.Core.GForce`——一套独立的通道集合（完全不从第 3-5 层派生），建模了一种「洗出式」结构，把持续水平与由变化率驱动的瞬态分开，另外还有按游戏/按车辆学习的最大值（`GForceMaxLearner`）。

**禁止依赖：** 第 3/4/5 层或 SimHub 类型。（车轮抱死/打滑的「抖动」集成会读取第 5 层的输出，作为 G 力自身振幅的一个输入——这是唯一的刻意例外，详见 `GForceEngine.Compute` 自己的注释。）

### 设置/持久化

**功能：** `QAdvanceFeedback.Settings`（设置 POCO 加 WPF 设置界面）以及 `ConfigStore`/`RuntimeStore`/`Core.Runtime`（配置与学习状态的 JSON 持久化）。只读写普通的 double、枚举和字符串——绝不会把一个存活的 SimHub 引用嵌入到持久化对象里。

**禁止依赖：** 就任何需要做单元测试的部分而言，不依赖 SimHub 类型（WPF 控件本身是这个子系统中唯一必须依赖 SimHub、因而也是唯一没有单元测试的部分——见 `ApplyDirtyStateTests.cs` 自己的注释）。

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

针对七份真实遥测日志的直接测量（`docs/cold-start-convergence-report.md`）表明，当前的收敛节奏已经达到数据本身能安全支持的最快速度——如果再加快，会以可测量的方式牺牲抵御瞬时过度报告的余量，而这正是这套设计要极力避免的风险。

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
- **设置面板「Auto detected」（自动检测）读数——过期快照修复。** `SettingsControl.RefreshGForceLearnedText` 过去只在构造时（`LoadFromSettings`）以及 Accel/Decel 模式下拉框的选项真正发生变化时才会被调用——从未有过定时刷新。经过端到端追踪：`GForceSettings.SetCurrentGameAndCar`/`ObserveAccelG`/`ObserveDecelG`/`GetLearnedMax`/`TryGetCurrentAccelAutoDetected`/`TryGetCurrentDecelAutoDetected` 全部使用同一个 `(gameId, carId)` 键，而真正驱动实时 G 力严重度路径的 `EffectiveAccelMaxG`/`EffectiveDecelMaxG` 则是每一帧遥测都重新查询——这条链路从未出现过过期或错配（从 1.0.6.5 到当前版本逐字节比对完全一致）。唯一的问题只是这段设置面板文本是一次性快照：如果驾驶者在开始跑圈前打开了面板（此时正确显示「暂无数据」），并让面板一直保持打开，那么即便之后确实积累了真实证据，这段文字也永远不会更新——这是**纯粹的界面（cosmetic）问题**，从来不是键/通道/学习器的错配，也从未影响任何实际生效的行为路径。修复方式是在 `SettingsControl` 中加入一个轻量的一秒 `DispatcherTimer`，在构造函数中启动，并在 `Unloaded` 时停止。

## SimHub 依赖型 vs. 纯净/可测试型

这条边界正是让本插件几乎整体都能在不启动 SimHub 进程的情况下完成单元测试的关键：

- **SimHub 依赖型**（引用了 `SimHub.Plugins`/`GameReaderCommon`，无法在真实 SimHub 宿主之外构造）：`QAdvanceFeedback.cs`（插件的组合根）、`SimHubTelemetryAdapter.cs`、`MotorsExportAvailabilityProvider.cs`、`PropertyPickerLauncher.cs`、`SimHubScriptEditor.cs`、`SimHubExpressionEvaluator.cs`、`WheelSourceResolver.cs`、`Settings/SettingsControl.xaml(.cs)`。
- **纯净/可测试型**（纯 C#，完全不引用 SimHub）：`Core\` 下的一切（第 1/3/4/5 层、G 力），加上设置 POCO 和 `ConfigStore`/`RuntimeStore`（它们把日志记录抽象为普通的 `Action<string>` 委托，而不是直接引用 SimHub 的日志对象）。

测试项目（`QAdvanceFeedback.Tests`）通过把这些纯净文件直接作为源码链接编译进一个零 SimHub 包引用的 net8.0 程序集来强制这一点，而不是引用已构建好的 net48 插件 DLL——一旦有 SimHub 依赖悄悄混入了本应保持纯净的文件，测试构建会立即失败，而不只是一个没人检查的运行时假设。

## 逐文件地图

### `QAdvanceFeedback\`（组合根、第 2 层、SimHub 相关辅助类）

| 文件 | 层 | 用途 |
|---|---|---|
| `QAdvanceFeedback.cs` | 组合根 | `IPlugin`/`IDataPlugin`/`IWPFSettingsV2` 入口点——每一帧把所有层串联在一起。 |
| `SimHubTelemetryAdapter.cs` | 2 | 把 SimHub 的 `GameData` 映射到第 1 层的 `TelemetrySample`；捕获原始诊断快照。 |
| `ITelemetryAdapter.cs` | 2（契约） | `SimHubTelemetryAdapter` 实现的接口。 |
| `MotorsExportAvailabilityProvider.cs` | 设置相关 | 把一个存活的 `PluginManager` 适配为 `MotorsExportAvailabilityResolver`，供设置界面的内联提示使用。 |
| `WheelSourceResolver.cs` | 第 4 层输入 | 把 `WheelChannelSettings` 的某个数据源字段（普通属性、JavaScript 或 NCalc）解析为一个实时的 0-100 读数。 |
| `PropertyPickerLauncher.cs` / `SimHubScriptEditor.cs` / `SimHubExpressionEvaluator.cs` | 设置相关 | 供设置界面的选择器/脚本编辑器按钮及表达式求值使用的 SimHub 反射辅助类。 |
| `PropertyPublisher.cs` | 发布边界 | 注册每一个发布到 SimHub 的属性（`Register`/`AttachTier`/`AttachTierNullable`）——该类中依赖 SimHub `IPlugin`/`AttachDelegate` 的那一半；仅限 net48。 |
| `PropertyPublisher.State.cs` | 发布边界 | 同一个 `partial class` 中不依赖 SimHub 的那一半：每一个后备字段、每一个 `Update*` setter、每一个 `*Snapshot` 访问器，以及 `SnapshotAllValuesForCsv` 本身——之所以拆分出来，是因为这一半（CSV 表头/数据行列数不一致的问题可能就出在这里）可以被链接编译进测试项目直接验证。 |
| `CsvExportWriter.cs` | 诊断 | 在「Export session to CSV」开启时，把每一个已发布的属性写入 CSV 文件。 |
| `ConfigStore.cs` / `RuntimeStore.cs` | 持久化 | 设置与学习到的运行时状态的 JSON 加载/保存。 |

### `QAdvanceFeedback\Core\`（第 1 层 + 共享基础组件）

| 文件 | 层 | 用途 |
|---|---|---|
| `ITelemetryFrame.cs` / `TelemetryFrame.cs` | 1 | 与具体游戏无关的一帧遥测数据。 |
| `ITelemetrySample.cs` / `TelemetrySample.cs` | 1 | 当前帧 + 上一帧 + 经过的时间。 |
| `Corners.cs` | 共享 | 四轮数值结构体，固定的 FL/FR/RL/RR 索引顺序。 |
| `ClampMath.cs` | 共享 | 发布边界处的截断（`To0100`/`To01`）及安全转换辅助方法。 |
| `MathHelpers.cs` | 共享（第 3 层公式） | Clamp/Map/Offset/分段映射等重映射辅助方法。 |
| `AggregationWeights.cs` / `Aggregator.cs` / `WheelAggregate.cs` | 共享（第 3/4/5 层） | 把四个逐轮数值组合成 Front/Rear/Left/Right/All 的、具有物理依据的轴间/左右混合。 |
| `ILegacyWheelLockSlipEngine.cs` / `LegacyWheelLockSlipResult.cs` / `WheelLegacyResult.cs` / `LegacyThresholds.cs` | 3（契约） | `RawCalculatorEngine` 实现的公开契约，以及为其把关的、车主可配置的踏板阈值。 |
| `WheelSlipBranchNames.cs` / `WheelSlipBranchSelector.cs` | 3（选择） | 诊断用分支名称常量，以及纯粹的能力优先级选择器。 |
| `RawWheelTelemetrySnapshot.cs` / `RawWheelTelemetryBuilder.cs` | 2/3 边界 | 第 3 层调度所读取的逐轮原始遥测+能力快照，以及其 null-vs-zero 的判定逻辑。 |
| `IValueDistributionLearner.cs` | 3（契约） | `StreamingPercentileLearner` 实现的学习器契约。 |
| `OnlineDistributionLearner.cs` | 4（KeyedScaleLearner 支持） | 用于按数据源缩放标定机制的、独立的流式均值/方差学习器。 |
| `PublishedPropertyNames.cs` / `AllPublishedProperties.cs` | 发布边界 | 每一个已发布属性的名称，产品属性与诊断属性均包含。 |
| `TelemetryLearningGate.cs` | 4/G 力 | 「这一帧对跨帧学习器而言是否是有效证据」的共享判定门（维修站/回放/会话重启）。 |
| `AccelerationUnits.cs` | 2 | m/s² 与 G 之间的换算，只在 SimHub 相关的边界处使用一次。 |
| `RobustBandEstimator.cs` | 共享（G 力） | 基于索引的样本池估计器（剔除最高的离群值，截取剩余部分的一段样本池，混合样本池自身的最大值/均值），供 `GForceMaxLearner` 用于自动最大 G 值参考——见 docs\robust-auto-gforce-report.md。也曾评估用于 Normalized 层的 `GripLearner`/`KeyedScaleLearner`，但未被采用（具体测量原因见该报告）。 |
| `ColdWarmBlend.cs` | 共享（第 4 层支持） | `GripLearner` 与 `KeyedScaleLearner` 共用的、按分散度加权的冷/热持久化机制——依据本局实时证据自身的变异系数（而不仅仅是样本数量）来权衡它与持久化的历史值，因此噪声较大的会话会倾向于信任持久化的数值，而不是靠数量把它覆盖掉。 |
| `KeyedTelemetrySupport.cs` | 2/3 边界 | 仅按游戏（而非按车辆）检测某款游戏是否真正支持那个没有对应 SimHub 能力标志位的遥测字段（`WheelOnLooseSurfaceFrontLeft`）——只有在出现持续的 `true` 证据后才会把某款游戏判定为「支持」，并且一旦判定后，无论在本局会话内还是跨越重启，都不会再被撤销。 |

### `QAdvanceFeedback\Core\RawCalculator\`（第 3 层具体引擎）

| 文件 | 用途 |
|---|---|
| `RawCalculatorEngine.cs` | `ILegacyWheelLockSlipEngine` 的实现——按帧调度，持有每一个有状态的学习器/滤波器。 |
| `BrakeSpeedSlipModel.cs` | 基于踏板+车速+转速推导的逐轮 Lock/Slip 模型（在没有逐轮遥测数据时使用的分支）。 |
| `BrakingVsSpeedModel.cs` | 仅基于踏板+车速的整车级 Lock/Slip 模型，外加低速修正。 |
| `DispatchBranchFormulas.cs` | 其余各分支公式（车轮转速、车轮速度、预先标定的打滑、学习到的分布、车轮速度差值）。 |
| `WheelRotationLockFilter.cs` | 基于车轮转速与车速对比、经 EMA 平滑的逐轮抱死估计。 |
| `StreamingPercentileLearner.cs` | `IValueDistributionLearner` 的具体实现——一个分桶的运行中直方图（均值 + 最近秩百分位）。 |

### `QAdvanceFeedback\Core\Normalized\`（第 4 层）

| 文件 | 用途 |
|---|---|
| `NormalizedWheelLockSlipEngine.cs` / `NormalizedWheelLockSlipResult.cs` | 第 4 层引擎及其发布的结果形状。 |
| `GripLearner.cs` / `KeyedGripLearner.cs` / `GripLearnerKeyMigration.cs` | 相对于本车学习到的峰值参考，按游戏+车辆+数据源（+路面）分别建键，并为旧的持久化键形状提供迁移支持。 |
| `KeyedScaleLearner.cs` | 按数据源分别做的缩放标定，锚定在一个共享的物理参考值上。 |
| `SourceIdentity.cs` | 根据某个通道的四个 Source/ScriptType 字段计算出一个稳定的复合键。 |
| `SurfaceLooseFraction.cs` | 连续的密实/松散路面混合权重。 |
| `LongitudinalDirectionResolver.cs` | 根据车速的差分结果，判定 Slowing/SpeedingUp/Unknown。 |
| `AchievedMotion.cs` | 供诊断使用的、按降级等级划分的 G 力幅值判定。 |

### `QAdvanceFeedback\Core\Projection\`（第 5 层）

| 文件 | 用途 |
|---|---|
| `ProjectedWheelLockSlipEngine.cs` / `ProjectedWheelLockSlipResult.cs` | 第 5 层引擎及其结果形状。 |
| `OutputProjector.cs` / `MonotoneCubicCurve.cs` / `PiecewiseCurve.cs` / `ProjectorSettings.cs` / `ProjectorAnchorEditor.cs` | 驾驶者可编辑的曲线及其设置/界面编辑辅助类。 |
| `PulseGenerator.cs` / `PulseSettings.cs` | 可选的「达到最大值时脉冲」阶段。 |

### `QAdvanceFeedback\Core\GForce\`

| 文件 | 用途 |
|---|---|
| `GForceEngine.cs` / `GForceOutput.cs` / `GForcePublishedNames.cs` | 洗出式 G 力引擎及其发布的 8 通道输出。 |
| `GForceMaxLearner.cs` | 通过 `RobustBandEstimator`，在一个 2 分钟实时窗口内学习每款游戏/每辆车的加速/刹车最大值，没有最小样本数量门槛。 |
| `GForceShake.cs` | 「Integrate Wheel Lock and Slip」抖动调制。 |

### `QAdvanceFeedback\Core\Health\`（韧性模型支持）

| 文件 | 用途 |
|---|---|
| `HealthRegistry.cs` | 一个小型、纯净、不依赖 SimHub 的注册表，每一个被加固的边界都会从自己的 catch 块内向它上报——绝不主动上报，绝不在每一帧都宣称「一切正常」。 |
| `HealthEntry.cs` | 一条注册表记录：子系统名称、严重程度、一个本地化键、原始异常详情、首次发生时间、发生次数，以及可能的原因是否是 SimHub 兼容性问题。 |
| `HealthSeverity.cs` | `Degraded`/`Failed` 严重程度枚举。 |
| `HealthSubsystems.cs` | 每个上报点都会用到的一组固定子系统名称常量，确保同一个子系统再次上报时只会更新已有的记录，而不是让注册表不断增长。 |
| `SafeCall.cs` | `PropertyPublisher.AttachSafe` 用来包裹每一个已发布属性取值逻辑的 `SafeCall.Value` 包装器，使得单个取值逻辑抛出异常时，只会让那一个属性降级为「无值」。 |

### `QAdvanceFeedback\Core\MotorsExport\`

| 文件 | 用途 |
|---|---|
| `MotorsExportPropertyNames.cs` | SimHub 自身 ShakeIt Motors 导出属性的名称形状（必须与 SimHub 的真实 API 保持一致——见 clean-room-restructure 报告中关于 ShakeIt 清理部分的说明）。 |
| `MotorsExportAvailabilityResolver.cs` | 纯粹的「四个车轮的导出属性当前是否都可用」检查。 |

### `QAdvanceFeedback\Core\Localization\`

| 文件 | 用途 |
|---|---|
| `Strings.cs` / `StringTableEn.cs` / `StringTableZhHans.cs` | 设置界面自己的字符串表（英文/简体中文）。 |

### `QAdvanceFeedback\Core\Runtime\`

| 文件 | 用途 |
|---|---|
| `RuntimeDocument.cs` / `RuntimeCache.cs` | 持久化的学习状态文档形状，及其内存中带脏标记跟踪的缓存。 |

### `QAdvanceFeedback\Settings\`

| 文件 | 用途 |
|---|---|
| `QAdvanceFeedbackSettings.cs` | 根设置对象（Lock/Slip/GForce/General）。 |
| `WheelChannelSettings.cs` | 单个通道（Lock 或 Slip）的数据源、聚合权重、阈值、曲线、脉冲设置。 |
| `GForceSettings.cs` | G-Force 标签页的设置 + 学习到的最大值导入/导出。 |
| `GeneralSettings.cs` | 诊断/CSV 导出开关。 |
| `SourceMode.cs` / `ScriptType.cs` / `SourceButtonMode.cs` | 支撑 Sources 部分的小型枚举。 |
| `DefaultWheelSources.cs` | 构建出厂默认的 Manual 模式数据源文本（对第 3 层自身 Raw 属性的一个简单引用）。 |
| `ApplyDirtyState.cs` | 跟踪设置界面是否存在未保存的修改，供 Apply 按钮的启用状态使用。 |
| `SettingsControl.xaml` / `SettingsControl.xaml.cs` | 唯一的 WPF 设置控件（四个标签页）。 |

## 设置截图采集规则（长期规则）

`docs\images\settings-*.png`（在两个 README 的「截图」小节中被引用）由一个一次性使用、不属于本仓库的 WPF 采集工具渲染而成（不属于本解决方案/测试的一部分）——它加载已构建好的 `QAdvanceFeedback.dll`，独立实例化 `Settings\SettingsControl.xaml(.cs)`，并按标签页渲染为 PNG。Apply/Restore 按钮行是 `SettingsControl.xaml` 中 `MainTabs` 的一个 `DockPanel.Dock="Bottom"` 同级元素——它位于 `TabControl` 之外，因此无论当前选中哪个标签页，逐标签页的截图都不会包含它。

按标签页划分的采集规则（未来每次重新生成截图都应遵循，无需再次提醒）：

- **Wheel Lock、Wheel Slip、G-Force**——这三个标签页内容较长。只截取当前选中 `TabItem` 的内容（其 `ScrollViewer` 的内容元素），不包含上方的标签条和下方的按钮行，使整个标签页的设置内容都能完整地放进一张图片，不被裁切。
- **General**——足够短，即使包含外层框架也不会丢失内容。改为截取完整的 `SettingsControl`：标签条、General 标签页的内容，以及 Apply/Restore 按钮行都包含在内。

在这两种情况下，都要按渲染目标自身的完整自然尺寸进行测量/排布（`Measure` 时高度设为 `PositiveInfinity`，随后在得到的 `DesiredSize` 上进行一次显式的 `Arrange`），而不是采用预览窗口宿主恰好施加的高度——如果跳过这个显式的重新 `Arrange` 步骤，`ScrollViewer` 的视口会裁切较长的内容，而 `DockPanel` 中负责填充的子元素又会拉伸以填满一个过大的宿主窗口，导致按钮行上方出现一段空白。

输出文件名（注意是 `settings-gforce.png`，而不是 `settings-g-force.png`——采集工具是从标签页标题文字推导文件名的，G-Force 这一个需要单独改名以匹配 README 中的链接）：`settings-wheel-lock.png`、`settings-wheel-slip.png`、`settings-gforce.png`、`settings-general.png`。

建立这条规则那次工作的完整依据、验证证据和像素尺寸：`docs\screenshot-capture-rule.md`。

## 韧性模型与健康状态注册表（长期规则）

本插件作为第三方，与所有其他已启用的插件、ShakeIt 以及各种仪表盘共享同一个存活的 SimHub 进程——我们自己代码中的一次故障绝不能传播到 SimHub 自身的调度流程或其他插件中去。关于哪些 SimHub 入口点在设计上是/不是异常安全的完整反编译证据，见 `docs\pipeline-exception-safety-report.md`；本小节是这方面持久有效的总结，以及把降级状态呈现给驾驶者、而不是让它悄无声息地发生的健康状态注册表设计。

**已加固的边界，端到端：**

- 每一个 `IPlugin`/`IDataPlugin`/`IWPFSettingsV2` 入口点（`Init`、`DataUpdate`、`End`、`GetWPFSettingsControl`）都被包裹在自己的顶层 try/catch 中，每种不同的故障只记录一次日志（绝不按帧记录），并且从不重新抛出异常——`Init` 尤其重要，因为 SimHub 自身的 `EnablePlugin`（延迟/手动启用路径）调用它时完全没有自己的 try/catch（已通过反编译确认）。
- 每一个发布到 SimHub 的属性（`PropertyPublisher.Register` 中的 `AttachDelegate` 调用）都通过 `PropertyPublisher.AttachSafe` -> `Core.Health.SafeCall.Value` 进行包裹，因此某个取值逻辑抛出异常时，只会让那一个属性降级为 SimHub 自身的「无值」，而不会传播到正在读取它的任何仪表盘/ShakeIt 效果/其他插件——`PropertyEntry.Evaluate()`/`PropertyEntryWrapper.GetValue()` 本身就是不受保护的 SimHub 基础机制（已反编译确认），因此本插件不能指望 SimHub 会替它捕获一个抛出异常的取值逻辑。
- 每一个对未文档化 SimHub 内部机制的反射包装（`SimHubScriptEditor`、`PropertyPickerLauncher`、`SimHubExpressionEvaluator`）都只解析一次、缓存结果，并且一旦失败就在本次会话剩余时间内永久降级为「不可用」——绝不会在下一帧/下一次点击时重试并再次抛出异常。`SimHubTelemetryAdapter.CaptureRawTelemetry` 自己对 `GetFeedbackCapabilities` 的调用（这是一个真实的 API，不是反射，但同样依赖一个未文档化的 SimHub 形状）也采用相同方式加固。
- 所有文件 I/O（`ConfigStore`、`RuntimeStore`、`CsvExportWriter`）在文件缺失、损坏、被锁定或权限不足时，都会降级为使用默认值/停止记录，而不是抛出异常。
- `RuntimeStore` 后台刷新用的 `Timer` 回调（`FlushTick`）是这里最危险的一个类：在 .NET Framework 下，直接发生在这个原始线程池线程上的未处理异常可能会终止整个 SimHub 进程。它（以及现在同样在该线程之外的 `Task.Run` 中独立运行的 `WriteAtomic`）都被一个宽泛的、兜底的 `catch (Exception)` 完整包裹。
- 设置界面的构造函数由 `GetWPFSettingsControl` 自身的防护覆盖；其顶层的 `Button.Click` 处理程序（Apply、Restore all defaults、按数据源重置、脚本编辑器/属性选择器的操作按钮）都分别包裹在 `SettingsControl.SafeUiAction`/`SafeUiActionAsync` 中——因为在构造完成之后很久才被调用的 WPF 事件处理程序，其上游本来不会有任何东西去捕获异常。
- 异常的遥测数据（NaN/无穷大/负值或过大的 `dt`、null 的 `GameData`/`NewData`/`OldData`、缺失车辆或游戏 id）在两端都受到防护：`DataUpdate` 自己的 null/状态检查会在到达 Core 之前就短路处理，而每一个 Core 引擎也都独立对自己的输入做有限性检查（见 `AbsentTelemetryTests`/`DtNormalizationTests`/`ClampMathTests` 等测试）——单独任何一道防护本身就足以防止异常抛出，因此这是刻意的双重保险，而不是一个单点故障。

**健康状态注册表（`QAdvanceFeedback.Core.Health`）：** 一个小型、纯净、不依赖 SimHub 的注册表（`HealthRegistry`、`HealthEntry`、`HealthSeverity`、`HealthSubsystems`），上面所有被加固的边界都会从各自的 catch 块内向它上报——绝不主动上报，也绝不在每一帧都宣称「一切正常」，这正是让「注册表中完全没有记录」成为健康状态的原因。每一条记录都包含子系统名称、严重程度（`Degraded`/`Failed`）、一个用于生成简短的、驾驶者可读的「这对你意味着什么」提示的本地化键（在显示时通过 `Strings.Get` 解析，绝不会写死成英文）、原始异常详情（供提交问题报告使用，刻意不做本地化）、首次发生的时间，以及可能的根本原因是否是 SimHub 更新移动/重命名/改变了本插件所依赖的某个东西（`IsSimHubCompatibilityIssue`）——这是车主特别要求要被明确点名、而不是笼统显示为一个不透明失败的唯一情形。再次上报同一个子系统（例如某个取值逻辑每一帧都在抛出异常）只会更新已有那条记录的时间戳/发生次数，而不会让注册表不断增长——这正是即使在持续故障下，也能维持「只记录一次日志，而不是按帧记录」的原因。

**设置界面呈现方式（General 标签页，「Plugin health」分组）：** 当 `HealthRegistry.Snapshot()` 为空时，只显示一行且不显眼的提示（「All systems normal - nothing to report.」），因此在正常情况下不会带来任何视觉干扰。否则，每个降级的子系统都会显示一行加粗的警告——一个驾驶者可读的子系统名称加上它的影响说明，`Degraded` 用橙色，`Failed` 用暗红色——并且对于任何被标记为 SimHub 兼容性问题的记录，还会附加一句通俗易懂的提示「此功能需要针对你的 SimHub 版本进行更新」，而不是显示一段原始异常信息。一个「Copy details for a bug report」按钮（仅在确有内容可报告时显示）会把每条记录的技术细节（子系统、严重程度、时间戳、发生次数、异常文本）复制到剪贴板，方便车主粘贴到问题反馈中。这个界面只在设置控件构造函数结束时刷新一次（`SettingsControl.RefreshHealthUi`，在该控件用到的每一个反射包装都已经被构造函数早先的接线逻辑强制解析完毕之后调用），并且在任何一次 `SafeUiAction`/`SafeUiActionAsync` 捕获到异常之后也会再次刷新，因此点击操作过程中发生的故障无需重新打开标签页就能立刻反映出来。

**已知的未加固路径，明确说明：** SimHub 自身的 `PluginManager.GetPropertyValue`，以及最终到达 `PropertyEntryWrapper.GetValue()` 的 NCalc/公式引擎调用链，已通过反编译确认本身就是异常安全/实践中安全的（见 pipeline-exception-safety 报告）——本插件既不会也不能去修补 SimHub 自身的基础机制。如果某个其他调用方（另一个插件、ShakeIt 自身的内部逻辑）在没有经过 SimHub 自身包装的情况下，直接调用了 `PropertyEntry.Evaluate()`/`PropertyEntryWrapper.GetValue()`，那仍然是真正未加固的——这超出了本插件的能力范围，本文档也不声称已经修复了这一点。

## 「Private」曾经所在的位置

`Core\RawCalculator\` 下的一切，加上 `SimHubTelemetryAdapter.cs`，曾经存放在一个被隐藏、被 git 忽略的 `Private\` 文件夹中，位于两个项目之外，并由一个基于反射的工厂（`AlgorithmFactory`/`PrivateTypeResolver`）在运行时解析它们，在其缺失时回退到惰性桩实现（`InertTelemetryAdapter`/`InertLegacyWheelLockSlipEngine`）。这种拆分方式，以及背后的整套机制，现在已经不存在了——完整的历史和理由见 `docs\clean-room-restructure-report.md`。
