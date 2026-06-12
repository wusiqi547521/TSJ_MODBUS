using BMHRI.WCS.Server.Models.ZhiKuFourWay.Lift;
using Microsoft.Extensions.Configuration;
using TSJ_Modbus;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// 自检模式：dotnet run -- selftest（不依赖 WCS，自己当网关跑通换层链路）
if (args.Length > 0 && string.Equals(args[0], "selftest", StringComparison.OrdinalIgnoreCase))
    return await SelfTest.RunAsync();

IConfigurationRoot config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

List<LiftConfig> lifts = config.GetSection("Lifts").Get<List<LiftConfig>>() ?? new List<LiftConfig>();
if (lifts.Count == 0)
{
    Console.WriteLine("appsettings.json 的 Lifts 为空，无可启动的提升机。");
    return 1;
}

List<LiftSimulator> sims = new();
foreach (LiftConfig cfg in lifts)
{
    try
    {
        LiftSimulator sim = new(cfg);
        sim.Start();
        sims.Add(sim);
        Console.WriteLine($"[{cfg.Name}] 提升机模拟器已启动  端口={cfg.Port} 从站={cfg.SlaveId} 起始层={cfg.StartFloor} 层范围={cfg.MinFloor}~{cfg.MaxFloor} 每层={cfg.MsPerFloor}ms");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{cfg.Name}] 启动失败（端口 {cfg.Port} 可能被占用）: {ex.Message}");
    }
}

if (sims.Count == 0)
    return 1;

Console.WriteLine($"\n共 {sims.Count} 台运行中。WCS 端 ZK_Lift 配 Host=127.0.0.1 / 对应 Port / SlaveId 即可连入。");
Console.WriteLine("Ctrl+C 退出。每 2 秒刷新一次状态：\n");

using CancellationTokenSource exitCts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; exitCts.Cancel(); };

try
{
    while (!exitCts.IsCancellationRequested)
    {
        Console.WriteLine($"── {DateTime.Now:HH:mm:ss} ──────────────────────────────────────────────");
        Console.WriteLine($"{"梯",-10}{"心跳",-6}{"层",-5}{"任务号",-8}{"状态",-6}{"动作",-6}{"运行",-6}{"空闲",-6}{"报警",-6}");
        foreach (LiftSimulator sim in sims)
        {
            ZhiKuLiftUplink s = sim.Snapshot;
            Console.WriteLine($"{sim.Name,-10}{s.Heartbeat,-6}{s.CurrentLayer,-5}{s.TaskNo,-8}{TaskStateName(s.TaskState),-6}{s.ActionType,-6}{RunStateName(s.RunState),-6}{(s.Idle ? "是" : "否"),-6}{s.AlarmCode,-6}");
        }
        Console.WriteLine();
        try { await Task.Delay(2000, exitCts.Token); } catch (OperationCanceledException) { break; }
    }
}
finally
{
    Console.WriteLine("正在停止…");
    foreach (LiftSimulator sim in sims) sim.Dispose();
}
return 0;

static string TaskStateName(int s) => s switch
{
    ZhiKuLiftTaskStates.NotDone => "未做",
    ZhiKuLiftTaskStates.Running => "执行",
    ZhiKuLiftTaskStates.Done => "完成",
    _ => s.ToString()
};

static string RunStateName(int s) => s switch
{
    ZhiKuLiftRunStates.Stopped => "停",
    ZhiKuLiftRunStates.ChainForward => "链正",
    ZhiKuLiftRunStates.ChainReverse => "链反",
    ZhiKuLiftRunStates.Ascending => "上升",
    ZhiKuLiftRunStates.Descending => "下降",
    ZhiKuLiftRunStates.SafetyStopping => "安停",
    _ => s.ToString()
};
