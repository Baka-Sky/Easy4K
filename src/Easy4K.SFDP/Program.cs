using System.Text.Encodings.Web;
using System.Text.Json;

// Easy4K.SFDP —— 相邻帧去重（论文「四阶级联判决」）的独立命令行工具。
// 算法与 Easy4K 主程序是同一份（FrameDedupService），这里只提供命令行 + JSON 契约，
// 方便别的程序直接调用：不用引用 WinUI 工程，也不需要 Windows App SDK。
//
// 输出约定（stdout，一行一条 JSON；stderr 只放人类可读的错误）：
//   {"type":"progress","phase":"判决阶段(dHash)","done":16,"total":750,"duplicateCount":3, ...}
//   {"type":"result","total":750,"duplicateCount":102,"keptCount":648,"dedupRate":0.136,"expandMap":[...],"decisions":[...]}
//   （带 --out 时第二条改成 {"type":"summary",...,"outFile":"..."}，完整结果写进文件）
//
// 退出码：0 成功 / 2 参数错误 / 3 目录里没有 PNG 帧 / 130 用户取消 / 1 其他失败。

Console.OutputEncoding = System.Text.Encoding.UTF8;

string? framesDir = null;
var mode = "performance";
var smoothing = true;
var quiet = false;
string? outPath = null;

var idx = 0;
while (idx < args.Length)
{
    var a = args[idx];
    switch (a)
    {
        case "--frames" or "-f":
            framesDir = Take();
            break;
        case "--mode" or "-m":
            mode = Take();
            break;
        case "--out" or "-o":
            outPath = Take();
            break;
        case "--no-smoothing":
            smoothing = false;
            break;
        case "--quiet" or "-q":
            quiet = true;
            break;
        case "--help" or "-h" or "/?":
            PrintHelp();
            return 0;
        default:
            Console.Error.WriteLine($"未知参数：{a}");
            PrintHelp();
            return 2;
    }
    idx++;

    // 取下一个参数作为选项值（越界返回空串，交给后面的校验报错）
    string Take() => idx + 1 < args.Length ? args[++idx] : "";
}

if (string.IsNullOrWhiteSpace(framesDir))
{
    Console.Error.WriteLine("缺少 --frames <帧目录>");
    PrintHelp();
    return 2;
}
if (mode is not ("performance" or "uhd"))
{
    Console.Error.WriteLine($"--mode 只接受 performance / uhd，收到：{mode}");
    return 2;
}
if (!Directory.Exists(framesDir))
{
    Console.Error.WriteLine($"目录不存在：{framesDir}");
    return 2;
}
if (Directory.GetFiles(framesDir, "*.png").Length == 0)
{
    Console.Error.WriteLine($"目录里没有 PNG 帧：{framesDir}");
    return 3;
}

var json = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
var opt = Easy4K.SFDP.FrameDedupService.Options.FromMode(mode);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // 不让进程被硬杀，走正常的取消路径（能删干净、退出码 130）
    cts.Cancel();
};

try
{
    var result = await Easy4K.SFDP.FrameDedupService.AnalyzeAsync(framesDir, opt, p =>
    {
        if (quiet) return;
        Console.Out.WriteLine(JsonSerializer.Serialize(new
        {
            type = "progress",
            phase = p.Phase,
            done = p.Done,
            total = p.Total,
            duplicateCount = p.DuplicateCount,
            currentIndex = p.CurrentIndex,
            currentFrame = p.CurrentFramePath,
            compareIndex = p.CompareIndex,
            compareFrame = p.CompareFramePath,
            verdictIndex = p.VerdictIndex,
            verdictFrame = p.VerdictFramePath,
        }, json));
    }, cts.Token, smoothing);

    var payload = JsonSerializer.Serialize(new
    {
        type = "result",
        mode,
        total = result.Total,
        duplicateCount = result.DuplicateCount,
        keptCount = result.KeptCount,
        dedupRate = result.DedupRate,
        expandMap = result.ExpandMap,
        decisions = result.Decisions.Select(d => new
        {
            index = d.Index,
            duplicate = d.Duplicate,
            mad = d.Mad,
            hamming = d.Hamming,
            qr = d.Qr,
            meanFlow = d.MeanFlow,
            varFlow = d.VarFlow,
            decided = d.Decided.ToString(),
        }),
    }, json);

    if (!string.IsNullOrWhiteSpace(outPath))
    {
        var full = Path.GetFullPath(outPath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(full, payload);
        Console.Out.WriteLine(JsonSerializer.Serialize(new
        {
            type = "summary",
            mode,
            total = result.Total,
            duplicateCount = result.DuplicateCount,
            keptCount = result.KeptCount,
            dedupRate = result.DedupRate,
            outFile = full,
        }, json));
    }
    else
    {
        Console.Out.WriteLine(payload);
    }
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("已取消");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"失败：{ex.Message}");
    return 1;
}

static void PrintHelp()
{
    Console.Error.WriteLine("""
        Easy4K.SFDP —— 相邻帧去重（四阶级联判决）

        用法：
          Easy4K.SFDP --frames <帧目录> [选项]

        选项：
          -f, --frames <目录>   必填，目录里放 00000001.png 这类连续帧
          -m, --mode <档位>     performance（默认，前两阶）| uhd（完美模式，四阶全开）
              --no-smoothing    跳过时序平滑（由调用方自己按全局序列处理时用）
          -o, --out <文件>      把完整结果（含逐帧判决与回填表）写进 JSON 文件
          -q, --quiet           不输出进度行
          -h, --help            显示本帮助

        输出：stdout 一行一条 JSON（progress / result 或 summary）；stderr 只放错误。
        """);
}
