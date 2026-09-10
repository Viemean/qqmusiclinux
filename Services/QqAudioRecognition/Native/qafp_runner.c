/*
 * qafp_runner.c - 官方 QAFP 核心特征提取器
 * 支持两种模式：
 * 1. 经典模式: qafp_runner <model_path> <pcm_path|-> <out_feat_path|->
 * 2. 常驻长连接协议模式: qafp_runner --server <model_path>
 */

typedef unsigned char uint8_t;
typedef signed char int8_t;
typedef short int16_t;
typedef int int32_t;
typedef long int64_t;
typedef unsigned long size_t;
typedef long ssize_t;
typedef long intptr_t;

// Bionic libc / libdl 动态导出标准符号声明
void exit(int code);
void* dlopen(const char* filename, int flag);
void* dlsym(void* handle, const char* symbol);
char* dlerror(void);
void* malloc(size_t sz);
void* realloc(void* ptr, size_t sz);
void free(void* ptr);

// POSIX 标准 I/O
ssize_t read(int fd, void* buf, size_t count);
ssize_t write(int fd, const void* buf, size_t count);
int open(const char* pathname, int flags, ...);
int close(int fd);
int64_t lseek(int fd, int64_t offset, int whence);

#define O_RDONLY    00
#define O_WRONLY    01
#define O_CREAT   0100
#define O_TRUNC  01000
#define SEEK_SET     0
#define SEEK_END     2

typedef int (*raw_init_t)(const char* path);
typedef int (*raw_process_t)(const uint8_t* pcm, int len);
typedef int (*raw_getfeature_t)(int* out_type, float* out_prob, uint8_t* out_buf, int* out_len, float threshold);
typedef int (*raw_reset_t)(void);

static int read_exact(int fd, void* buf, size_t count) {
    size_t done = 0;
    while (done < count) {
        ssize_t r = read(fd, (char*)buf + done, count - done);
        if (r <= 0) return -1;
        done += r;
    }
    return 0;
}

static int write_exact(int fd, const void* buf, size_t count) {
    size_t done = 0;
    while (done < count) {
        ssize_t w = write(fd, (const char*)buf + done, count - done);
        if (w <= 0) return -1;
        done += w;
    }
    return 0;
}

static int str_eq(const char* a, const char* b) {
    while (*a && (*a == *b)) { a++; b++; }
    return *(const unsigned char*)a - *(const unsigned char*)b;
}

