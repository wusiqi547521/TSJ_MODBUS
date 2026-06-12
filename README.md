# TSJ_Modbus —— 智库提升机 Modbus 模拟器

提升机（TiShengJi）的 Modbus TCP **从站**模拟器，给 WCS 端在没有真机时联调跨层换乘用。

## 它是什么

- 托管一个 Modbus TCP 从站，保持寄存器覆盖 **下发块 41088 / 上报块 41138**（与 `ZhiKuLiftRegisterOptions` 默认一致）。
- 后台状态机：读 WCS 写的任务块 → 校验 CRC → 按动作执行（`101` 定位接车 / `102` 载车换层 / `99` 释放 / `1~5` 取放）→ 模拟升降耗时 → 回写上报块（心跳、空闲、接收反馈、任务号、任务状态、当前层…）。
- 寄存器编解码与 CRC **直接 link 复用 WCS 工程的 `ZhiKuLiftProtocol.cs`**（`<Compile Include=...Link=...>`），保证字节口径与真机/WCS 完全一致，绝不会出现"模拟器能跑、真机对不上"的假绿。

## 运行

```bash
# 自检（不依赖 WCS，自己当网关跑通 1层接车→载车到3层→释放 整条链路）
dotnet run -- selftest

# 正常模拟（按 appsettings.json 的 Lifts 启动，等 WCS 连入；Ctrl+C 退出）
dotnet run
```

## 配置 `appsettings.json`

```jsonc
{
  "Lifts": [
    {
      "Name": "LIFT1",
      "Port": 502,        // 该梯监听端口（WCS 端 ZK_Lift.Port 对应）
      "SlaveId": 1,       // Modbus 从站号（WCS 端 ZK_Lift.SlaveId 对应）
      "StartFloor": 1,    // 起始所在层
      "MinFloor": 1,
      "MaxFloor": 5,
      "MsPerFloor": 1500, // 每升降一层耗时
      "ActionDwellMs": 800, // 到位后取放/进出动作停留
      "HeartbeatMs": 1000   // 心跳切换周期
    }
  ]
}
```

多台梯 = 在 `Lifts` 里加多项（各用不同 `Port`）。

## 接 WCS

WCS 端 `ZK_Lift` 表配一行：`Host=127.0.0.1`、`Port`=上面的端口、`SlaveId` 对应、`Enabled=1`。
地图上把该梯各层平台格设 `角色=提升机` 并把 `DeviceId` 填成同一个梯号即可（见提升机集成说明）。

## 注意

- CRC 自检里那三组协议样例（807E/813E/41FF）算不上——这是**已知现象**，现场集成商算 CRC 的字节口径未公开。
  但模拟器与 WCS 用同一份 `ZhiKuLiftRegisterCodec`，两边一致自洽，联调不受影响；上真机时再用
  `ZhiKuLiftRegisterCodec.SelfTestCrc()` 对现场 PLC，必要时翻 `DIntLowWordFirst / CrcBigEndianBytes / CrcByteSwap`。
- 模拟器不建模"小车是否真在梯上"，接到任务即把下发的小车/货物信号镜像回上报（模拟传感器一致）。
