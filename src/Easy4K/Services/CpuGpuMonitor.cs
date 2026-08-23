using System.Diagnostics;

namespace Easy4K.Services;

/// <summary>实时采样系统 CPU 与 GPU 使用率（0-100）。
/// CPU 和 GPU 各用独立专用后台线程采样，互不阻塞；GPU 实例枚举也在 GPU 线程内完成。
/// GPU 的 PerformanceCounter.NextValue 在超分（GPU 繁忙）时可能变慢甚至阻塞，
/// 但因在独立线程，绝不拖累 CPU 采样、UI 线程或处理流程。</summary>
public sealed class CpuGpuMonitor : IDisposable
{
    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter[] _gpuCounters = Array.Empty<PerformanceCounter>();
    private readonly object _lock = new();
    private readonly object _valueLock = new();
    private double _cpuValue;
    private double _gpuValue;
    private volatile bool _running;
    private Thread? _cpuThread;
    private Thread? _gpuThread;

    /// <summary>采样事件（后台线程触发，UI 层需自行调度回 UI 线程）(cpu, gpu)</summary>
    public event Action<double, double>? Sampled;

    /// <summary>开始采样。CPU 每 cpuIntervalMs，GPU 每 gpuIntervalMs。</summary>
    public void Start(int cpuIntervalMs = 250, int gpuIntervalMs = 500)
    {
        Stop();

        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
            _cpuCounter.NextValue(); // 预热
        }
        catch { _cpuCounter = null; }

        // 初始 GPU 枚举放线程池（快速返回），后续重枚举在 GPU 线程内做
        _ = Task.Run(() => RefreshGpuCounters());

        _running = true;
        _cpuThread = new Thread(() => CpuLoop(cpuIntervalMs)) { IsBackground = true };
        _cpuThread.Start();
        _gpuThread = new Thread(() => GpuLoop(gpuIntervalMs)) { IsBackground = true };
        _gpuThread.Start();
    }

    private void CpuLoop(int interval)
    {
        while (_running)
        {
            try { if (_cpuCounter is not null) lock (_valueLock) _cpuValue = Math.Clamp(_cpuCounter.NextValue(), 0, 100); } catch { }
            Notify();
            try { Thread.Sleep(interval); } catch { }
        }
    }

    private void GpuLoop(int interval)
    {
        int count = 0;
        while (_running)
        {
            try
            {
                // 每 4 次（约 2 秒 @500ms）重枚举，捕捉处理进程新产生的引擎实例
                if (++count % 4 == 0) RefreshGpuCounters();

                double max = 0;
                PerformanceCounter[] arr;
                lock (_lock) arr = _gpuCounters;
                foreach (var c in arr)
                {
                    var v = c.NextValue();
                    if (v > max) max = v;
                }
                lock (_valueLock) _gpuValue = Math.Clamp(max, 0, 100);
            }
            catch { }
            Notify();
            try { Thread.Sleep(interval); } catch { }
        }
    }

    private void Notify()
    {
        double cpu, gpu;
        lock (_valueLock) { cpu = _cpuValue; gpu = _gpuValue; }
        Sampled?.Invoke(cpu, gpu);
    }

    /// <summary>枚举 GPU 3D/Compute 引擎实例并预热（在 GPU 线程内调用）。</summary>
    private void RefreshGpuCounters()
    {
        var fresh = new List<PerformanceCounter>();
        try
        {
            var cat = new PerformanceCounterCategory("GPU Engine");
            if (cat.CounterExists("Utilization Percentage"))
            {
                foreach (var inst in cat.GetInstanceNames())
                {
                    if (!inst.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase) &&
                        !inst.Contains("engtype_Compute", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true);
                        c.NextValue(); // 预热
                        fresh.Add(c);
                    }
                    catch { }
                }
            }
        }
        catch { }

        lock (_lock)
        {
            var old = _gpuCounters;
            _gpuCounters = fresh.ToArray();
            foreach (var c in old) { try { c.Dispose(); } catch { } }
        }
    }

    public void Stop()
    {
        _running = false;
        _cpuThread = null;
        _gpuThread = null;
    }

    public void Dispose()
    {
        Stop();
        try { _cpuCounter?.Dispose(); } catch { }
        lock (_lock)
        {
            foreach (var c in _gpuCounters) { try { c.Dispose(); } catch { } }
            _gpuCounters = Array.Empty<PerformanceCounter>();
        }
    }
}
