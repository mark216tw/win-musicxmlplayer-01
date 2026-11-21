using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;

namespace WinMusicXmlPlayer.Services
{
    /// <summary>
    /// 在應用程式閒置、最小化或完成初始化後，於背景安全壓縮託管堆積並修剪 Windows 工作集，
    /// 降低常駐時的實體記憶體佔用（播放期間絕不觸發，避免音訊卡頓）。
    /// </summary>
    public static class MemoryOptimizer
    {
        private static int _trimInProgress;
        private static long _lastTrimTicks;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessWorkingSetSizeEx(
            IntPtr hProcess,
            IntPtr dwMinimumWorkingSetSize,
            IntPtr dwMaximumWorkingSetSize,
            uint flags);

        /// <summary>
        /// 在背景執行緒安全觸發一次記憶體整理與工作集修剪。
        /// </summary>
        /// <param name="isBusy">若回傳 true（例如正在播放音樂），則立即中止修剪。</param>
        /// <param name="force">是否忽略冷卻時間（例如視窗最小化時）。</param>
        public static void TrimIdleMemoryAsync(Func<bool>? isBusy = null, bool force = false)
        {
            if (isBusy != null && isBusy())
                return;

            long nowTicks = Stopwatch.GetTimestamp();
            long lastTicks = Volatile.Read(ref _lastTrimTicks);
            if (!force && lastTicks != 0)
            {
                double elapsedSec = (nowTicks - lastTicks) / (double)Stopwatch.Frequency;
                if (elapsedSec < 10.0)
                    return;
            }

            if (Interlocked.CompareExchange(ref _trimInProgress, 1, 0) != 0)
                return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (isBusy != null && isBusy())
                        return;

                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);

                    if (isBusy != null && isBusy())
                        return;

                    using var proc = Process.GetCurrentProcess();
                    SetProcessWorkingSetSizeEx(proc.Handle, (IntPtr)(-1), (IntPtr)(-1), 0);
                    Volatile.Write(ref _lastTrimTicks, Stopwatch.GetTimestamp());
                }
                catch
                {
                    // 忽略任何平台或權限例外
                }
                finally
                {
                    Volatile.Write(ref _trimInProgress, 0);
                }
            });
        }
    }
}
