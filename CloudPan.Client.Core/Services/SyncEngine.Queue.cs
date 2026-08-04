using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
using CloudPan.Infrastructure.Persistence.Client;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Core.Services;

/// <summary>SyncEngine 部分实现：传输队列处理（队列消费、进度上报、重试退避）。</summary>
public partial class SyncEngine
{
    // ============================================================
    // 传输队列处理
    // ============================================================

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        await using var store = await _storeFactory.CreateStoreAsync();

        // T-045：冲突项不占传输名额。待决策冲突路径从候选中剔除（冲突项保留在 DB，
        // 由 OnConflictResolved 解决时清除），避免冲突堆积 ≥5 个时恒占前 5 槽位、其余传输饥饿。
        var conflictPaths = _pendingConflicts.Keys.ToList();
        var items = await store.GetNextQueueBatchAsync(conflictPaths.Count > 0 ? conflictPaths : null, 5, ct);

        if (items.Count == 0)
        {
            return;
        }

        // 计算总队列字节数和文件数（每次重新计算，避免外部新增项导致进度倒缩）
        await _progress.RecalcTotals(store);

        _progress.Emit();

        // 定时进度报告（确保长传输期间至少每秒一次）
        using CancellationTokenSource progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task progressTask = Task.Run(async () =>
        {
            while (!progressCts.Token.IsCancellationRequested)
            {
                try { await Task.Delay(1000, progressCts.Token); _progress.Emit(); }
                catch (OperationCanceledException) { /* 取消是预期的 */ }
                catch (Exception ex) { _logger.LogWarning(ex, "进度报告异常"); }
            }
        }, progressCts.Token);

        try
        {
            foreach (var item in items)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                // 跳过等待用户决策的冲突文件
                if (_pendingConflicts.ContainsKey(item.FilePath))
                {
                    _logger.LogDebug("冲突文件跳过处理: {Path}", item.FilePath);
                    continue;
                }

                // 设置当前传输文件名
                _progress.SetCurrentFile(Path.GetFileName(item.FilePath));
                _progress.Emit();

                bool success = false;

                try
                {
                    success = item.Operation switch
                    {
                        (int)SyncOperation.Upload => await ProcessUploadAsync(item, ct),
                        (int)SyncOperation.Download => await ProcessDownloadAsync(item, ct),
                        (int)SyncOperation.Delete => await ProcessDeleteAsync(item, ct),
                        (int)SyncOperation.Rename => await ProcessRenameAsync(item, ct),
                        _ => throw new InvalidOperationException("未知同步操作: " + item.Operation)
                    };
                }
                catch (Exception ex)
                {
                    item.RetryCount++;
                    item.LastError = ex.Message;
                    SyncOperation op = (SyncOperation)item.Operation;
                    _logger.LogError($"传输异常 [{item.RetryCount}/{MaxRetryCount}]: {item.FilePath} — {ex.Message}");
                    // F-31：不再透出原始异常字符串，转为白话归因 + 下一步
                    ErrorAttribution attribution = ErrorAttribution.FromException(ex);
                    ErrorOccurred?.Invoke(item.FilePath, attribution, op);
                    // F-34：连续 401（Token/服务端配置变更）达到阈值 → 触发重配引导，而非静默离线
                    TrackAuthFailure(attribution.RequiresReconfiguration);

                    // 阶梯退避：200ms→400ms→...→2000ms 后保持 2000ms
                    int backoffMs = GetBackoffDelay(item.RetryCount);
                    if (backoffMs > 0)
                    {
                        await Task.Delay(backoffMs, ct);
                    }
                }

                if (success || item.RetryCount >= MaxRetryCount)
                {
                    if (success && item.FileSize.HasValue)
                    {
                        _progress.AddCompletedBytes(item.FileSize.Value);
                    }

                    // 单项传输成功 → 重置连续认证失败计数
                    if (success)
                    {
                        TrackAuthFailure(false);
                    }

                    store.RemoveQueueItem(item);
                    _progress.IncrementCompleted();
                    if (item.RetryCount >= MaxRetryCount)
                    {
                        SyncOperation op = (SyncOperation)item.Operation;
                        _logger.LogError($"传输放弃（已达最大重试）: {item.FilePath}");
                        ErrorOccurred?.Invoke(item.FilePath, new ErrorAttribution($"已达最大重试次数（{item.RetryCount}）", "请重试；若反复失败，请检查网络或文件权限"), op);
                        NotifyStatus($"同步失败: {Path.GetFileName(item.FilePath)}");
                    }
                }

                await store.CommitAsync();
                _progress.Emit();
            }
        }
        finally
        {
            progressCts.Cancel();
            try { await progressTask; } catch (OperationCanceledException) { } catch (Exception ex) { _logger.LogWarning(ex, "等待进度任务完成时异常"); }
        }

        // 队列清空后清除当前文件名
        int remainingAfter = await store.GetQueueCountAsync();
        if (remainingAfter == 0)
        {
            _progress.SetCurrentFile(null);
            _progress.Emit();
        }
    }

    /// <summary>
    /// 持续处理传输队列直到清空。用于首次同步阶段，确保下载队列处理完毕后再执行本地扫描，
    /// 防止 FullScanAsync 在本地文件尚未下载时误将远程文件判定为"已删除"。
    /// </summary>
    private async Task DrainQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await ProcessQueueAsync(ct);
            await using var store = await _storeFactory.CreateStoreAsync(ct);
            int remaining = await store.GetQueueCountAsync(ct);
            if (remaining == 0)
            {
                break;
            }

            await Task.Delay(200, ct);
        }
    }

    /// <summary>
    /// 重试退避延迟（毫秒）。单源：shared-spec.json → SpecConfig.RetryBackoffMs。
    /// 第 n 次重试取序列第 n-1 个值，超出序列长度保持末值。
    /// </summary>
    private static int GetBackoffDelay(int retryCount)
    {
        if (retryCount <= 0)
        {
            return 0;
        }
        int index = Math.Min(retryCount - 1, SpecConfig.RetryBackoffMs.Length - 1);
        return SpecConfig.RetryBackoffMs[index];
    }

    // 本地文件删除工具（SafeDelete/SafeDeleteDirectory）T-099 起由 SyncPath.SafeDelete 与 SyncRemoteApplier 承担
}
