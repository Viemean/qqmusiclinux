using QQMusic.Tui.Models;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Services;

/// <summary>
/// 全局独立播放队列服务，隔离界面展示视图与真实待播队列
/// </summary>
public sealed class PlaybackQueueService
{
    private static readonly Lazy<PlaybackQueueService> _lazy = new(() => new PlaybackQueueService());
    public static PlaybackQueueService Instance => _lazy.Value;

    private readonly Lock _lock = new();
    private readonly List<Song> _activeSongs = [];
    private readonly List<int> _shuffleIndices = [];
    private int _shufflePointer = -1;

    public event Action? QueueChanged;

    public IReadOnlyList<Song> ActiveSongs
    {
        get
        {
            lock (_lock)
            {
                return _activeSongs.ToArray();
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _activeSongs.Count;
            }
        }
    }

    public int CurrentIndex { get; private set; } = -1;

    public Song? CurrentSong
    {
        get
        {
            lock (_lock)
            {
                if (CurrentIndex >= 0 && CurrentIndex < _activeSongs.Count)
                {
                    return _activeSongs[CurrentIndex];
                }
                return null;
            }
        }
    }

    public PlaybackMode Mode { get; set; } = PlaybackMode.ListLoop;

    private PlaybackQueueService() { }

    /// <summary>
    /// 装载全新播放队列并定位起始播放索引
    /// </summary>
    public void SetQueue(IEnumerable<Song> songs, int startIndex = 0)
    {
        lock (_lock)
        {
            _activeSongs.Clear();
            _activeSongs.AddRange(songs);

            if (_activeSongs.Count == 0)
            {
                CurrentIndex = -1;
                _shuffleIndices.Clear();
                _shufflePointer = -1;
            }
            else
            {
                CurrentIndex = Math.Clamp(startIndex, 0, _activeSongs.Count - 1);
                RebuildShuffleQueue(_activeSongs.Count, CurrentIndex);
            }
        }

        QueueChanged?.Invoke();
    }

    /// <summary>
    /// 将歌曲插队至下一顺位播放
    /// </summary>
    public void InsertNext(Song song)
    {
        lock (_lock)
        {
            if (_activeSongs.Count == 0)
            {
                _activeSongs.Add(song);
                CurrentIndex = 0;
                _shuffleIndices.Clear();
                _shuffleIndices.Add(0);
                _shufflePointer = 0;
            }
            else
            {
                int insertIdx = CurrentIndex >= 0 ? CurrentIndex + 1 : 0;
                _activeSongs.Insert(insertIdx, song);

                // 更新 Shuffle 索引映射
                if (_shuffleIndices.Count > 0)
                {
                    for (int i = 0; i < _shuffleIndices.Count; i++)
                    {
                        if (_shuffleIndices[i] >= insertIdx)
                        {
                            _shuffleIndices[i]++;
                        }
                    }

                    int insertShufflePos = _shufflePointer >= 0 ? _shufflePointer + 1 : 0;
                    _shuffleIndices.Insert(insertShufflePos, insertIdx);
                }
                else
                {
                    RebuildShuffleQueue(_activeSongs.Count, CurrentIndex);
                }
            }
        }

        AppLogger.Info("PlaybackQueue", $"Song inserted next: {song.Title} - {song.Artist}");
        QueueChanged?.Invoke();
    }

