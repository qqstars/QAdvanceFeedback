# Slip Angle telemetry spike report (SimHub 9.13.2)

Task 0 of the slip-angle plan (design `slip-angle-design.md` §9.1, §9.2, §9.5.8, §12).

## Method and limits

- Source: the SimHub 9.13.2 installer, all `{app}\*.dll` unpacked with innounp-2 (278 files), then
  decompiled with `ilspycmd`. Every game reader assembly was decompiled and searched for slip, patch
  velocity, lateral velocity and surface/terrain/material members.
- SimHub exposes raw data as `DataCorePlugin.GameRawData.<path>` by walking the reader's raw-data
  object graph. Public members appear unless marked `DoNotExpose` / `RawIgnore`; a member tagged
  `ExposeName("")` is flattened into its parent. Paths below are derived from the static type graph.
  **SimHub was never run, so no path was observed live.** A path is "verified" only in the sense
  that the field exists, with the stated type, in the decompiled raw-data class.
- Only structure is recorded. A unit is stated only when the decompiled code states it (a type
  name such as `...Radians_...`, or code that converts it). Otherwise it is "unit unverified": the
  assemblies do not say.
- Wheel order: "FL,FR,RL,RR" is only stated where a type, name or enum states it, or where the
  reader remaps raw indices into SimHub's feedback order (the F1 and LFS remaps).
  Where a reader copies `raw[i]` straight into `FeedbackData[i]`, the raw index equals SimHub's
  feedback index, and it is marked "same index as FeedbackData (order not named in assembly)".
- Games with no tyre or wheel members at all (aircraft, trains, farming, coasters, boats, shooters,
  etc.) are listed one row each as N/A.

## Summary by verdict

Game ids are SimHub's own registry codes (`GameRegistry`, SimHubWPF.exe), 110 registered games plus
dynamically registered External Sim / custom games. Every registered id appears in the per-game table below.

**Native (direct)**: a per-wheel tyre slip angle field exists in raw data.
`ACC`, `AssettoCorsaRally`, `AssettoCorsaEVO`, `F12023`, `F12024`, `F12025`, `FM7`, `FM8`, `FH4`,
`FH5`, `FH6`, `LFS` (tangent), `Wreckfest2`, `ProjectMotorRacing`, `Drift21` (radians), `KartKraft`,
`BusBound` (bus sim, not a car title). Rennsport is a registered reader (`RennsportManager`) but has
no id in the 9.13.2 `GameRegistry` literal list (see "Could not determine").

**Native (computed)**: no angle field, but per-wheel lateral and longitudinal contact velocity.
`RFactor2`, `LMU`.

**Native (computed), PROVISIONAL**: `NASCARHeat4`, `NASCARHeat5`, `TonyStewartsAllAmericanRacing`,
`TonyStewartsSprintCarRacing`, `SRXTheGame` (all one reader). The fields `lat_terrain_speed` /
`lon_terrain_speed` exist per wheel, but whether they are contact-patch velocities is **not proven**.

**Unverified**: `IRacing` (raw data is a name-keyed dictionary built at runtime; no slip-angle or
per-wheel surface variable is referenced by the manager).

**Fallback only**: every other vehicle game with tyres (see the table, verdict column).

**N/A**: titles with no wheels or tyres (aircraft, trains, coasters, farming, boats, etc.), one row each below.

## Per-game table

Paths are under `DataCorePlugin.GameRawData.`. "unverified" means the assemblies do not say; "none
(searched)" means the whole raw-data type was searched and no such field exists. Wheel order
"FeedbackData index" means the reader copies `raw[i]` into `FeedbackData[i]` (order not named in the assembly).

### Native (direct)

