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

**保真契约（1.0.7.0）：** 本层的职责是复现 SimHub ShakeIt Motors 插件实际发布的数值，使两者体感一致。自 1.0.7.0 起，标定（calibration）机制不再是“功能相似的自有实现”，而是 SimHub 自身实现的忠实移植，见 `Core\RawCalculator\Calibration\`：直方图及其自适应分桶阶梯、500 个正样本的百分位门槛、`Math.Max(1.0, Max * 0.9) * pct / 100` 的未成熟回退公式、`track;car;metric` 键、7000 点的写入上限，以及与预置数据固定 0.25 的混合比例，均来自 SimHub，并已对随包发布的 `SimHub.Plugins.dll` 逐一核实。**仅保留两处有意为之的偏离**，且均在调用处有说明：平滑滤波器经过 dt 修正（SimHub 使用固定的每帧系数，其时间常数会随游戏遥测帧率变化；本插件在 60Hz 下与其完全一致，在其他帧率下则保持一致行为）；以及最后一个分支的 legacy/非 legacy 子选择未由任何能力标志位暴露，因此总是假定为 legacy 变体。

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

### 关键数据点：手动 SMax/S90/S75 及其生效时机（1.0.7.0）

`Settings\KeyDataPointSettings.cs`、`Core\Normalized\ManualOverrideGate.cs`。

**学习从不受条件限制。** `AutoGenerate` 只决定哪些数值会被**发布**，并不控制观测本身。`ObserveAtPhysicalLimit`/`ObserveGeneral` 以及锚点学习器的弯道缓冲，都在 `ComputeChannel` 中手动数值那一段**之前**无条件执行。由此带来三个结果：手动模式下 `[学习值: xx.x]` 提示依然有意义；切回自动是立即生效的；手动数值永远不可能污染学习器已经掌握的内容。

**数值按「槽位」存储，槽位 =（模式, 游戏, 数据源）。**

```
全局模式     ->  "global|src:<sourceIdentity>"
每游戏模式   ->  "game:<gameId>|src:<sourceIdentity>"
```

数据源**始终**是键的一部分，因为适用于 ShakeIt 导出的数值并不适用于本插件自己的 Raw——两者量程不同。游戏只在每游戏模式下参与构键；不含游戏，正是「全局」的含义。两类命名空间共用一个字典，因此「全局 → 每游戏 → 全局」的往返切换，会原封不动地取回最初的全局数值。

**门控。** `ManualOverrideGate` 会一直扣住手动数值，直到该上下文**同时**满足：冷启动完成（`KeyedScaleLearner.CeilingHandoverConfidence` >= 0.95）**且**已累计 30 秒驾驶时间。只有车辆确实在移动的帧才会被计入，且任何超过 0.5 秒的单帧都会被截断——因此暂停、断点或切出游戏都无法凑满该门控。

**一次性写入发生在插件的每帧循环中**，而不是设置页面里——因为无论页面是否打开，它都必须发生。当某个槽位首次满足条件时，学习值会被写入，并直接调用 `ConfigStore.Save`（而非 `ApplySettings`，后者还会重建 Projected 引擎——那是不该在一帧中途做的事）。由于该闩锁是**按槽位**记录的，一个从未游玩过的游戏、或一个新选择的数据源，都会自行再次触发一次写入。插件会递增一个修订计数器，好让已打开的设置页面重新加载。

**默认值按数据源类型区分**，通过 `KnownSourceColdStartReference.Classify` 解析：对本插件自己的 Raw 与 ShakeIt 导出，Lock 为 85/75/60，Slip 为 75。**未知**数据源（脚本、NCalc 表达式）刻意**不给任何默认值**——对一个量程从未被测量过的信号，任何猜测都称不上诚实；这类通道会继续发布学习值，直到某个槽位被写入为止。

**仅最大抓地力 / 仅最佳点**模式会隐藏下方两个锚点，并在后台按顶部数值的 0.90/0.70 推导它们，因此切回三点映射时不会发现它们已经过时或次序错乱。Slip 始终采用推导方式——它没有原生的 90%/75% 抓地力测量——这也是它随包默认「仅最佳点」的原因。

**手动模式下的恢复按钮**（`ResetKeyDataPoints`）：对 `KnownSourceColdStartReference.Classify` 能识别的数据源，写入随包提供的默认值，并将该槽位标记为已写入，使一次性学习值写入不会立刻覆盖驾驶者刚刚选择的内容。对**未知**数据源，没有可写入的默认值，因此改为调用 `KeyDataPointSettings.ClearSlot`——数值与已写入闩锁一并清除——从而重新武装一次性写入，并让输入框保持空白，直到真实证据到来。与页面上其他恢复操作一样，点击即刻持久化。

**自动模式下 Slip 被强制为「仅最大抓地力」**（`EnforceSlipPatternForAutoMode`），且自动开启时选择框被禁用。Slip 没有原生的 90%/75% 测量，其下方两个锚点始终是推导而来；在插件自行生成数值时提供三点映射，等于把推导值当作测量值呈现。手动模式下由驾驶者提供全部三个数值，因此该选择重新可用。Lock 三个点都能测量，不受影响。

**两张曲线图**（`RenderGraphDecorations`、`RenderKeyPointMarkers`、`RenderSourceToProjectedGraph`）在代码中绘制而非在 XAML 中声明，以便跟随实时设置。左图为 归一化 -> 映射，虚线标记位于关键数据点在归一化坐标上的规范位置（S75 -> 30、S90 -> 60、SMax -> 80，即四段曲线自身的节点表），「仅最大抓地力」模式下仅显示 SMax。右图为 源数值 -> 映射 的完整链路；由于关键数据点与投影器是复合关系，它没有闭式解，因此按每 5 取样并连线。两张图共用同一套网格：每 20% 高度一条 25% 黑度的横线，0% 处改用纯黑作为基线，横向每 20% 一个 3px 刻度。

**「恢复默认」从不丢弃这些数值。** `QAdvanceFeedbackSettings.RestoreDefaults` 会把两个通道的 `KeyDataPoints`（所有槽位，以及自动/每游戏两个开关）一并带过重置；而三个通道级的数据源重置方法，只会触碰数据源字段。

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

  | 模式 | 区间来源 | 行程 | 横向偏置 |
  | --- | --- | --- | --- |
  | `PerChannel`（即 1.0.8 之前的全部行为） | 该通道自身的水平 | 以该水平为中心 | 应用 |
  | `AllChannelsGForce` | **当前活跃链条的终端**通道水平，应用于全部 8 个 | 以该水平为中心 | 应用 |
  | `AllChannelsLockSlip` | `100 × contribution`——完全忽略 G 力 | **从 0 起** | **不**应用 |
  | `HigherOfGForceOrLockSlip`（**出厂默认**） | `Max(通道水平, 100 × contribution)` | **从 0 起** | 应用 |

  `AllChannelsGForce` 按**方向**选择终端通道（刹车→BottomFront，加速→BackTop），而不是按抱死/打滑哪个更大：否则刹车中的打滑会选中 BackTop，而它在刹车时水平为 0，恰恰会在最需要抖动时让它消失。因此该终端通道的输出与 `PerChannel` 完全一致。`AllChannelsLockSlip` 刻意不应用横向偏置，因为该模式的全部意义就在于输出只取决于抱死/打滑。

  **`HigherOfGForceOrLockSlip` 抬高的是峰值，而不是移动波形。** 各通道的最大值变为 `Max(其 G 力水平, 抱死/打滑数值)`，最小值仍为 0，波形本身完全不变——因此两种提示互不掩盖：重刹依然通过 G 力水平传达，而抱死则在它更强的位置叠加显现。

  **两种由车轮驱动的模式从 0 起振，而不是围绕中心摆动。** `AllChannelsLockSlip` 与 `HigherOfGForceOrLockSlip` 的最小值都取 0，而非 `level − half`。它们的频带**就是**车轮数值本身，若采用居中摆动，完全抱死时也只会掉到半强度，读起来会是一阵响亮的嗡鸣而不是抖动；两种 G 力模式则保留居中摆动，因为在那里频带是对既有水平的*调制*，抖动开始时不允许产生跳变。

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

  **切换 `ShakeApplyMode` 不会重写两个强度值**（作者，2026-09-06）。此前会重写，理由是同一个强度在不同模式下含义不同。事实并非如此：`contribution = scale × wheel/100` 在全部四种模式中的计算完全一致，且抱死与打滑的强度是分别作用于各自通道之后，引擎才取其中较大者。真正不同的只是该 contribution 所**乘的对象**——两种 G 力模式乘的是震动垫自身水平，两种由车轮驱动的模式乘的是完整的 0-100 区间——因此在 1.5 时，由车轮驱动的模式从车轮值 67 起就达到满宽频带。这是一个值得知晓的饱和点，而不是含义的改变，更不足以成为覆盖手动调好数值的理由。两个强度在所有模式下的出厂值均为 **1.5**。

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

- **Test Effect 效果测试面板（1.0.8，`SettingsControl.GForceTest.cs`）。** 一个可拖动圆球的 G 力控制板与一条抱死/打滑滑条，把合成出来的帧送入设置页自己持有的一个**私有 `GForceEngine` 实例**，使驾驶者无需在赛道上真的制造一次抱死，就能设置好 ShakeIt 的各项强度。

  **它会写入真实属性。** 只驱动私有引擎，只会让界面读数动起来：在没有游戏运行时 `DataUpdate` 会在发布任何内容之前就返回，因此 `QAdvanceFeedback.GForce.*` 读到 null，而 ShakeIt——这个面板存在的全部理由——什么也收不到。`QAdvanceFeedback.PublishTestEffect` 通过实时路径所用的同一个 `PropertyPublisher.UpdateGForce` 写入（其属性是该数组之上的拉取式委托，因此无论有没有游戏在跑，写入都会生效），并置位一个标志让 `DataUpdate` 跳过自己的发布。传入 null 即清除接管；开关关闭与页面卸载都必须这样做，否则关闭后的页面会把输出永久钉死。

  **控制板方位**（作者，2026-09-06）：圆球标记的是载荷被甩向的方向，因此**上方为刹车、下方为加速，向左拖动表示右转**。两个轴在进入遥测帧时都会取反。

  此外还有两条性质是它存在的全部意义，两者均有守护：
  - **本页所有 G 力设置都会实时生效——但写入的是临时对象。** 每一个模拟帧都会先 `SaveGForceToSettings` 写入一个一次性的 `GForceSettings`，再 `ApplyTo(_testEngine)`，因此面板读取的是页面**当前**状态（含 Wheel Lock 与 Wheel Slip 强度），却不会碰插件的实时设置。它此前调用的是完整的 `SaveToSettings()`，而后者直接写入 `_plugin.Settings`——于是仅仅打开面板，就会把页面上所有尚未提交的修改推入正在运行的插件：一次无人请求的 Apply。把 G 力这一半抽取成接受目标参数的方法，正是临时对象得以成立的前提。
  - **它的任何内容都不会被保存**（作者要求）。其控件既不被 `SaveToSettings` 读取，也不被 `LoadFromSettings` 写入，且 `WireDirtyTracking` 会按 `GForceTest` 名称前缀跳过它们——按前缀而非清单，理由与该遍历本身采用反射完全相同。`SettingsControlDirtyTrackingTests` 对这三点均有守护。
  - **关闭即彻底无输出。** 开关关闭时控件整体折叠、不渲染任何标签，也不产生任何模拟帧——面板在关闭状态下绝不允许驱动驾驶者的电机。

  抖动区域的**预览图**是同一思路的静态版本：它直接调用 `FeelingPair`，绘制当前设置下两侧震动垫一秒内的动作。两处界面都没有重新实现波形，画出来的就是插件自身会输出的结果。

- **触发门限与抖动节奏（1.0.8，`GForceEngine.AdvanceShake`）。** 四条规则，目的都是让抖动读起来是一段连续的节奏，而不是一个每当车轮数值抖动一下就重新开始的信号：
  1. **阈值**（默认 5，作用于**未经缩放**的 `Max(lock, slip)`，因此它与驾驶者在车轮页签上看到的是同一个数字——调高缩放绝不会让抖动更早触发）。低于该值不会起振；它同时是整个整合功能的**软开关**：此时所有模式都会输出与 `IntegrateWheelLockAndSlip` 为 false 完全一致的结果——纯 G 力、无抖动，两种由车轮驱动的模式也不例外，因为它们已没有可比较的对象。
  2. **单一节奏。** 一旦运行，相位只会前进；车轮数值的变化只改变频带宽度，别无其他。
  3. **走完当前周期。** 掉出阈值后，抖动会把已经开始的那个周期走完。该时长以**显式余数**计算，而**非** `Ceiling(elapsed) × period`——相位是通过反复 `+= dt` 累加得到的，因此当掉落恰好发生在周期边界上时，除法会得到 `5.9999…`，`Ceiling` 随即返回抖动**当前就站在**的那个边界，导致收尾立刻结束，恰恰切断了这条规则本应保全的那一次完整动作。
  4. **在收尾期内重新触发会保持既有节拍。** 只有从完全静止开始的抖动才会重新对齐。

  收尾期内保持的是**最后一次高于阈值**的驱动量，而非当前低于阈值的那个值，从而让这次动作以驾驶者正在感受到的幅度收尾，而不是在结束时萎缩下去。
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
| `BrakingVsSpeedModel.cs` | 仅基于踏板+车速的整车级 Lock/Slip 模型——SimHub `GetSimpleBraking` 的忠实移植；原有的“低速修正”已在 1.0.7.0 移除（该文件内附有实测差异对照表）。 |
| `DispatchBranchFormulas.cs` | 其余各分支公式（车轮转速、车轮速度、预先标定的打滑、学习到的分布、车轮速度差值）。 |
| `WheelRotationLockFilter.cs` | 基于车轮转速与车速对比、经 EMA 平滑的逐轮抱死估计。 |
| `StreamingPercentileLearner.cs` | `IValueDistributionLearner` 的具体实现——一个分桶的运行中直方图（均值 + 最近秩百分位）。 |

### `QAdvanceFeedback\Core\RawCalculator\Calibration\`（ShakeIt 标定移植，1.0.7.0）

此目录下的每一个类型都是同名 SimHub 类型的移植，来源于对随包发布的 `SimHub.Plugins.dll` 的反编译。它们的存在意义在于：让第 3 层与 ShakeIt 在相同输入下得出相同的**数值**，而不仅仅是相同的公式。

| 文件 | 用途 |
|---|---|
| `ICalibrationData.cs` | 实时标定与随包预置共享的契约。注意 `GetPercentile` 返回普通 `double` 且永不为 null——这正是关键所在。 |
| `CalibrationData.cs` | 实时累积的直方图：自适应分桶阶梯、仅计正值的点数器、百分位记忆化，以及使其从第一个样本起就能给出答案的“运行最大值回退”。 |
| `PreloadedCalibrationData.cs` | 随包的分游戏预置：`MeasuredMaximum x CorrectionFactor x pct/100`，与实时证据按固定 0.25 混合——是永久比例，而非逐渐接管。`GetAverage` 有意抛出异常。 |
| `CalibrationDataProvider.cs` | 持有全部标定，以 `track;car;metric` 为键。将四个轮子汇聚为**单一** `Slip` 标定，而 `RPSToSpeed` 按轴拆分——这是 SimHub 自身的作用域划分，也是早期逐轮 Lock 学习器被废弃的原因。 |
| `GameCalibrationBounds.cs` | 在 SimHub 文件中与预置并列的三个分游戏车轮速差边界值。 |
| `TimeMovingAverage.cs` | 逐挡位的车轮速差参考值。本目录中**唯一**基于调用方式重建而非反编译得到的类型——它位于本项目不随包发布的 `WoteverCommon` 中；其内部说明阐述了为何这在此调用处不会产生影响。 |

### `QAdvanceFeedback\Core\Normalized\`（第 4 层）

| 文件 | 用途 |
|---|---|
| `NormalizedWheelLockSlipEngine.cs` / `NormalizedWheelLockSlipResult.cs` | 第 4 层引擎及其发布的结果形状。 |
| `SlipCrossingGate.cs` / `LockCrossingGate.cs` | 决定哪些「处于极限」的时刻有资格教学 SMax 的交叉门控（1.0.8）。**刻意实现为两个类而非一个共享类**：作者要求两个通道采用同一套机制但各自独立实现，以便任意一侧都能单独停用。完整规格见 `docs\slip-smax-crossing-gate-design.md`。 |
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
| `GForceShake.cs` | 「Integrate Wheel Lock and Slip」抖动调制：区间放置（平移而非压缩）以及 1.0.8 的 `SineHoldWave`——一条在两端各带停留的正弦波，周期恒为 1/f。 |
| `ShakeFeeling.cs` | 抖动进行时成对两侧的关系（1.0.8）——反相（出厂默认）、同相或错相。取代已退役的「Both-sides blend（双侧混合，%）」数值框；`Blending` 固定使用自己的 50% 停留，UI 也会为它隐藏停留控件。 |
| `ShakeApplyMode.cs` | 该抖动在 8 个震动垫间的分配方式（1.0.8）——按通道、全通道跟随 G 力、全通道仅按抱死/打滑，或 **G 力与抱死/打滑取较大者**（出厂默认）：每个震动垫从 0 摆动到当前较强的提示值，两者互不掩盖。两种由车轮驱动的模式采用**零下限**（车轮数值即为整个行程）；两种以 G 力为中心的模式仍围绕该垫当前水平抖动。 |

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
| `DefaultWheelSources.cs` | 构建出厂默认的「插件内置」模式数据源文本（对第 3 层自身 Raw 属性的一个简单引用）。 |
| `KeyDataPointSettings.cs` | 手动 SMax/S90/S75（Slip 为 完美/优秀/良好），按槽位存储 =（模式, 游戏, 源）；随包提供按源类型的默认值与校验。 |
| `ApplyDirtyState.cs` | 跟踪设置界面是否存在未保存的修改，供 Apply 按钮的启用状态使用。 |
| `SettingsControl.xaml` / `SettingsControl.xaml.cs` | 唯一的 WPF 设置控件（四个标签页）。其「未保存修改」跟踪采用对生成的 `x:Name` 字段进行**反射式**遍历，而不是一份枚举清单——那份清单曾落后 XAML 达 52 个控件，导致修改其中任何一个后 Apply 按钮仍然是灰的。 |
| `SettingsControl.GForceTest.cs` | 抖动预览图与 Test Effect 效果测试面板（1.0.8）：可拖动圆球的 G 力控制板与抱死/打滑滑条，驱动一个私有 `GForceEngine`，并在每一帧按页面**当前**状态重新喂入，因此所有 G 力设置（含抱死/打滑强度）均会生效。开关关闭时不产生任何输出。 |

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
