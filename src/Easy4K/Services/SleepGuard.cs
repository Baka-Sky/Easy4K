using System.Runtime.InteropServices;

namespace Easy4K.Services;

/// <summary>阻止 Windows 自动休眠（不阻止关闭显示器或自动锁屏）。
/// SetThreadExecutionState 的持续状态挂在调用线程上，必须在长期存活的主 UI 线程调用；
/// 关闭开关或进程退出后，系统会自动恢复默认电源策略。</summary>
public static class SleepGuard
{
    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        Continuous = 0x80000000
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(ExecutionState flags);

    /// <summary>当前是否已提交休眠阻止请求（用于避免重复调用）</summary>
    public static bool Enabled { get; private set; }

    /// <summary>按开关状态提交或撤销休眠阻止请求。</summary>
    public static void SetEnabled(bool enabled)
    {
        if (enabled == Enabled) return;
        Enabled = enabled;
        try
        {
            SetThreadExecutionState(enabled
                ? ExecutionState.Continuous | ExecutionState.SystemRequired
                : ExecutionState.Continuous);
        }
        catch
        {
            // 电源接口调用失败不应影响主流程
        }
    }
}