| Id | Game | Verdict | Slip-angle path | Wheel order | Unit | Per-wheel surface |
|---|---|---|---|---|---|---|
| ACC | Assetto Corsa Competizione | Native (direct) | `Physics.slipAngle[4]` | FeedbackData index | unverified | none (searched; `Graphics.SurfaceGrip` is a scalar) |
| AssettoCorsaRally | Assetto Corsa Rally | Native (direct) | `Physics.slipAngle[4]` | unverified | unverified | none (searched) |
| AssettoCorsaEVO | Assetto Corsa EVO | Native (direct) | `Physics.slipAngle[4]` (type `SPageFilePhysics`) | unverified | unverified | none (searched) |
| F12023 | F1 23 | Native (direct) | `PacketMotionExData.m_wheelSlipAngle[4]` | raw RL,RR,FL,FR (reader remaps `(j+2)%4`) | unverified | `PlayerCarTelemetryData.m_surfaceType[4]` (byte; RL,RR,FL,FR) |
| F12024 | F1 24 | Native (direct) | same | same | unverified | same |
| F12025 | F1 25 (the id also runs the 2026 season pack, `F12026` manager) | Native (direct) | same | same | unverified | same |
| FM7 | Forza Motorsport 7 | Native (direct) | `TireSlipAngle{Front,Rear}{Left,Right}` (flattened) | named FL,FR,RL,RR | unverified | none (only `SurfaceRumble*` floats and `WheelOnRumbleStrip*`) |
| FM8 | Forza Motorsport 8 | Native (direct) | same names | named | unverified | same |
| FH4 | Forza Horizon 4 | Native (direct) | same names (`FH4Packet`) | named | unverified | same |
| FH5 | Forza Horizon 5 | Native (direct) | same (reuses `FH4Packet`) | named | unverified | same |
| FH6 | Forza Horizon 6 | Native (direct) | same (`FH6Packet`) | named | unverified | same |
| LFS | Live For Speed | Native (direct) | `OutSim2.OSWheels[i].TanSlipAngle` | raw RL,RR,FL,FR (remapped) | tangent (not an angle) | none (searched; `Touching` byte only) |
| Wreckfest2 | Wreckfest 2 | Native (direct) | `Main.carPlayer.tires[i].slipAngle` | FL,FR,RL,RR (enum) | unverified | `Main.carPlayer.tires[i].surfaceType` (enum) |
| ProjectMotorRacing | Project Motor Racing | Native (direct) | `PlayerTelemetry.m_wheels[i].m_slipAngle` | FeedbackData index | unverified | `PlayerTelemetry.m_wheels[i].m_contactMaterialHash` |
| Drift21 | Drift21 | Native (direct) | `RaceTelemetry.TireSlipAngleRadians_{Front,Back}{Left,Right}` | named FL,FR,BL,BR | radians (by name) | none (searched) |
| KartKraft | KartKraft | Native (direct) | `Motion.WheelsArray[i].SlipAngle` | unverified | unverified | `Motion.WheelsArray[i].Surface` (enum) |
| BusBound | Bus Bound | Native (direct) | `Telemetry.Bus.Wheels[i].SlipAngle` | unverified | unverified | `Telemetry.Bus.Wheels[i].Ground` (`GroundType`) |

Raw data also holds, but the effect should not use for the angle: PMR `m_chassis.m_sideslip`
(body slip), Drift21 `DriftInfos.DriftAngle`, and generic `WheelSlip` fields.

### Native (computed)

| Id | Game | Verdict | Fields | Wheel order | Unit | Per-wheel surface |
|---|---|---|---|---|---|---|
| RFactor2 | rFactor 2 | Native (computed) | `CurrentPlayerTelemetry.mWheels[i].mLateralPatchVel`, `.mLongitudinalPatchVel`, `.mLateralGroundVel`, `.mLongitudinalGroundVel` (double); `atan2(lat patch vel, abs(lon ground vel))` | FeedbackData index | radians (computed) | `mWheels[i].mSurfaceType` (byte; enum Dry, Wet, Grass, Dirt, Gravel, Kerb, Special) |
| LMU | Le Mans Ultimate | Native (computed) | `PlayerNativeTelemetry.mWheel[i].<same four fields>` (exposure under GameRawData unconfirmed: member is `JsonIgnore`, not `DoNotExpose`) | FeedbackData index | radians (computed) | `mWheel[i].mSurfaceType` (byte) |
| NASCARHeat4 | NASCAR Heat 4 | Native (computed), PROVISIONAL | `LCarPacket.wheel_{lf,rf,lr,rr}.lat_terrain_speed`, `.lon_terrain_speed` (double); semantics unverified | named LF,RF,LR,RR | unverified | `LCarPacket.wheel_<w>.surface` (`TelemetrySurfaceType`) |
| NASCARHeat5 | NASCAR Heat 5 | same (subclass of Heat 4 reader) | same | same | unverified | same |
| TonyStewartsAllAmericanRacing | Tony Stewart's All-American Racing | same | same | same | unverified | same |
| TonyStewartsSprintCarRacing | Tony Stewart's Sprint Car Racing | same | same | same | unverified | same |
| SRXTheGame | SRX: The Game | same | same | same | unverified | same |

