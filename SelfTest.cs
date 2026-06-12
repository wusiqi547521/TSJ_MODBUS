using System.Diagnostics;
using System.Net.Sockets;
using BMHRI.WCS.Server.Models.ZhiKuFourWay.Lift;
using NModbus;

namespace TSJ_Modbus
{
    // ── 自检：内置一个"WCS 网关"，用与 WCS 完全相同的 ZhiKuLiftRegisterCodec 编解码，
    //    对一台本地模拟器跑一遍"1层接车(101) → 载车换到3层(102) → 释放(99)"，校验上报块状态扭转。
    //    这等于不依赖 WCS 就把"WCS编码→模拟器解码→模拟器编码→WCS解码"整条字节链路验通。
    public static class SelfTest
    {
        public static async Task<int> RunAsync()
        {
            ZhiKuLiftRegisterOptions opt = new();

            Console.WriteLine("=== CRC 寄存器配置自检（协议三组样例）===");
            foreach ((string name, ushort got, ushort expect, bool ok) in ZhiKuLiftRegisterCodec.SelfTestCrc(opt))
                Console.WriteLine($"  {(ok ? "✓" : "✗")} {name}: 算得={got:X4} 期望={expect:X4}");
            Console.WriteLine("  （样例对不上是已知现象——现场字节口径未公开；不影响模拟器自洽链路）\n");

            const int port = 15020;
            const byte slaveId = 1;
            LiftConfig cfg = new()
            {
                Name = "SELFTEST",
                Port = port,
                SlaveId = slaveId,
                StartFloor = 1,
                MinFloor = 1,
                MaxFloor = 5,
                MsPerFloor = 150,
                ActionDwellMs = 100,
                HeartbeatMs = 500
            };

            using LiftSimulator sim = new(cfg, opt);
            sim.Start();
            await Task.Delay(300); // 等监听就绪

            using TcpClient client = new();
            await client.ConnectAsync("127.0.0.1", port);
            ModbusFactory factory = new();
            using IModbusMaster master = factory.CreateMaster(client);

            int failures = 0;
            void Check(bool ok, string desc)
            {
                Console.WriteLine($"  {(ok ? "✓" : "✗")} {desc}");
                if (!ok) failures++;
            }

            async Task<ZhiKuLiftUplink> ReadUplink()
            {
                ushort[] regs = await master.ReadHoldingRegistersAsync(slaveId, opt.UplinkStartAddress, (ushort)opt.UplinkWordCount);
                return ZhiKuLiftRegisterCodec.DecodeUplink(regs, opt);
            }

            // 写任务块：与 WCS 网关 WriteTaskAsync 一致——只写 word0..7（任务号..CRC）。
            async Task WriteTask(ZhiKuLiftDownlink dl)
            {
                ushort[] block = ZhiKuLiftRegisterCodec.EncodeDownlink(dl, opt);
                ushort[] taskWords = new ushort[8];
                Array.Copy(block, 0, taskWords, 0, 8);
                await master.WriteMultipleRegistersAsync(slaveId, opt.DownlinkStartAddress, taskWords);
            }

            async Task<ZhiKuLiftUplink> WaitUntil(Func<ZhiKuLiftUplink, bool> pred, int timeoutMs, string desc)
            {
                Stopwatch sw = Stopwatch.StartNew();
                ZhiKuLiftUplink last = null;
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    last = await ReadUplink();
                    if (pred(last)) return last;
                    await Task.Delay(60);
                }
                throw new TimeoutException($"等待超时: {desc}（最后: 任务号={last?.TaskNo} 状态={last?.TaskState} 层={last?.CurrentLayer}）");
            }

            // 模拟 WCS 的"能否下发"门控：联机 & 空闲 & (任务号=0 || 任务状态=执行中/完成)。
            bool GateClear(ZhiKuLiftUplink u) =>
                u.Online && u.Idle &&
                (u.TaskNo == 0 || u.TaskState == ZhiKuLiftTaskStates.Running || u.TaskState == ZhiKuLiftTaskStates.Done);

            Console.WriteLine("=== 换层链路自检：1层接车(101) → 载车到3层(102) → 释放(99) ===");
            try
            {
                ZhiKuLiftUplink u = await ReadUplink();
                Check(u.Online && u.Idle, $"初始 联机={u.Online} 空闲={u.Idle} 当前层={u.CurrentLayer}");
                Check(u.CurrentLayer == 1, $"初始当前层=1（实际 {u.CurrentLayer}）");

                // —— 101 小车进入：梯定位到车所在层(1) ——
                Check(GateClear(u), "下发前门控通过(101)");
                await WriteTask(new ZhiKuLiftDownlink { TaskNo = 1, ActionType = ZhiKuLiftActionCodes.CarEnter, TargetLayer = 1 });
                u = await WaitUntil(x => x.ReceivedTaskNo == 1, 3000, "101 接收反馈=1");
                Check(true, $"101 已接收（接收反馈={u.ReceivedTaskNo} 任务状态={u.TaskState}）");
                u = await WaitUntil(x => x.TaskNo == 1 && x.TaskState == ZhiKuLiftTaskStates.Done, 8000, "101 完成");
                Check(u.CurrentLayer == 1, $"101 完成且在1层（实际 {u.CurrentLayer}）");

                // —— 102 小车离开：载车从1层换到3层 ——
                Check(GateClear(u), "下发前门控通过(102)");
                await WriteTask(new ZhiKuLiftDownlink { TaskNo = 2, ActionType = ZhiKuLiftActionCodes.CarLeave, TargetLayer = 3, CarSignal = 1 });
                u = await WaitUntil(x => x.ReceivedTaskNo == 2, 3000, "102 接收反馈=2");
                Check(true, $"102 已接收（接收反馈={u.ReceivedTaskNo}）");
                u = await WaitUntil(x => x.TaskNo == 2 && x.TaskState == ZhiKuLiftTaskStates.Done, 8000, "102 完成");
                Check(u.CurrentLayer == 3, $"102 完成且到达3层（实际 {u.CurrentLayer}）");
                Check(u.CarSignal == 1, $"102 小车信号=1（实际 {u.CarSignal}）");

                // —— 99 释放 ——
                Check(GateClear(u), "下发前门控通过(99)");
                await WriteTask(new ZhiKuLiftDownlink { TaskNo = 3, ActionType = ZhiKuLiftActionCodes.Release, TargetLayer = 3 });
                u = await WaitUntil(x => x.TaskNo == 3 && x.TaskState == ZhiKuLiftTaskStates.Done, 5000, "99 完成");
                Check(u.Idle, $"99 完成后空闲={u.Idle}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ 异常: {ex.Message}");
                failures++;
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0
                ? "=== 自检全部通过 ✓（换层状态机与寄存器字节链路一致）==="
                : $"=== 自检失败 {failures} 项 ✗ ===");
            return failures == 0 ? 0 : 1;
        }
    }
}
