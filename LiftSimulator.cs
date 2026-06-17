using System.Net;
using System.Net.Sockets;
using BMHRI.WCS.Server.Models.ZhiKuFourWay.Lift;
using NModbus;

namespace TSJ_Modbus
{
    // 一台提升机的配置（来自 appsettings.json 的 Lifts[]）。
    public sealed class LiftConfig
    {
        public string Name { get; set; } = "LIFT1";
        public int Port { get; set; } = 502;
        public byte SlaveId { get; set; } = 1;
        public int StartFloor { get; set; } = 1;
        public int MinFloor { get; set; } = 1;
        public int MaxFloor { get; set; } = 5;
        public int MsPerFloor { get; set; } = 1500;   // 每层升降耗时
        public int ActionDwellMs { get; set; } = 800; // 到位后取放/进出动作停留
        public int HeartbeatMs { get; set; } = 1000;  // 心跳切换周期
    }

    // ── 提升机 Modbus TCP 从站 + 状态机 ──
    // 托管一个 Modbus TCP 从站（保持寄存器覆盖下发块 41088 / 上报块 41138），后台循环：
    //   读下发块 → 校验 CRC → 接收/执行任务（101 定位接车 / 102 载车换层 / 99 释放 / 1~5 取放）
    //   → 模拟升降 → 写上报块。寄存器编解码与 CRC 复用 WCS 端 ZhiKuLiftRegisterCodec，字节口径一致。
    public sealed class LiftSimulator : IDisposable
    {
        private readonly LiftConfig cfg;
        private readonly ZhiKuLiftRegisterOptions opt;
        private readonly ZhiKuLiftUplink state;

        private TcpListener listener;
        private IModbusSlaveNetwork network;
        private IModbusSlave slave;
        private CancellationTokenSource cts;
        private Task pumpTask;

        // 执行态（不进寄存器，纯内存）
        private int acceptedTaskNo;          // 已接收并在处理/已完成的任务号（去重用）
        private int moveTargetFloor;
        private DateTime? arriveAtUtc;        // 预计到达目标层的时刻
        private DateTime? actionDoneUtc;      // 到位后动作完成的时刻
        private double floorProgressFrom;     // 本次移动起点层
        private DateTime moveStartUtc;
        private int totalFloors;

        private int heartbeatCounter;
        private DateTime lastHeartbeatUtc;
        private int lastLoggedAlarm = -1;

        public string Name => cfg.Name;
        public ZhiKuLiftUplink Snapshot => state;

        public LiftSimulator(LiftConfig config, ZhiKuLiftRegisterOptions registerOptions = null)
        {
            cfg = config ?? throw new ArgumentNullException(nameof(config));
            opt = registerOptions ?? new ZhiKuLiftRegisterOptions();
            state = new ZhiKuLiftUplink
            {
                Online = true,
                Idle = true,
                CurrentLayer = cfg.StartFloor,
                // 电阻挡恒"落下"：WCS 在车进/出平台前会校验电阻挡=落下才发车。模拟器不模拟"行程中升起防滑"
                // 的物理，保持落下即可让进梯/出梯两道闸都通过（真机是到层落下，行为一致）。
                ResistanceState = ZhiKuLiftResistanceStates.Lowered,
                RunState = ZhiKuLiftRunStates.Stopped,
                InPosition = true,
                PalletType = 1
            };
        }

        public void Start()
        {
            listener = new TcpListener(IPAddress.Any, cfg.Port);
            listener.Start();
            ModbusFactory factory = new();
            network = factory.CreateSlaveNetwork(listener);
            slave = factory.CreateSlave(cfg.SlaveId);
            network.AddSlave(slave);

            // 初始上报块写一次，连上即有有效状态。
            WriteUplink();

            cts = new CancellationTokenSource();
            _ = network.ListenAsync(cts.Token);             // Modbus 请求接收循环
            pumpTask = Task.Run(() => PumpLoopAsync(cts.Token)); // 状态机循环
        }

        private async Task PumpLoopAsync(CancellationToken token)
        {
            lastHeartbeatUtc = DateTime.UtcNow;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    Tick();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{cfg.Name}] tick error: {ex.Message}");
                }