### Fallback only (vehicles with tyres)

| Id | Game | Verdict | Slip angle / patch velocity | Wheel order | Unit | Per-wheel surface |
|---|---|---|---|---|---|---|
| AssettoCorsa | Assetto Corsa | Fallback only | none (searched `Physics`: `WheelSlip`, `TyreContact*`, no patch velocity; `slipAngle` exists only in the UDP datagram `RTCarInfo`, not in `ACRawData`) | n/a | n/a | none (searched) |
| Automobilista2 | Automobilista 2 | Fallback only | none (searched `AMS2APIStruct`: `mTyreSlipSpeed[4]` is a slip speed; no per-wheel lateral velocity) | n/a | n/a | `mTerrain[4]` (uint, `eTerrain` enum), FeedbackData index |
| Automobilista2UDP (sub-reader) | Automobilista 2 UDP | Fallback only | none (searched `PC2UDPTelemetryContainer`) | n/a | n/a | `sTelemetryData.sTerrain[k]` |
| PCars | Project Cars | Fallback only | none (searched `PCarsAPIStruct`: `mTyreSlipSpeed`) | n/a | n/a | `mTerrain[4]` |
| PCars2 | Project Cars 2 | Fallback only | none (searched `PCars2APIStruct`) | n/a | n/a | `mTerrain[4]` |
| PCars3 | Project Cars 3 | Fallback only | none (subclass of the PC2 reader, same struct) | n/a | n/a | `mTerrain[4]` |
| PCars2UDP / PCars3UDP (sub-readers) | Project Cars 2 / 3 UDP | Fallback only | none (searched) | n/a | n/a | `sTelemetryData.sTerrain[k]` |
| IRacing | iRacing | Unverified | unverified | unverified | unverified | unverified |
| RRRE | RaceRoom Racing Experience | Fallback only | none (searched `Shared`: `TireGrip`, `TireSpeed`) | n/a | n/a | none (searched) |
| RFactor1 | rFactor | Fallback only | none (searched `rfWheel`: no patch velocity) | n/a | n/a | `Data.wheel[i].surfaceType` |
| StockCarExtreme | Stock Car Extreme | Fallback only | none (rF1 reader) | n/a | n/a | `Data.wheel[i].surfaceType` |
| CopaPetrobrasDeMarcas | Copa Petrobras de Marcas | Fallback only | none (rF1 reader) | n/a | n/a | `Data.wheel[i].surfaceType` |
| FormulaTruck2013 | Formula Truck 2013 | Fallback only | none (rF1 reader) | n/a | n/a | `Data.wheel[i].surfaceType` |
| Automobilista | Automobilista 1 | Fallback only | none (searched AMS1 `rfWheel`) | n/a | n/a | `Data.wheel[i].surfaceType` |
| SIMBINGTR2 | SimBin GTR 2 | Fallback only | none (rF1-struct reader) | n/a | n/a | `Data.wheel[i].surfaceType` |
| SIMBINGTR2SharedMemory (sub-reader) | SimBin GTR 2 shared memory | Fallback only | none (searched `GTR2Wheel`) | n/a | n/a | `mWheel[i].mSurfaceType` |
| SIMBINRACE07 | SimBin Race 07 | Fallback only | none (searched `SimBinMMF`) | n/a | n/a | none (searched) |
| VolvoTheGame | Volvo The Game | Fallback only | none (`SimBinMMF`) | n/a | n/a | none (searched) |
| SIMBINRACEWTCC | SimBin Race The WTCC Game | Fallback only | none (`SimBinMMF`) | n/a | n/a | none (searched) |
| SIMBINGTLEGENDS | SimBin GT Legends | Fallback only | none (`SimBinMMF`) | n/a | n/a | none (searched) |
| SIMBINGTR | SimBin GTR | Fallback only | none (`SimBinMMF`) | n/a | n/a | none (searched) |
| BMWM3CHALLENGE | BMW M3 Challenge | Fallback only | none (`SimBinMMF`) | n/a | n/a | none (searched) |
| CodemastersDirt2 | Dirt 2 | Fallback only | none (searched `TelemetryPacketExtradata1`) | n/a | n/a | none (searched) |
| CodemastersDirt3 | Dirt 3 | Fallback only | none (same family) | n/a | n/a | none (searched) |
| CodemastersDirtShowdown | Dirt Showdown | Fallback only | none (Codemasters legacy reader) | n/a | n/a | none (searched) |
| CodemastersDirt4 | Dirt 4 | Fallback only | none (searched `TelemetryPacket`) | n/a | n/a | none (searched) |
| CodemastersDirtRally | Dirt Rally | Fallback only | none (`TelemetryPacket`) | n/a | n/a | none (searched) |
| CodemastersDirtRally2 | Dirt Rally 2.0 | Fallback only | none (`TelemetryPacket`: only per-wheel suspension and wheel speed) | n/a | n/a | none (searched) |
| WRCGenerations | WRC Generations | Fallback only | none (legacy `TelemetryPacket` reader) | n/a | n/a | none (searched) |
| TDUSC | Test Drive Unlimited Solar Crown | Fallback only | none (same legacy reader) | n/a | n/a | none (searched) |
| EAWRC23 | EA WRC | Fallback only | none (searched `SessionUpdate`: only `vehicle_cp_forward_speed_{bl,br,fl,fr}`, no lateral) | n/a | n/a | none (searched; `vehicle_tyre_state_*` is tyre state) |
| WRCX | WRC 7/8/9 | Fallback only | none (searched `WrcTelemetry`, 12 members, no wheels) | n/a | n/a | none (searched) |
| WRC10 | WRC 10 | Fallback only | none (same reader) | n/a | n/a | none (searched) |
| F12012 / F12013 / F12014 / F12015 / F12016 | Codemasters F1 2012 to 2016 | Fallback only | none (legacy `TelemetryPacket`) | n/a | n/a | none (searched) |
| F12017 | Codemasters F1 2017 | Fallback only | none (searched `F12017TelemetryPacket`) | n/a | n/a | none (searched) |
| F12018 | Codemasters F1 2018 | Fallback only | none (`m_wheelSlip` is a ratio; no `PacketMotionExData`) | n/a | n/a | none (searched) |
| F12019 | Codemasters F1 2019 | Fallback only | none | n/a | n/a | `PlayerCarTelemetryData.m_surfaceType[4]` |
| F12020 | Codemasters F1 2020 | Fallback only | none | n/a | n/a | same |
| F12021 | Codemasters F1 2021 | Fallback only | none | n/a | n/a | same |
| F12022 | Codemasters F1 22 | Fallback only | none (first slip angle is F1 23) | n/a | n/a | same |
| CodemastersGrid2 | Grid 2 | Fallback only | none (`TelemetryPacketExtradata1`) | n/a | n/a | none (searched) |
| CodemastersGridAutosport | Grid Autosport | Fallback only | none (`TelemetryPacket`) | n/a | n/a | none (searched) |
| CodemastersGrid2019 | Grid (2019) | Fallback only | none | n/a | n/a | none (searched) |
| CodemastersGridLegends | Grid Legends | Fallback only | none | n/a | n/a | none (searched) |
| RBR | Richard Burns Rally | Fallback only | none (searched `RBRData`: speed, spin, position only) | n/a | n/a | none (searched) |
| GRally | GRally | Fallback only | none (searched `gRally_Telemetry`: pressure and temperatures per wheel) | n/a | n/a | none (searched) |
| GranTurismo7 | Gran Turismo 7 | Fallback only | none (searched `GT7LatestPacket`: `Wheel_Slip`, `Wheel_Speed`) | n/a | n/a | `surfaceType[4]` (`SurfaceTypeID`: kerb, dirt, grass, sand, snow, tarmac; FeedbackData index) |
| GranTurismo7Legacy (sub-reader) | Gran Turismo 7 legacy | Fallback only | none (searched `GTPacket`) | n/a | n/a | none (searched) |
| GranTurismoSport | Gran Turismo Sport | Fallback only | none (assumed `GTPacket`; unverified) | n/a | n/a | unverified |
| Trackmania | Trackmania Series | Fallback only | none (searched `SVehicleState`: `WheelsIsSliping` bool) | n/a | n/a | none (searched; `WheelsIsGroundContact` only) |
| TrackmaniaTurbo | Trackmania Turbo | Fallback only | none (same reader) | n/a | n/a | none (searched) |
| BeamNgDrive | BeamNG Drive (extra feed) | Fallback only | none (`wheel_slip_{fl,fr,rl,rr}` exists; meaning not stated, unverified) | named fl,fr,rl,rr | unverified | `wheel_contactMaterial_{fl,fr,rl,rr}` (int) |
| BeamNgOutgauge (sub-reader) | BeamNG OutGauge | Fallback only | none (searched OutGauge) | n/a | n/a | none (searched) |
| KartRacingPro | Kart Racing Pro | Fallback only | none (searched `SProxyData_t`) | n/a | n/a | `m_sData.m_aiWheelMaterial[]` |
| GPBikes | GP Bikes | Fallback only | none (searched) | n/a | n/a | `m_sData.m_aiWheelMaterial[2]` (front, rear) |
| MXBikes | MX Bikes | Fallback only | none (searched) | n/a | n/a | `m_sData.m_aiWheelMaterial[]` |
| MOTOGP18 | MotoGP 18 | Fallback only | none (searched `MotoGP18Packet`: `WheelSlipF/R`, `LateralGforce`) | n/a | n/a | none (searched) |
| FURIDASHIDriftCyberSport | FURIDASHI: Drift Cyber Sport | Fallback only | none (searched; `traction_loss` scalar) | n/a | n/a | none (searched) |
| RDSTheOfficialDriftVideoGame | RDS Drift Video Game | Fallback only | none (subclass of Furidashi reader) | n/a | n/a | none (searched) |
| NR2003 | NASCAR Racing 2003 Season | Fallback only | none (searched) | n/a | n/a | none (searched) |
| ETS2 | Euro Truck Simulator 2 | Fallback only | none (searched `TelemetryEx`) | n/a | n/a | `Telemetry.TruckValues.CurrentValues.WheelsValues.Substance` (uint[]) |
| ATS | American Truck Simulator | Fallback only | none (same reader) | n/a | n/a | same |
| FernbusSimulator | TML Fernbus Simulator | Fallback only | none (searched `Vehicle.Wheels[i]`: suspension, location, OnGround) | n/a | n/a | none (searched) |
| TouristBusSimulator | TML Tourist Bus Simulator | Fallback only | same reader | n/a | n/a | none (searched) |
| TMLTheBus | TML The Bus | Fallback only | same reader | n/a | n/a | none (searched) |
| Rennsport (no registry id literal found; reader `RennsportManager`) | Rennsport | Native (direct) | `TelemetryPoint.{LF,RF,LR,RR}.SlipAngle` | named LF,RF,LR,RR | unverified | `TelemetryPoint.<wheel>.ContactSurface` (byte) |

