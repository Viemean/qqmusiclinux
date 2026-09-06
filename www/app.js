/**
 * QQ Music Web 客户端
 */

class ElectronMusicPlayer {
  constructor() {
    // 容器与舞台
    this.stageView = document.getElementById('stageView');
    this.coverContainer = document.getElementById('coverContainer');
    this.albumCover = document.getElementById('albumCover');
    this.mobileLyricsContainer = document.getElementById('mobileLyricsContainer');
    this.mobileLyricsInner = document.getElementById('mobileLyricsInner');
    this.desktopLyricsPane = document.getElementById('desktopLyricsPane');
    this.desktopLyricsInner = document.getElementById('desktopLyricsInner');
    this.ambientBackdrop = document.getElementById('ambientBackdrop');

    // 信息与控制元素
    this.trackMeta = document.getElementById('trackMeta');
    this.trackTitle = document.getElementById('trackTitle');
    this.trackArtist = document.getElementById('trackArtist');
    this.qualityBadge = document.getElementById('qualityBadge');
    this.btnLike = document.getElementById('btnLike');
    this.btnFooterLike = document.getElementById('btnFooterLike');

    // 进度条
    this.progressWrap = document.getElementById('progressWrap');
    this.progressFill = document.getElementById('progressFill');
    this.timeCurrent = document.getElementById('timeCurrent');
    this.timeTotal = document.getElementById('timeTotal');

    // 按钮与音频
    this.btnPlay = document.getElementById('btnPlay');
    this.playIcon = document.getElementById('playIcon');
    this.pauseIcon = document.getElementById('pauseIcon');
    this.btnPrev = document.getElementById('btnPrev');
    this.btnNext = document.getElementById('btnNext');
    this.btnMode = document.getElementById('btnMode');
    this.iconModeList = document.getElementById('iconModeList');
    this.iconModeSingle = document.getElementById('iconModeSingle');
    this.iconModeShuffle = document.getElementById('iconModeShuffle');
    this.iconModeSeq = document.getElementById('iconModeSeq');

    // 音量与弹出条 (独立控制，不同步 TUI)
    this.volContainer = document.getElementById('volContainer');
    this.volPopover = document.getElementById('volPopover');
    this.volSlider = document.getElementById('volSlider');
    this.volNum = document.getElementById('volNum');
    this.btnVolume = document.getElementById('btnVolume');
    this.audioElement = document.getElementById('audioElement');

    // 状态
    this.state = {
      isPlaying: false,
      currentPosition: 0,
      totalDuration: 0,
      currentSongMid: '',
      lyrics: [],
      activeLyricIndex: -1,
      isLiked: false,
      playbackMode: 'list_loop',
    };

    this.isSeeking = false;
    this.currentStreamUrl = '';
    this.lastProgressReportTick = 0;
    this.hasSetInitialPositionState = false;

    // SSE 通信看门狗与自动重连状态
    this.sse = null;
    this.sseReconnectTimer = null;
    this.lastSseMessageTime = Date.now();
    this.sseWatchdogInterval = null;
    this.pendingAutoplay = false;

    this.init();
  }

  init() {
    this.bindEvents();
    this.setupAudioListeners();
    this.connectSSE();
    this.startTicker();
  }

