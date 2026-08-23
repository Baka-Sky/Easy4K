namespace Easy4K.Models;

/// <summary>处理流水线阶段</summary>
public enum ProcessStage
{
    Idle,
    Splitting,
    SuperRes,
    Interpolating,
    Merging,
    AddingAudio,
    HdrConverting,
    Done,
    Failed,
    Stopped
}

/// <summary>日志级别</summary>
public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,
    Command
}

/// <summary>UI 语言</summary>
public enum AppLanguage
{
    ZhCn,
    EnUs
}

/// <summary>UI 主题</summary>
public enum AppTheme
{
    Light,
    Dark,
    System
}

/// <summary>编码预设</summary>
public enum EncodePreset
{
    Medium,
    Slow,
    VerySlow
}

/// <summary>超分倍率</summary>
public enum SrScale
{
    X2 = 2,
    X3 = 3,
    X4 = 4
}

/// <summary>补帧倍率</summary>
public enum IfMultiplier
{
    X2 = 2,
    X3 = 3,
    X4 = 4,
    X5 = 5
}
