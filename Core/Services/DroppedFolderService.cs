using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AutoUnpackTool.Core.Abstractions;

namespace AutoUnpackTool.Core.Services
{
    /// <summary>
    /// 拖入文件夹的登记与智能路径（扁平化）收尾。
    ///
    /// 这段逻辑原先在两个地方各写了一份：
    /// - MainViewModel 的拖拽分支（本次一个压缩包都没扫到时也要收尾）
    /// - ExtractionPipeline 的监控循环退出后（正常流程的收尾）
    /// 两边条件、日志、异常防护都不一致，而且抢同一个 HashSet：
    /// 运行中拖入一个不含压缩包的文件夹，会把集合里正在被解压的其他文件夹一起快照走并提前扁平化，
    /// 等流程真正结束时限定的正规收尾反而拿不到东西了。
    /// 现在收敛为唯一实现：登记 -> 宿主在合适的时机请求收尾 -> 摘走快照 -> 串行处理。
    /// “本轮待收尾文件夹”是任务级工作集而不是缓存，所以 ForceReset 必须调 ResetRunState()。
    /// </summary>
    public sealed class DroppedFolderService
    {
        private readonly IAppLogger _logger;
        private readonly SmartPathService _smartPath;
        private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>收尾解析出的最终路径上报给界面（宿主注入，方法内部需自行封送到 UI 线程）。</summary>
        public Action<string>? OnFinalPathResolved { get; set; }

        public DroppedFolderService(IAppLogger logger, SmartPathService smartPath)
        {
            _logger = logger;
            _smartPath = smartPath;
        }

        /// <summary>
        /// 本功能是否生效：判据归 SmartPathService 拥有，这里只转发。
        /// （上一版在这里又抄了一份 EnableSmartPathProcessing && ArchiveFolder，
        /// 等于第 5 份开关条件复制品。）
        /// </summary>
        public bool IsEnabled => _smartPath.IsDroppedFolderFlattenEnabled;

        /// <summary>当前待收尾的文件夹数量。</summary>
        public int PendingCount
        {
            get { lock (_folders) return _folders.Count; }
        }

        /// <summary>
        /// 登记一个拖入的文件夹（里面没有压缩包也要记，收尾要对它本身做扁平化）。
        /// 功能关时不登记：登记了也没人收（ScheduleFinalize 在 !IsEnabled 时直接 return），
        /// 残留项会一直活在集合里，等用户以后改开智能路径 + 同名文件夹模式时，
        /// 被一次无关的拖入顺带扁平化。
        /// </summary>
        public void Register(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath))
                return;

            if (!IsEnabled)
            {
                _logger.Log($"[智能路径] 功能未启用（需开启智能路径处理且输出模式为「同名文件夹」），不登记拖入文件夹: {folderPath}", ConsoleColor.Gray);
                return;
            }

            lock (_folders)
                _folders.Add(folderPath);
        }

        /// <summary>
        /// 丢弃本轮登记（强制中止/复位时调用）。
        /// 不收的话，上一轮残留的文件夹会被下一次无关的拖入顺带扁平化。
        /// </summary>
        public void ResetRunState()
        {
            lock (_folders)
                _folders.Clear();
        }

        /// <summary>摘走当前全部登记项。先摘后处理，避免处理期间的新拖入被本轮重复处理。</summary>
        private List<string> TakePending()
        {
            lock (_folders)
            {
                if (_folders.Count == 0)
                    return new List<string>();

                var snapshot = _folders.ToList();
                _folders.Clear();
                return snapshot;
            }
        }

        /// <summary>
        /// 请求对已登记的文件夹执行智能路径收尾（后台 fire-and-forget，持智能路径收尾锁串行执行）。
        /// </summary>
        /// <param name="trigger">触发场景，写进日志便于定位是谁触发的</param>
        /// <param name="isBusy">
        /// 由调用点直接传入（不做属性注入，免得宿主忘接线时默默退回旧行为）。
        /// true = 有任务在跑，不动手，登记留待流程结束时的收尾（拖入场景用）；
        /// false = 立即处理（双队列已退出的场景用）。
        /// </param>
        /// <returns>是否真的排了收尾任务</returns>
        public bool ScheduleFinalize(string trigger, bool isBusy)
        {
            if (!IsEnabled)
            {
                // 不静默吞掉：正常路径下 Register 已拦住登记，走到这里只可能是
                // 登记后、收尾前用户改了开关，得把“这批没被动过”说清楚
                int pendingDisabled = PendingCount;
                if (pendingDisabled > 0)
                    _logger.Log($"[智能路径] {trigger}：智能路径未启用或输出模式不是「同名文件夹」，{pendingDisabled} 个拖入文件夹未处理", ConsoleColor.Yellow);
                return false;
            }

            if (isBusy)
            {
                // 不静默吞掉：让日志能看出这批文件夹是被推迟而不是被丢弃
                int pending = PendingCount;
                if (pending > 0)
                    _logger.Log($"[智能路径] {trigger}：有任务在跑，{pending} 个拖入文件夹留待流程结束后收尾", ConsoleColor.Gray);
                return false;
            }

            if (PendingCount == 0)
                return false;

            var folders = TakePending();
            if (folders.Count == 0)
                return false;

            _logger.Log($"[智能路径] {trigger}：开始处理拖入的文件夹（{folders.Count} 个）...", ConsoleColor.Cyan);

            _ = Task.Run(async () =>
            {
                // 与解压收尾共用同一把锁：两者都会移动目录树，必须串行
                await _smartPath.FinalizeLock.WaitAsync();
                try
                {
                    foreach (var folder in folders)
                    {
                        if (!Directory.Exists(folder))
                        {
                            _logger.Log($"[智能路径] 文件夹已不存在，跳过: {folder}", ConsoleColor.Gray);
                            continue;
                        }

                        // 逐个隔离：一个文件夹失败不能连带跳过后面所有的文件夹
                        try
                        {
                            _logger.Log($"[智能路径] 处理拖入的文件夹: {folder}", ConsoleColor.Cyan);
                            await _smartPath.ProcessSmartPathBatchAsync(folder);

                            string finalPath = _smartPath.FindFinalFlattenedPath(folder) ?? folder;
                            OnFinalPathResolved?.Invoke(finalPath);
                            _logger.Log($"[智能路径] 文件夹最终路径: {finalPath}", ConsoleColor.Green);
                        }
                        catch (Exception ex)
                        {
                            _logger.Log($"[智能路径] 处理文件夹失败 {folder}: {ex.Message}", ConsoleColor.Red);
                        }
                    }

                    _logger.Log("[智能路径] 所有拖入文件夹处理完成", ConsoleColor.Green);
                }
                finally
                {
                    _smartPath.FinalizeLock.Release();
                }
            });

            return true;
        }
    }
}