  bindEvents() {
    // 桌面端禁用点击封面
    const setMobileLyricsView = (show) => {
      if (show) {
        this.stageView.classList.add('show-lyrics');
      } else {
        this.stageView.classList.remove('show-lyrics');
      }
      const isShowing = this.stageView.classList.contains('show-lyrics');
      document.body.classList.toggle('is-lyrics-view', isShowing);
      if (isShowing) {
        requestAnimationFrame(() => {
          this.scrollToActiveLyric(true);
        });
      }
    };

    this.stageView.addEventListener('click', (e) => {
      if (window.innerWidth >= 820) {
        // 可显示歌词视窗大小下禁止点击封面
        return;
      }
      if (e.target.closest('.lyric-item')) return;
      const isCurrentlyShowing = this.stageView.classList.contains('show-lyrics');
      setMobileLyricsView(!isCurrentlyShowing);
    });

    // 2. 手机端便捷交互：点击歌曲信息区可返回显示大封面图片
    if (this.trackMeta) {
      this.trackMeta.addEventListener('click', (e) => {
        if (e.target.closest('.btn-like') || e.target.closest('.quality-btn-sync')) return;
        if (window.innerWidth < 820 && this.stageView.classList.contains('show-lyrics')) {
          setMobileLyricsView(false);
        }
      });
    }

    // 窗口尺寸变化时响应式重置移动端特化状态
    window.addEventListener('resize', () => {
      if (window.innerWidth >= 820) {
        document.body.classList.remove('is-lyrics-view');
        this.stageView.classList.remove('show-lyrics');
      }
    });

    // 封面加载完成时更新背景色
    this.albumCover.addEventListener('load', () => {
      this.stageView.classList.remove('no-cover');
      this.albumCover.classList.remove('error');
      const monet = this.extractMonetColors(this.albumCover);
      if (monet) {
        document.documentElement.style.setProperty('--play-monet-bg', monet.bg);
      }
    });

    this.albumCover.addEventListener('error', () => {
      // 若 CDN 封面失败且尚未尝试本地端点，优雅回退至本地提取接口
      if (this.albumCover.src && !this.albumCover.src.includes('/cover')) {
        this.albumCover.src = `/cover?mid=${encodeURIComponent(this.state.currentSong?.mid || '')}&t=${Date.now()}`;
        return;
      }
      this.stageView.classList.add('no-cover');
      this.albumCover.classList.add('error');
      document.documentElement.style.setProperty('--play-monet-bg', '#1a1c22');
    });

    // 喜欢按钮交互：双向绑定同步
    const toggleLike = () => {
      this.state.isLiked = !this.state.isLiked;
      this.updateLikeUI();
      this.sendAction('/api/favorite', { favorite: this.state.isLiked });
    };

    if (this.btnLike) {
      this.btnLike.addEventListener('click', toggleLike);
    }
    if (this.btnFooterLike) {
      this.btnFooterLike.addEventListener('click', toggleLike);
    }

    // 播放控制
    this.btnPlay.addEventListener('click', () => this.togglePlay());
    this.btnPrev.addEventListener('click', () => this.sendAction('/api/previous'));
    this.btnNext.addEventListener('click', () => this.sendAction('/api/next'));

    // 播放循环模式控制 (双向同步 TUI)
    this.btnMode.addEventListener('click', () => {
      this.sendAction('/api/mode');
    });

    // 音质切换控制 (双向同步 TUI，多按钮统一切换)
    const syncQualityButtons = document.querySelectorAll('.quality-btn-sync');
    for (const btn of syncQualityButtons) {
      btn.addEventListener('click', (e) => {
        e.stopPropagation();
        this.sendAction('/api/quality');
      });
    }

    // 音量调节 (点击弹出浮层，用户明确指定不需要同步 TUI)
    this.btnVolume.addEventListener('click', (e) => {
      e.stopPropagation();
      this.volPopover.classList.toggle('show');
    });

    // 点击外部区域自动收起音量浮动条
    document.addEventListener('click', (e) => {
      if (this.volContainer && !this.volContainer.contains(e.target)) {
        this.volPopover.classList.remove('show');
      }
    });

    this.volSlider.addEventListener('input', (e) => {
      const vol = Number.parseInt(e.target.value, 10);
      this.audioElement.volume = Math.max(0, Math.min(1, vol / 100));
      if (this.volNum) {
        this.volNum.textContent = `${vol}%`;
      }
    });

    // 进度条交互
    this.setupProgressBar();

    // 快捷键支持
    window.addEventListener('keydown', (e) => {
      if (['input', 'textarea'].includes(document.activeElement.tagName.toLowerCase())) return;
      if (e.code === 'Space') {
        e.preventDefault();
        this.togglePlay();
      } else if (e.code === 'ArrowRight') {
        e.preventDefault();
        this.seekRelative(5);
      } else if (e.code === 'ArrowLeft') {
        e.preventDefault();
        this.seekRelative(-5);
      }
    });

    // 注册 Android / 系统通知栏标准按钮与状态公布
    this.setupMediaSession();

    // 移动端息屏唤醒与切回前台事件：若长连接断开或超时，立即静默重连，并补偿受限的自动起播
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible') {
        const elapsed = Date.now() - this.lastSseMessageTime;
        if (!this.sse || this.sse.readyState === EventSource.CLOSED || elapsed > 25000) {
          this.reconnectSSE();
        }
        if (this.pendingAutoplay && this.state.isPlaying && this.audioElement.src) {
          this.audioElement.play().then(() => {
            this.pendingAutoplay = false;
          }).catch(() => {});
        }
      }
    });

    // 用户在页面任意触碰时，若存在被浏览器策略挂起的待播放曲目，立即静默触发起播
    document.addEventListener('pointerdown', () => {
      if (this.pendingAutoplay && this.state.isPlaying && this.audioElement.src) {
        this.audioElement.play().then(() => {
          this.pendingAutoplay = false;
        }).catch(() => {});
      }
    }, { passive: true });

    window.addEventListener('online', () => {
      this.reconnectSSE();
    });
  }

  updateLikeUI() {
    if (this.btnLike) {
      this.btnLike.classList.toggle('liked', this.state.isLiked);
    }
    if (this.btnFooterLike) {
      this.btnFooterLike.classList.toggle('liked', this.state.isLiked);
    }
  }

  setupAudioListeners() {
    const syncPosition = () => {
      const dur = this.audioElement.duration || this.state.totalDuration;
      if (Number.isFinite(dur) && dur > 0) {
        this.state.totalDuration = dur;
        this.updateProgressUI();
        this.updateMediaSessionPosition();
      }
    };

    // 监听本地原生音频进度并同步
    this.audioElement.addEventListener('timeupdate', () => {
      if (this.isSeeking) return;
      const cur = this.audioElement.currentTime;
      const dur = this.audioElement.duration || this.state.totalDuration;
      if (Number.isFinite(dur) && dur > 0) this.state.totalDuration = dur;
      this.state.currentPosition = cur;
      this.updateProgressUI();
      this.updateActiveLyric();

      // 单次保底：确保在刚起播且拿到有效时长时，必定成功向 Android 系统公布一次原生进度条
      if (!this.hasSetInitialPositionState && Number.isFinite(dur) && dur > 0) {
        this.updateMediaSessionPosition();
      }

      // 每 1.5 秒向 TUI 汇报一次进度，确保歌词对齐
      const now = Date.now();
      if (now - this.lastProgressReportTick > 1500) {
        this.lastProgressReportTick = now;
        this.sendAction('/api/progress', { position: cur, duration: dur });
      }
    });

    this.audioElement.addEventListener('loadedmetadata', syncPosition);
    this.audioElement.addEventListener('canplay', syncPosition);
    this.audioElement.addEventListener('durationchange', syncPosition);

    this.audioElement.addEventListener('play', () => {
      this.state.isPlaying = true;
      this.updatePlayStateUI();
      syncPosition();
    });

    this.audioElement.addEventListener('playing', () => {
      this.state.isPlaying = true;
      this.updatePlayStateUI();
      syncPosition();
    });

    this.audioElement.addEventListener('pause', () => {
      this.state.isPlaying = false;
      this.updatePlayStateUI();
      this.updateMediaSessionPosition();
    });

    this.audioElement.addEventListener('seeked', () => {
      this.updateMediaSessionPosition();
    });

    this.audioElement.addEventListener('ended', () => {
      this.sendAction('/api/action', { action: 'ended' });
    });
  }

  togglePlay() {
    this.state.isPlaying = !this.state.isPlaying;
    this.updatePlayStateUI();

    if (this.state.isPlaying) {
      if (this.audioElement.src) {
        this.audioElement.play().catch((err) => {
          console.warn('Playback requires user gesture:', err);
        });
      }
    } else {
      this.audioElement.pause();
    }

    this.updateMediaSessionPosition();
    this.sendAction('/api/toggle');
  }

  setupProgressBar() {
    const handleSeek = (e) => {
      if (this.state.totalDuration <= 0) return;
      const rect = this.progressWrap.getBoundingClientRect();
      const clientX = e.touches ? e.touches[0].clientX : e.clientX;
      const ratio = Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
      const targetSec = ratio * this.state.totalDuration;

      this.state.currentPosition = targetSec;
      this.updateProgressUI();
      return targetSec;
    };

    const commitSeek = (targetSec) => {
      if (targetSec !== undefined) {
        this.sendAction(`/api/seek?pos=${targetSec.toFixed(1)}`);
        if (Math.abs(this.audioElement.currentTime - targetSec) > 1.5) {
          this.audioElement.currentTime = targetSec;
        }
        this.updateMediaSessionPosition();
      }
    };

    this.progressWrap.addEventListener('mousedown', (e) => {
      this.isSeeking = true;
      const sec = handleSeek(e);
      const onMouseMove = (moveEvent) => handleSeek(moveEvent);
      const onMouseUp = (upEvent) => {
        this.isSeeking = false;
        window.removeEventListener('mousemove', onMouseMove);
        window.removeEventListener('mouseup', onMouseUp);
        const finalSec = handleSeek(upEvent);
        commitSeek(finalSec ?? sec);
      };
      window.addEventListener('mousemove', onMouseMove);
      window.addEventListener('mouseup', onMouseUp);
    });

    this.progressWrap.addEventListener(
      'touchstart',
      (e) => {
        this.isSeeking = true;
        const sec = handleSeek(e);
        const onTouchMove = (moveEvent) => handleSeek(moveEvent);
        const onTouchEnd = () => {
          this.isSeeking = false;
          window.removeEventListener('touchmove', onTouchMove);
          window.removeEventListener('touchend', onTouchEnd);
          commitSeek(sec);
        };
        window.addEventListener('touchmove', onTouchMove, { passive: true });
        window.addEventListener('touchend', onTouchEnd);
      },
      { passive: true }
    );
  }

  seekRelative(delta) {
    if (this.state.totalDuration <= 0) return;
    const target = Math.max(
      0,
      Math.min(this.state.totalDuration, this.state.currentPosition + delta)
    );
    this.state.currentPosition = target;
    this.updateProgressUI();
    this.sendAction(`/api/seek?pos=${target.toFixed(1)}`);
    if (Math.abs(this.audioElement.currentTime - target) > 1.5) {
      this.audioElement.currentTime = target;
    }
    this.updateMediaSessionPosition();
  }

  connectSSE() {
    if (this.sse) {
      try { this.sse.close(); } catch {}
      this.sse = null;
    }

    if (this.sseReconnectTimer) {
      clearTimeout(this.sseReconnectTimer);
      this.sseReconnectTimer = null;
    }

    const sse = new EventSource('/api/events');
    this.sse = sse;
    this.lastSseMessageTime = Date.now();

    const processMessageData = (rawJson) => {
      this.lastSseMessageTime = Date.now();
      try {
        const data = JSON.parse(rawJson);
        this.handleStateUpdate(data);
      } catch (err) {
        console.error('Failed to parse SSE payload', err);
      }
    };

    sse.onopen = () => {
      this.lastSseMessageTime = Date.now();
    };

    sse.onmessage = (e) => processMessageData(e.data);

    sse.onerror = () => {
      if (sse.readyState === EventSource.CLOSED) {
        this.scheduleSseReconnect();
      }
    };

    const sseEventTypes = [
      'sync',
      'init',
      'track',
      'play',
      'pause',
      'resume',
      'stop',
      'seek',
      'progress',
      'lyrics',
      'lyrics_change',
      'song_change',
      'position',
      'mode_change',
      'quality_change',
      'favorite_change',
      'audio_toggled',
    ];
    for (const type of sseEventTypes) {
      sse.addEventListener(type, (e) => processMessageData(e.data));
    }

    // 启动看门狗：每 5 秒检测一次，若超过 25 秒无任何事件消息，判定连接挂起并自动重建连接
    if (!this.sseWatchdogInterval) {
      this.sseWatchdogInterval = setInterval(() => {
        if (Date.now() - this.lastSseMessageTime > 25000) {
          this.reconnectSSE();
        }
      }, 5000);
    }
  }

  scheduleSseReconnect() {
    if (this.sseReconnectTimer) return;
    this.sseReconnectTimer = setTimeout(() => {
      this.sseReconnectTimer = null;
      this.connectSSE();
    }, 2000);
  }

  reconnectSSE() {
    this.connectSSE();
  }

  handleStateUpdate(data) {
    if (!data) return;

    // 1. 歌曲元数据切换
    if (data.song) {
      this.handleTrackChange(data.song);
    }

    // 2. 切歌阶段通知：若新音频流尚未准备好，平滑暂停并重置进度（严禁 removeAttribute('src') 破坏移动端播放会话上下文）
    if (data.type === 'song_change' || (!data.streamUrl && data.type !== 'play')) {
      if (data.type === 'song_change') {
        this.currentStreamUrl = '';
        this.audioElement.pause();
        this.state.currentPosition = 0;
        this.updateProgressUI();
      }
    }

    // 3. 音频流地址更新
    if (data.streamUrl) {
      const streamUrl = data.streamUrl;
      if (this.currentStreamUrl !== streamUrl) {
        this.currentStreamUrl = streamUrl;
        const targetSrc = streamUrl.startsWith('http')
          ? streamUrl
          : `${streamUrl}?mid=${encodeURIComponent(this.state.currentSongMid)}&t=${Date.now()}`;
        this.audioElement.src = targetSrc;
        if (data.position && data.position > 0) {
          this.audioElement.currentTime = data.position;
        }
        if (data.isPlaying) {
          this.audioElement.play().then(() => {
            this.pendingAutoplay = false;
          }).catch((err) => {
            console.warn('Autoplay waiting for gesture:', err);
            this.pendingAutoplay = true;
          });
        }
        this.updateMediaSessionPosition();
      }
    }

    // 4. 播放/暂停状态对齐
    if (data.isPlaying !== undefined && this.state.isPlaying !== !!data.isPlaying) {
      this.state.isPlaying = !!data.isPlaying;
      this.updatePlayStateUI();

      if (this.state.isPlaying) {
        if (this.audioElement.paused && this.audioElement.src) {
          this.audioElement.play().catch((err) => {
            console.warn('Play gesture needed:', err);
          });
        }
      } else {
        if (!this.audioElement.paused) {
          this.audioElement.pause();
        }
      }
      this.updateMediaSessionPosition();
    }

    // 5. 进度与总时长对齐 (仅在大偏差跳跃时修正系统通知栏，避免高频冲刷)
    if (data.position !== undefined && !this.isSeeking) {
      this.state.currentPosition = data.position;
      if (this.audioElement.src && Math.abs(this.audioElement.currentTime - data.position) > 2.5) {
        this.audioElement.currentTime = data.position;
        this.updateMediaSessionPosition();
      }
    }

    if (
      data.duration !== undefined &&
      data.duration > 0 &&
      Math.abs(this.state.totalDuration - data.duration) > 1
    ) {
      this.state.totalDuration = data.duration;
      this.updateMediaSessionPosition();
    }
    this.updateProgressUI();

    // 6. 歌词渲染
    if (data.lyrics) {
      this.parseAndRenderLyrics(data.lyrics);
    }

    // 7. 收藏状态双向同步
    if (data.isFavorite !== undefined) {
      this.state.isLiked = !!data.isFavorite;
      this.updateLikeUI();
    }

    // 8. 循环模式双向同步
    if (data.mode) {
      this.updatePlaybackModeUI(data.mode);
    }

    // 9. 音质标识双向同步
    if (data.qualityBadge) {
      this.updateQualityUI(data.qualityBadge);
    }
  }

  updateQualityUI(quality) {
    if (!quality) return;
    const badges = document.querySelectorAll('.quality-btn-sync');
    for (const badge of badges) {
      badge.textContent = quality;
    }
  }

  handleTrackChange(song) {
    if (!song) return;

    // 切歌时移动端默认恢复为大专辑封面视图
    this.stageView.classList.remove('show-lyrics');
    document.body.classList.remove('is-lyrics-view');
    this.hasSetInitialPositionState = false;

    const midChanged = this.state.currentSongMid !== song.mid;
    this.state.currentSongMid = song.mid || '';
    this.trackTitle.textContent = song.title || '未知曲目';
    this.trackArtist.textContent = `${song.artist || '未知歌手'}${song.album ? ` · ${song.album}` : ''}`;

    if (song.quality) {
      this.updateQualityUI(song.quality);
    }

    if (song.duration) {
      this.state.totalDuration = song.duration;
    }

    // 优先直连官方 CDN 高清大图 (800x800 带缓存)，本地音乐或无 albumMid 回退本地提取端点
    if (midChanged || !this.albumCover.src || this.albumCover.src.endsWith('/cover')) {
      this.stageView.classList.remove('no-cover');
      this.albumCover.classList.remove('error');
      if (!song.isLocal && song.albumMid) {
        this.albumCover.src = `https://y.qq.com/music/photo_new/T002R800x800M000${encodeURIComponent(song.albumMid)}.jpg?max_age=2592000`;
      } else {
        this.albumCover.src = `/cover?mid=${encodeURIComponent(song.mid || '')}&t=${Date.now()}`;
      }
    }

    this.updateProgressUI();
    this.updateMediaSessionMetadata(song);
    this.updateMediaSessionPosition();
  }

  updatePlaybackModeUI(mode) {
    this.state.playbackMode = mode;
    if (this.iconModeList) this.iconModeList.style.display = 'none';
    if (this.iconModeSingle) this.iconModeSingle.style.display = 'none';
    if (this.iconModeShuffle) this.iconModeShuffle.style.display = 'none';
    if (this.iconModeSeq) this.iconModeSeq.style.display = 'none';

    switch (mode) {
      case 'single_loop':
        if (this.iconModeSingle) this.iconModeSingle.style.display = 'block';
        this.btnMode.title = '单曲循环';
        break;
      case 'shuffle':
        if (this.iconModeShuffle) this.iconModeShuffle.style.display = 'block';
        this.btnMode.title = '随机播放';
        break;
      case 'sequential':
        if (this.iconModeSeq) this.iconModeSeq.style.display = 'block';
        this.btnMode.title = '顺序播放';
        break;
      default:
        if (this.iconModeList) this.iconModeList.style.display = 'block';
        this.btnMode.title = '列表循环';
        break;
    }
  }

  updatePlayStateUI() {
    if (this.state.isPlaying) {
      this.playIcon.style.display = 'none';
      this.pauseIcon.style.display = 'block';
    } else {
      this.playIcon.style.display = 'block';
      this.pauseIcon.style.display = 'none';
    }
    if ('mediaSession' in navigator) {
      navigator.mediaSession.playbackState = this.state.isPlaying ? 'playing' : 'paused';
    }
  }

  startTicker() {
    setInterval(() => {
      // 仅在音频尚未播放或未触发 timeupdate 时保底更新
      if (this.state.isPlaying && !this.isSeeking && this.audioElement.paused) {
        this.state.currentPosition += 0.5;
        if (this.state.totalDuration > 0 && this.state.currentPosition > this.state.totalDuration) {
          this.state.currentPosition = this.state.totalDuration;
        }
        this.updateProgressUI();
        this.updateActiveLyric();
      }
    }, 500);
  }

  updateProgressUI() {
    const cur = this.state.currentPosition;
    const tot = this.state.totalDuration;
    this.timeCurrent.textContent = this.formatTime(cur);
    this.timeTotal.textContent = this.formatTime(tot);

    const pct = tot > 0 ? Math.min(100, Math.max(0, (cur / tot) * 100)) : 0;
    this.progressFill.style.width = `${pct}%`;
  }

  formatTime(seconds) {
    if (Number.isNaN(seconds) || seconds < 0) return '00:00';
    const m = Math.floor(seconds / 60);
    const s = Math.floor(seconds % 60);
    return `${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
  }

  parseAndRenderLyrics(rawLyrics) {
    if (!rawLyrics) return;
    const list = [];

    if (Array.isArray(rawLyrics)) {
      for (const item of rawLyrics) {
        if (typeof item === 'object' && item.text) {
          const sec = item.timeMs ? item.timeMs / 1000 : item.time || 0;
          list.push({ time: sec, text: item.text, trans: item.trans || '' });
        }
      }
    } else if (typeof rawLyrics === 'string') {
      const lines = rawLyrics.split('\n');
      const timeRegex = /\[(\d{2}):(\d{2})\.(\d{2,3})\]/;
      for (const line of lines) {
        const match = timeRegex.exec(line);
        if (match) {
          const min = Number.parseInt(match[1], 10);
          const sec = Number.parseInt(match[2], 10);
          const ms = Number.parseInt(match[3], 10);
          const totalSec = min * 60 + sec + (match[3].length === 2 ? ms / 100 : ms / 1000);
          const text = line.replace(/\[\d{2}:\d{2}\.\d{2,3}\]/g, '').trim();
          if (text) list.push({ time: totalSec, text });
        }
      }
    }

    this.state.lyrics = list;
    this.state.activeLyricIndex = -1;

    this.mobileLyricsInner.innerHTML = '';
    this.desktopLyricsInner.innerHTML = '';

    if (list.length === 0) {
      const emptyM = document.createElement('div');
      emptyM.className = 'lyric-line-empty';
      emptyM.textContent = '该歌曲暂无歌词';
      this.mobileLyricsInner.appendChild(emptyM);

      const emptyD = document.createElement('div');
      emptyD.className = 'lyric-line-empty desktop';
      emptyD.textContent = '该歌曲暂无歌词';
      this.desktopLyricsInner.appendChild(emptyD);
      return;
    }

    // 渲染移动端与桌面端歌词列表
    for (let index = 0; index < list.length; index++) {
      const item = list[index];
      // 移动端 item
      const elM = document.createElement('div');
      elM.className = 'lyric-item';
      elM.dataset.index = index;
      elM.textContent = item.text;
      if (item.trans) {
        const transEl = document.createElement('div');
        transEl.className = 'lyric-trans';
        transEl.textContent = item.trans;
        elM.appendChild(transEl);
      }
      elM.addEventListener('click', (e) => {
        e.stopPropagation();
        this.sendAction(`/api/seek?pos=${item.time.toFixed(1)}`);
        this.audioElement.currentTime = item.time;
        this.updateMediaSessionPosition();
      });
      this.mobileLyricsInner.appendChild(elM);

      // 桌面端 item (支持单句歌词点击快速跳转)
      const elD = document.createElement('div');
      elD.className = 'lyric-item';
      elD.dataset.index = index;
      elD.textContent = item.text;
      if (item.trans) {
        const transEl = document.createElement('div');
        transEl.className = 'lyric-trans';
        transEl.textContent = item.trans;
        elD.appendChild(transEl);
      }
      elD.addEventListener('click', (e) => {
        e.stopPropagation();
        this.sendAction(`/api/seek?pos=${item.time.toFixed(1)}`);
        this.audioElement.currentTime = item.time;
        this.updateMediaSessionPosition();
      });
      this.desktopLyricsInner.appendChild(elD);
    }

    this.updateActiveLyric();
  }

  updateActiveLyric() {
    const list = this.state.lyrics;
    if (!list || list.length === 0) return;

    const cur = this.state.currentPosition;
    let idx = -1;
    for (let i = 0; i < list.length; i++) {
      if (cur >= list[i].time - 0.25) {
        idx = i;
      } else {
        break;
      }
    }

    if (idx === -1 && list.length > 0) idx = 0;
    if (idx !== this.state.activeLyricIndex) {
      this.state.activeLyricIndex = idx;
      this.scrollToActiveLyric(false);
    }
  }

  scrollToActiveLyric(force = false) {
    const idx = this.state.activeLyricIndex;
    if (idx < 0) return;

    // 1. 更新移动端居中歌词滚动
    const mobileItems = this.mobileLyricsInner.querySelectorAll('.lyric-item');
    for (let i = 0; i < mobileItems.length; i++) {
      mobileItems[i].classList.toggle('active', i === idx);
    }
    const activeMobile = mobileItems[idx];
    if (activeMobile && (this.stageView.classList.contains('show-lyrics') || force)) {
      const container = this.mobileLyricsContainer;
      const targetTop =
        activeMobile.offsetTop - container.clientHeight / 2 + activeMobile.clientHeight / 2;
      container.scrollTo({ top: Math.max(0, targetTop), behavior: 'smooth' });
    }

    // 2. 更新桌面端多行歌词高亮与局部居中滚动
    const desktopItems = this.desktopLyricsInner.querySelectorAll('.lyric-item');
    for (let i = 0; i < desktopItems.length; i++) {
      desktopItems[i].classList.toggle('active', i === idx);
    }
    const activeDesktop = desktopItems[idx];
    if (activeDesktop) {
      const containerD = this.desktopLyricsInner;
      const targetTopD =
        activeDesktop.offsetTop - containerD.clientHeight / 2 + activeDesktop.clientHeight / 2;
      containerD.scrollTo({ top: Math.max(0, targetTopD), behavior: 'smooth' });
    }
  }

  // ================= Android & 系统级标准按钮与通知栏进度条公布 =================
  setupMediaSession() {
    if (!('mediaSession' in navigator)) return;

    const safeSetHandler = (action, handler) => {
      try {
        navigator.mediaSession.setActionHandler(action, handler);
      } catch {
        // 某些浏览器可能对特定 action 抛错，安全忽略
      }
    };

    safeSetHandler('play', () => this.togglePlay());
    safeSetHandler('pause', () => this.togglePlay());
    safeSetHandler('previoustrack', () => this.sendAction('/api/previous'));
    safeSetHandler('nexttrack', () => this.sendAction('/api/next'));
    safeSetHandler('seekto', (details) => {
      if (details.seekTime !== null && details.seekTime !== undefined) {
        const target = Math.max(
          0,
          Math.min(details.seekTime, this.state.totalDuration || details.seekTime)
        );
        this.state.currentPosition = target;
        this.updateProgressUI();
        this.sendAction(`/api/seek?pos=${target.toFixed(1)}`);
        if (this.audioElement.src) {
          this.audioElement.currentTime = target;
        }
        this.updateMediaSessionPosition();
      }
    });

    // 不注册 seekbackward 与 seekforward，避免被识别为语音播客
    safeSetHandler('seekbackward', null);
    safeSetHandler('seekforward', null);
  }

  updateMediaSessionMetadata(song) {
    if (!('mediaSession' in navigator) || !song) return;
    const mid = song.mid || song.id || '';
    const origin = window.location.origin;
    // 使用带绝对路径和动态时间戳的 URL
    const coverUrl = new URL(`/cover?mid=${encodeURIComponent(mid)}&t=${Date.now()}`, origin).href;

    try {
      navigator.mediaSession.metadata = new MediaMetadata({
        title: song.title || 'QQ音乐',
        artist: song.artist || '未知歌手',
        album: song.album || 'QQ音乐',
        artwork: [
          { src: coverUrl, sizes: '96x96', type: 'image/jpeg' },
          { src: coverUrl, sizes: '128x128', type: 'image/jpeg' },
          { src: coverUrl, sizes: '192x192', type: 'image/jpeg' },
          { src: coverUrl, sizes: '256x256', type: 'image/jpeg' },
          { src: coverUrl, sizes: '384x384', type: 'image/jpeg' },
          { src: coverUrl, sizes: '512x512', type: 'image/jpeg' },
        ],
      });
    } catch (e) {
      console.warn('Failed to update MediaMetadata:', e);
    }
  }

  updateMediaSessionPosition() {
    if (!('mediaSession' in navigator) || !('setPositionState' in navigator.mediaSession)) return;
    try {
      const dur = this.state.totalDuration || this.audioElement.duration;
      let pos =
        this.audioElement.src && !this.audioElement.paused
          ? this.audioElement.currentTime
          : this.state.currentPosition;

      if (Number.isFinite(dur) && dur > 0 && Number.isFinite(pos) && pos >= 0) {
        pos = Math.max(0, Math.min(pos, dur));

        // 确保与系统会话当前的播放/暂停状态对齐
        navigator.mediaSession.playbackState = this.state.isPlaying ? 'playing' : 'paused';

        // playbackRate 必须大于 0，否则系统底层抛出异常
        const rawRate = Number(this.audioElement.playbackRate);
        const rate = Number.isFinite(rawRate) && rawRate > 0 ? rawRate : 1.0;

        navigator.mediaSession.setPositionState({
          duration: dur,
          playbackRate: rate,
          position: pos,
        });
        this.hasSetInitialPositionState = true;
      }
    } catch (err) {
      console.warn('setPositionState error:', err);
    }
  }

  extractMonetColors(img) {
    try {
      if (!img || !img.naturalWidth || !img.naturalHeight) return null;
      const canvas = document.createElement('canvas');
      const ctx = canvas.getContext('2d', { willReadFrequently: true });
      if (!ctx) return null;

      const size = 64;
      canvas.width = size;
      canvas.height = size;
      ctx.drawImage(img, 0, 0, size, size);
      const data = ctx.getImageData(0, 0, size, size).data;

      // 36 个色相区间桶 (每 10 度一个桶: 0°~360°)
      const bins = Array.from({ length: 36 }, () => ({
        count: 0,
        sSum: 0,
        lSum: 0,
        hSum: 0,
      }));

      for (let i = 0; i < data.length; i += 4) {
        const r = data[i] / 255;
        const g = data[i + 1] / 255;
        const b = data[i + 2] / 255;
        const a = data[i + 3];
        if (a < 128) continue;

        const max = Math.max(r, g, b);
        const min = Math.min(r, g, b);
        const l = (max + min) / 2;

        // 过滤极黑与极白 (纯黑白不作为主题色提取源)
        if (l < 0.08 || l > 0.94) continue;

        const d = max - min;
        if (d < 0.08) continue;

        const s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        let h = 0;
        if (max === r) {
          h = (g - b) / d + (g < b ? 6 : 0);
        } else if (max === g) {
          h = (b - r) / d + 2;
        } else {
          h = (r - g) / d + 4;
        }
        h *= 60;

        const binIdx = Math.floor(h / 10) % 36;
        bins[binIdx].count++;
        bins[binIdx].sSum += s;
        bins[binIdx].lSum += l;
        bins[binIdx].hSum += h;
      }

      let bestBin = null;
      let bestScore = -1;

      for (const bin of bins) {
        if (bin.count === 0) continue;
        const avgS = bin.sSum / bin.count;
        const score = bin.count ** 0.65 * avgS ** 1.25;
        if (score > bestScore) {
          bestScore = score;
          bestBin = bin;
        }
      }

      let targetHue = 220;
      let targetSat = 0.2;

      if (bestBin && bestScore > 0) {
        targetHue = Math.round(bestBin.hSum / bestBin.count);
        targetSat = bestBin.sSum / bestBin.count;
      }

      // 莫奈背景色彩计算
      const sPct = Math.min(Math.max(Math.round(targetSat * 70), 22), 42);
      const lPct = 20;
      return {
        bg: `hsl(${targetHue}, ${sPct}%, ${lPct}%)`,
      };
    } catch (e) {
      console.warn('extractMonetColors error:', e);
      return null;
    }
  }

  async sendAction(url, body = null) {
    try {
      if (body) {
        await fetch(url, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(body),
        });
      } else {
        await fetch(url, { method: 'POST' });
      }
    } catch (err) {
      console.error(`Action error: ${url}`, err);
    }
  }
}

window.addEventListener('DOMContentLoaded', () => {
  window.electronPlayer = new ElectronMusicPlayer();
});