    /// <summary>
    /// 从队列中移除指定索引的歌曲
    /// </summary>
    public bool RemoveAt(int index)
    {
        lock (_lock)
        {
            if (index < 0 || index >= _activeSongs.Count) return false;

            _activeSongs.RemoveAt(index);

            if (_activeSongs.Count == 0)
            {
                CurrentIndex = -1;
                _shuffleIndices.Clear();
                _shufflePointer = -1;
            }
            else
            {
                if (index < CurrentIndex)
                {
                    CurrentIndex--;
                }
                else if (CurrentIndex >= _activeSongs.Count)
                {
                    CurrentIndex = _activeSongs.Count - 1;
                }

                // 重建或修正 Shuffle 队列
                RebuildShuffleQueue(_activeSongs.Count, CurrentIndex);
            }
        }

        QueueChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 清空所有待播歌曲（保留当前正在播放的曲目）
    /// </summary>
    public void ClearUpcoming()
    {
        lock (_lock)
        {
            if (_activeSongs.Count <= 1) return;

            var current = CurrentSong;
            _activeSongs.Clear();
            if (current != null)
            {
                _activeSongs.Add(current);
                CurrentIndex = 0;
                _shuffleIndices.Clear();
                _shuffleIndices.Add(0);
                _shufflePointer = 0;
            }
            else
            {
                CurrentIndex = -1;
                _shuffleIndices.Clear();
                _shufflePointer = -1;
            }
        }

        QueueChanged?.Invoke();
    }

    /// <summary>
    /// 推演并切换至下一首播放曲目
    /// </summary>
    public Song? GetNextSong(bool isAutoPlayback = false)
    {
        lock (_lock)
        {
            if (_activeSongs.Count == 0) return null;

            // 单曲循环且为播放完毕自动切歌
            if (Mode == PlaybackMode.SingleLoop && isAutoPlayback)
            {
                return CurrentSong;
            }

            // 随机播放模式
            if (Mode == PlaybackMode.Shuffle && _activeSongs.Count > 1)
            {
                EnsureShuffleQueue();

                _shufflePointer++;
                if (_shufflePointer >= _shuffleIndices.Count)
                {
                    int lastSongIdx = _shuffleIndices[^1];
                    RebuildShuffleQueue(_activeSongs.Count, -1);
                    if (_shuffleIndices.Count > 1 && _shuffleIndices[0] == lastSongIdx)
                    {
                        int swapTarget = 1 + Random.Shared.Next(_shuffleIndices.Count - 1);
                        (_shuffleIndices[0], _shuffleIndices[swapTarget]) = (_shuffleIndices[swapTarget], _shuffleIndices[0]);
                    }
                    _shufflePointer = 0;
                }

                int nextIdx = _shuffleIndices[_shufflePointer];
                CurrentIndex = nextIdx;
                return _activeSongs[CurrentIndex];
            }

            // 列表循环模式 (或单曲循环下主动按切歌键)
            if (Mode == PlaybackMode.ListLoop || (!isAutoPlayback && Mode == PlaybackMode.SingleLoop))
            {
                CurrentIndex = (CurrentIndex + 1) % _activeSongs.Count;
                return _activeSongs[CurrentIndex];
            }

            // 顺序播放模式
            if (CurrentIndex + 1 < _activeSongs.Count)
            {
                CurrentIndex++;
                return _activeSongs[CurrentIndex];
            }

            if (!isAutoPlayback && CurrentIndex == -1 && _activeSongs.Count > 0)
            {
                CurrentIndex = 0;
                return _activeSongs[0];
            }

            // 顺序播放到达末尾
            return null;
        }
    }

    /// <summary>
    /// 预先窥视下一首曲目（用于平滑预加载，不改变内部游标）
    /// </summary>
    public Song? PeekNextSong()
    {
        lock (_lock)
        {
            if (_activeSongs.Count <= 1) return null;

            if (Mode == PlaybackMode.Shuffle)
            {
                EnsureShuffleQueue();
                if (_shufflePointer >= 0 && _shufflePointer + 1 < _shuffleIndices.Count)
                {
                    int nextIdx = _shuffleIndices[_shufflePointer + 1];
                    if (nextIdx >= 0 && nextIdx < _activeSongs.Count)
                    {
                        return _activeSongs[nextIdx];
                    }
                }
                return null;
            }

            int next = (CurrentIndex + 1) % _activeSongs.Count;
            return _activeSongs[next];
        }
    }

    /// <summary>
    /// 推演并切换至上一首播放曲目
    /// </summary>
    public Song? GetPrevSong()
    {
        lock (_lock)
        {
            if (_activeSongs.Count == 0) return null;

            if (Mode == PlaybackMode.Shuffle && _activeSongs.Count > 1)
            {
                EnsureShuffleQueue();
                if (_shufflePointer > 0)
                {
                    _shufflePointer--;
                }
                else
                {
                    _shufflePointer = _shuffleIndices.Count - 1;
                }

                int prevIdx = _shuffleIndices[_shufflePointer];
                CurrentIndex = prevIdx;
                return _activeSongs[CurrentIndex];
            }

            if (Mode == PlaybackMode.ListLoop || Mode == PlaybackMode.SingleLoop)
            {
                CurrentIndex = (CurrentIndex - 1 + _activeSongs.Count) % _activeSongs.Count;
                return _activeSongs[CurrentIndex];
            }

            // 顺序播放
            if (CurrentIndex > 0)
            {
                CurrentIndex--;
                return _activeSongs[CurrentIndex];
            }

            return _activeSongs[0];
        }
    }

    /// <summary>
    /// 定位至队列中的指定索引曲目
    /// </summary>
    public Song? SetCurrentIndex(int index)
    {
        lock (_lock)
        {
            if (index < 0 || index >= _activeSongs.Count) return null;

            CurrentIndex = index;
            if (Mode == PlaybackMode.Shuffle)
            {
                EnsureShuffleQueue();
                int p = _shuffleIndices.IndexOf(CurrentIndex);
                if (p >= 0)
                {
                    _shufflePointer = p;
                }
            }

            return _activeSongs[CurrentIndex];
        }
    }

    /// <summary>
    /// 对齐外部正在播放的歌曲
    /// </summary>
    public void SyncCurrentSong(Song song)
    {
        lock (_lock)
        {
            int idx = -1;
            for (int i = 0; i < _activeSongs.Count; i++)
            {
                if (_activeSongs[i].Mid == song.Mid)
                {
                    idx = i;
                    break;
                }
            }

            if (idx >= 0)
            {
                CurrentIndex = idx;
                if (Mode == PlaybackMode.Shuffle)
                {
                    EnsureShuffleQueue();
                    int p = _shuffleIndices.IndexOf(CurrentIndex);
                    if (p >= 0) _shufflePointer = p;
                }
            }
            else
            {
                // 若队列中不存在该歌曲，作为单曲队列初始化
                _activeSongs.Clear();
                _activeSongs.Add(song);
                CurrentIndex = 0;
                _shuffleIndices.Clear();
                _shuffleIndices.Add(0);
                _shufflePointer = 0;
            }
        }
    }

    private void EnsureShuffleQueue()
    {
        if (_shuffleIndices.Count == _activeSongs.Count && _shufflePointer >= 0 && _shufflePointer < _shuffleIndices.Count)
        {
            if (CurrentIndex >= 0 && _shuffleIndices[_shufflePointer] != CurrentIndex)
            {
                int p = _shuffleIndices.IndexOf(CurrentIndex);
                if (p >= 0) _shufflePointer = p;
            }
            return;
        }

        RebuildShuffleQueue(_activeSongs.Count, CurrentIndex);
    }

    private void RebuildShuffleQueue(int count, int currentIdx)
    {
        _shuffleIndices.Clear();
        for (int i = 0; i < count; i++)
        {
            _shuffleIndices.Add(i);
        }

        for (int i = count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (_shuffleIndices[i], _shuffleIndices[j]) = (_shuffleIndices[j], _shuffleIndices[i]);
        }

        if (currentIdx >= 0)
        {
            int p = _shuffleIndices.IndexOf(currentIdx);
            if (p >= 0)
            {
                (_shuffleIndices[0], _shuffleIndices[p]) = (_shuffleIndices[p], _shuffleIndices[0]);
            }
            _shufflePointer = 0;
        }
        else
        {
            _shufflePointer = -1;
        }
    }
}