                try { await Task.Delay(100, token); }
                catch (OperationCanceledException) { break; }
            }
        }

        // 一拍：心跳 → 读下发 → 控制字 → 任务 FSM → 写上报。
        private void Tick()
        {
            DateTime now = DateTime.UtcNow;

            // 心跳 0~127
            if ((now - lastHeartbeatUtc).TotalMilliseconds >= cfg.HeartbeatMs)
            {
                heartbeatCounter = (heartbeatCounter + 1) & 0x7F;
                state.Heartbeat = heartbeatCounter;
                lastHeartbeatUtc = now;
            }

            ushort[] dlWords = slave.DataStore.HoldingRegisters.ReadPoints(
                opt.DownlinkStartAddress, (ushort)opt.DownlinkWordCount);
            ZhiKuLiftDownlink dl = ZhiKuLiftRegisterCodec.DecodeDownlink(dlWords, opt);

            HandleControl(dl);

            // 安全停止中：不接新任务、不推进，等取消安全停止。
            if (state.RunState == ZhiKuLiftRunStates.SafetyStopping)
            {
                WriteUplink();
                return;
            }

            // 接收新任务（任务号有值、与上次不同、当前不在执行中）。
            bool hasTask = dl.TaskNo != 0;
            bool isNew = hasTask && dl.TaskNo != acceptedTaskNo;
            if (isNew && state.TaskState != ZhiKuLiftTaskStates.Running)
            {
                if (!ZhiKuLiftRegisterCodec.VerifyDownlinkCrc(dlWords, opt))
                {
                    if (lastLoggedAlarm != 10)
                    {
                        Console.WriteLine($"[{cfg.Name}] ✗ CRC 校验失败，拒收任务 {dl.TaskNo}（检查寄存器字序/字节序配置）");
                        lastLoggedAlarm = 10;
                    }
                    state.AlarmCode = 10; // CRC 错误
                }
                else
                {
                    AcceptTask(dl, now);
                }
            }

            if (state.TaskState == ZhiKuLiftTaskStates.Running)
                ProgressExecution(now);

            WriteUplink();
        }

        private void HandleControl(ZhiKuLiftDownlink dl)
        {
            if (dl.UpperEStop == ZhiKuLiftEStopCodes.HardStop || dl.UpperEStop == ZhiKuLiftEStopCodes.SoftStop)
            {
                if (state.RunState != ZhiKuLiftRunStates.SafetyStopping)
                    Console.WriteLine($"[{cfg.Name}] ⚠ 上位急停({dl.UpperEStop}) → 安全停止中");
                state.RunState = ZhiKuLiftRunStates.SafetyStopping;
                return;
            }

            if (dl.ResetSignal == ZhiKuLiftResetCodes.Reset || dl.ResetSignal == ZhiKuLiftResetCodes.CancelSafetyStop)
            {
                if (state.RunState == ZhiKuLiftRunStates.SafetyStopping || state.AlarmCode != 0)
                    Console.WriteLine($"[{cfg.Name}] ↻ 复位({dl.ResetSignal}) → 清报警/解除安全停止");
                if (state.RunState == ZhiKuLiftRunStates.SafetyStopping)
                    state.RunState = ZhiKuLiftRunStates.Stopped;
                state.AlarmCode = 0;
                lastLoggedAlarm = -1;
            }
        }

        private void AcceptTask(ZhiKuLiftDownlink dl, DateTime now)
        {
            acceptedTaskNo = dl.TaskNo;
            state.ReceivedTaskNo = dl.TaskNo;   // 接收任务反馈（不清除，覆盖上一个）
            state.TaskNo = dl.TaskNo;
            state.TaskState = ZhiKuLiftTaskStates.Running;
            state.ActionType = dl.ActionType;
            state.TargetLayer = dl.TargetLayer;
            state.CarSignal = dl.CarSignal;     // 模拟前后光电/超声与下发一致
            state.GoodsSignal = dl.GoodsSignal;
            state.PalletType = dl.PalletType <= 0 ? 1 : dl.PalletType;
            state.Idle = false;
            state.InPosition = false;
            state.AlarmCode = 0;
            lastLoggedAlarm = -1;

            string actionName = ActionName(dl.ActionType);
            if (dl.ActionType == ZhiKuLiftActionCodes.Release)
            {
                // 释放：不升降，短停后完成。
                moveTargetFloor = state.CurrentLayer;
                arriveAtUtc = now;
                actionDoneUtc = now.AddMilliseconds(cfg.ActionDwellMs);
                state.RunState = ZhiKuLiftRunStates.Stopped;
            }
            else
            {
                // 101/102/1~5：升降到目标层 + 到位动作停留。
                moveTargetFloor = Clamp(dl.TargetLayer <= 0 ? state.CurrentLayer : dl.TargetLayer);
                floorProgressFrom = state.CurrentLayer;
                moveStartUtc = now;
                totalFloors = Math.Abs(moveTargetFloor - state.CurrentLayer);
                int travelMs = totalFloors * cfg.MsPerFloor;
                arriveAtUtc = now.AddMilliseconds(travelMs);
                actionDoneUtc = arriveAtUtc.Value.AddMilliseconds(cfg.ActionDwellMs);
                state.RunState = moveTargetFloor > state.CurrentLayer
                    ? ZhiKuLiftRunStates.Ascending
                    : moveTargetFloor < state.CurrentLayer
                        ? ZhiKuLiftRunStates.Descending
                        : ZhiKuLiftRunStates.Stopped;
            }

            Console.WriteLine($"[{cfg.Name}] ✓ 收到任务 {dl.TaskNo} 动作={dl.ActionType}({actionName}) 目标层={dl.TargetLayer} 车={dl.CarSignal} 货={dl.GoodsSignal}（当前层={state.CurrentLayer}）");
        }

        private void ProgressExecution(DateTime now)
        {
            // 升降进度：按耗时线性插值当前层（仅显示用，整数层在到位时落定）。
            if (arriveAtUtc.HasValue && now < arriveAtUtc.Value && totalFloors > 0)
            {
                double frac = (now - moveStartUtc).TotalMilliseconds / Math.Max(1, totalFloors * cfg.MsPerFloor);
                double cur = floorProgressFrom + (moveTargetFloor - floorProgressFrom) * Math.Clamp(frac, 0, 1);
                state.CurrentLayer = (int)Math.Round(cur);
                state.InPosition = false;
                return;
            }

            // 已到目标层。
            state.CurrentLayer = moveTargetFloor;
            state.RunState = ZhiKuLiftRunStates.Stopped;
            state.InPosition = true;

            // 到位后再停留 ActionDwellMs 完成动作。
            if (actionDoneUtc.HasValue && now < actionDoneUtc.Value)
                return;

            state.TaskState = ZhiKuLiftTaskStates.Done;
            state.Idle = true;
            arriveAtUtc = null;
            actionDoneUtc = null;
            Console.WriteLine($"[{cfg.Name}] ✔ 任务 {state.TaskNo} 完成（动作={state.ActionType}/{ActionName(state.ActionType)} 当前层={state.CurrentLayer}）");
        }

        private void WriteUplink()
        {
            state.LayerHeightMm = state.CurrentLayer * 3000;
            ushort[] words = ZhiKuLiftRegisterCodec.EncodeUplink(state, opt);
            slave.DataStore.HoldingRegisters.WritePoints(opt.UplinkStartAddress, words);
        }

        private int Clamp(int floor) => Math.Clamp(floor, cfg.MinFloor, cfg.MaxFloor);

        // 外部注入：报警 / 解除安全停止（控制台按键测试用）。
        public void InjectAlarm(int code)
        {
            state.AlarmCode = code;
            Console.WriteLine($"[{cfg.Name}] ⚠ 注入报警 code={code}");
        }

        private static string ActionName(int action) => action switch
        {
            ZhiKuLiftActionCodes.Move => "移动",
            ZhiKuLiftActionCodes.LeftPick => "左取",
            ZhiKuLiftActionCodes.LeftUnload => "左卸",
            ZhiKuLiftActionCodes.RightPick => "右取",
            ZhiKuLiftActionCodes.RightUnload => "右卸",
            ZhiKuLiftActionCodes.Release => "释放",
            ZhiKuLiftActionCodes.CarEnter => "小车进入",
            ZhiKuLiftActionCodes.CarLeave => "小车离开",
            _ => $"#{action}"
        };

        public void Dispose()
        {
            try { cts?.Cancel(); } catch { }
            try { pumpTask?.Wait(500); } catch { }
            try { network?.Dispose(); } catch { }
            try { listener?.Stop(); } catch { }
            try { cts?.Dispose(); } catch { }
        }
    }
}