int real_main(int argc, char** argv) {
    if (argc < 3) {
        const char* usage = "Usage:\n  qafp_runner <model_path> <pcm_path|-> <out_feat_path|->\n  qafp_runner --server <model_path>\n";
        write(2, usage, 88);
        exit(1);
    }

    int is_server = (str_eq(argv[1], "--server") == 0);
    const char* model_path = is_server ? argv[2] : argv[1];

    void* h = dlopen("/system/lib64/libMusicWrapper.so", 2);
    if (!h) {
        write(2, "dlopen failed\n", 14);
        exit(2);
    }

    void* get_wver = dlsym(h, "Java_com_music_voice_MusicWrapperJNI_GetMusicWarpperVersion");
    if (!get_wver) exit(3);
    char* lib_base = (char*)get_wver - 0xeccc;

    raw_init_t c_init = (raw_init_t)(lib_base + 0xecd0);
    raw_process_t c_process = (raw_process_t)(lib_base + 0xefd8);
    raw_getfeature_t c_getfeature = (raw_getfeature_t)(lib_base + 0xf02c);
    
    void* jni_reset = dlsym(h, "Java_com_music_voice_MusicWrapperJNI_Reset");
    raw_reset_t c_reset = jni_reset ? (raw_reset_t)jni_reset : (raw_reset_t)(lib_base + 0xef28);

    // 模型仅在启动时初始化一次
    int init_ret = c_init(model_path);
    if (init_ret != 0) {
        exit(4);
    }

    // ==========================================
    // 模式 A: 常驻长连接协议 (Server Mode)
    // ==========================================
    if (is_server) {
        // 1. 发送就绪魔数 "QAFP" (0x51414650, 大端序)
        uint8_t ready_magic[4] = { 0x51, 0x41, 0x46, 0x50 };
        write_exact(1, ready_magic, 4);

        uint8_t* pcm_buf = 0;
        size_t pcm_buf_cap = 0;
        uint8_t feat_buf[65536];

        while (1) {
            uint8_t hdr[4];
            if (read_exact(0, hdr, 4) != 0) break;

            // 大端序解析 int32_t pcm_len
            int32_t pcm_len = ((int32_t)hdr[0] << 24) |
                              ((int32_t)hdr[1] << 16) |
                              ((int32_t)hdr[2] << 8)  |
                              ((int32_t)hdr[3]);

            if (pcm_len <= 0) {
                // 退出信号
                break;
            }

            if ((size_t)pcm_len > pcm_buf_cap) {
                pcm_buf_cap = pcm_len + 16384;
                pcm_buf = (uint8_t*)realloc(pcm_buf, pcm_buf_cap);
            }

            if (read_exact(0, pcm_buf, pcm_len) != 0) break;

            // 复位内部状态机并喂入 PCM 数据
            c_reset();
            int chunk_size = 3200;
            int offset = 0;
            while (offset < pcm_len) {
                int len = pcm_len - offset;
                if (len > chunk_size) len = chunk_size;
                c_process(pcm_buf + offset, len);
                offset += len;
            }

            // 提取特征
            int out_type = 0;
            float out_prob = 0.0f;
            int feat_len = 0;
            int ret = c_getfeature(&out_type, &out_prob, feat_buf, &feat_len, 0.0f);

            uint8_t resp_hdr[4];
            if (ret != 0 || feat_len <= 0) {
                // 提取失败写入 0
                resp_hdr[0] = resp_hdr[1] = resp_hdr[2] = resp_hdr[3] = 0;
                write_exact(1, resp_hdr, 4);
            } else {
                // 提取成功写入大端长度 + 特征内容
                resp_hdr[0] = (uint8_t)((feat_len >> 24) & 0xff);
                resp_hdr[1] = (uint8_t)((feat_len >> 16) & 0xff);
                resp_hdr[2] = (uint8_t)((feat_len >> 8)  & 0xff);
                resp_hdr[3] = (uint8_t)(feat_len & 0xff);
                write_exact(1, resp_hdr, 4);
                write_exact(1, feat_buf, feat_len);
            }
        }

        if (pcm_buf) free(pcm_buf);
        exit(0);
    }

    // ==========================================
    // 模式 B: 经典 CLI 模式 (兼容现有命令行与测试)
    // ==========================================
    if (argc < 4) exit(1);
    const char* pcm_path = argv[2];
    const char* out_path = argv[3];

    int is_stdin = (pcm_path[0] == '-' && pcm_path[1] == '\0');
    int is_stdout = (out_path[0] == '-' && out_path[1] == '\0');

    uint8_t* pcm_data = 0;
    long pcm_size = 0;

    if (is_stdin) {
        size_t cap = 131072;
        pcm_data = (uint8_t*)malloc(cap);
        while (1) {
            ssize_t r = read(0, pcm_data + pcm_size, cap - pcm_size);
            if (r <= 0) break;
            pcm_size += r;
            if (pcm_size + 4096 >= cap) {
                cap *= 2;
                pcm_data = (uint8_t*)realloc(pcm_data, cap);
            }
        }
    } else {
        int fd = open(pcm_path, O_RDONLY);
        if (fd < 0) exit(5);
        pcm_size = (long)lseek(fd, 0, SEEK_END);
        lseek(fd, 0, SEEK_SET);
        pcm_data = (uint8_t*)malloc(pcm_size);
        read(fd, pcm_data, pcm_size);
        close(fd);
    }

    if (pcm_size <= 0) exit(5);

    c_reset();

    int chunk_size = 3200;
    int offset = 0;
    while (offset < pcm_size) {
        int len = (int)(pcm_size - offset);
        if (len > chunk_size) len = chunk_size;
        c_process(pcm_data + offset, len);
        offset += len;
    }
    free(pcm_data);

    int out_type = 0;
    float out_prob = 0.0f;
    uint8_t feat_buf[65536];
    int feat_len = 0;

    int ret = c_getfeature(&out_type, &out_prob, feat_buf, &feat_len, 0.0f);
    if (ret != 0 || feat_len <= 0) {
        exit(6);
    }

    if (is_stdout) {
        write(1, feat_buf, feat_len);
    } else {
        int out_fd = open(out_path, O_WRONLY | O_CREAT | O_TRUNC, 0644);
        if (out_fd >= 0) {
            write(out_fd, feat_buf, feat_len);
            close(out_fd);
        } else {
            exit(7);
        }
    }

    exit(0);
}