Additional reader in the package without a registry code of its own: External Sim / custom games
(`ExternalSimManager`, `ControllerGameManager`); their raw data is whatever the external feed
declares, so the verdict is **unverified** (and Fallback only unless the feed supplies slip angle).

### N/A (no wheels or tyres)

| Id | Game | Verdict | Reason (raw-data type searched) |
|---|---|---|---|
| KayakVR | Kayak VR Mirage | N/A | kayak |
| EliteDangerous | Elite Dangerous Odyssey | N/A | space/SRV; no tyre fields |
| TrainSimulator | Train Simulator 21 | N/A | rail |
| FlightSimulator2020, FlightSimulator2024 | MS Flight Simulator 2020, 24 | N/A | aircraft |
| IL2, IL2Series | IL-2 Sturmovik, IL-2 Series | N/A | aircraft |
| DCS | DCS World | N/A | aircraft |
| Condor2, Condor3 | Condor 2, 3 | N/A | glider |
| EpicRollerCoasters, NoLimits2 | Epic Roller Coasters, NoLimits 2 | N/A | coaster |
| FarmingSimulator19, 22, 25 | Farming Simulator | N/A | farm vehicles, no tyre data |
| eXpanSIM | eXpanSIM | N/A | motion rig |
| HydroSim | HydroSim | N/A | boat |
| WARTHUNDER, AcesOfThunder | War Thunder, Aces of Thunder | N/A | aircraft |
| XPlane10, XPlane11, XPlane12 | X-Plane | N/A | aircraft |

(CS:GO, Dota 2, Omsi 2 and ProjectRC readers exist in the assemblies but have no `GameRegistry` id in
the list above; none has tyre slip data.)

## Derived data, units and semantics (all from `GameReaderCommon`)

- `FeedbackData.YawChangeVelocity` is **degrees per second**. Games that supply it convert
  radians to degrees explicitly (R3E, Rennsport, PMR, Condor, DCS, IL2). When a game declares
  `Yaw` but no `YawChangeVelocity` (ACC, AC, AC Rally, AC EVO, rF2, LMU, rF1, AMS1, ...), the base
  class estimates it with `AngularRateFilter` from the yaw orientation in degrees, so the unit is
  again deg/s but it is a smoothed, lagged estimate (smoothing tau 0.06 s). **Sign is not uniform**: PMR
  negates the local Y angular velocity; others pass it through. The sign convention per game is
  **unverified**.
- `FeedbackData.LocalVelocity` is `(Forward, Lateral, Upward)` in **m/s** (`SpeedMPS()` exists on
  it). Readers remap game axes and signs per game (e.g. rF2 passes `(-z, -x, y)`, AMS2
  `(-z, -x, -y)`), so the lateral sign is a per-game convention; the plugin sees only the remapped
  value.
- `FeedbackData.VerticalAcceleration` (float) is populated **only by iRacing and Rennsport** among
  the readers surveyed. For every other game it stays at its default of 0. The unit is not stated in
  the assemblies (iRacing's variable is `VertAccel`; unit **unverified**). The design's "|vertical G − 1 g| > 0.5 g
  small-hop exclusion" therefore cannot rely on this field. A plugin-side alternative is
  `WorldAccelerationZ` / `WorldAccelerationZSmoothed` (the base class already smooths it) or the game's own raw
  acceleration fields.
- `IsFlying` is **derived by SimHub, not read from the game**: `IsFlying = 1` when the smoothed
  `WorldAccelerationZ` average is below -8.5 (free fall, m/s^2 scale). It is not driven by wheel
  contact. `LastFlightStartedAt/EndedAt` track transitions; a re-start within 100 ms of the previous end
  is merged.
- `FlightIsOnGround` is a plain `bool` that is only **set** by flight, RC and kayak readers (Condor,
  DCS, MSFS, IL2, X-Plane, ProjectRC, KayakVR, External Sims) and gated by the capability flag of
  the same name. **For car games it is never written and stays `false`.**
  **Consequence for §9.2: "`FlightIsOnGround == false` means airborne" would permanently gate every car
  game.** The airborne test must be `IsFlying > 0`, and `FlightIsOnGround` may be consulted only when
  `FeedbackCapabilities.FlightIsOnGround` is true.

## Could not determine

- Units of every native slip-angle field except Drift21 (radians, by name), LFS (tangent, by name) and
  the computed rF2/LMU transform. No decompiled code states them. Needs a live capture, or the
  game vendors' shared-memory documentation, which is outside this spike.
- Wheel order for ACR, EVO, PMR, KartKraft, BusBound, rF2/LMU, NASCAR Heat (partly), and any
  "same index as FeedbackData" row, beyond the index equality.
- Whether `PlayerNativeTelemetry` (LMU) and all other paths are actually listed under GameRawData at
  runtime.
- NASCAR Heat `lat_terrain_speed` / `lon_terrain_speed` semantics.
- Whether ACC/ACR/EVO populate `slipAngle` with non-zero data in live play (the struct has the field;
  the §9.1 evidence promotion rule already covers this).
- iRacing raw variable set; BeamNG `wheel_slip_*` meaning; (none else open).

## Scope reached

Complete: all 110 ids in SimHub's `GameRegistry` plus the sub-readers (UDP, spectator, shared-memory
variants) are in the per-game table, each with a verdict. Every `GameManagerBase` /
`SimpleGameManagerBase` subclass in the 22 reader assemblies was searched. Rennsport is a reader
without a registry id in 9.13.2.
