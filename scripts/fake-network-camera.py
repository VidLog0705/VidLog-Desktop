#!/usr/bin/env python3
"""造一个假的网络摄像头源，给那 15 条 RequiresRtspFact 用例当对端。

为什么要它 —— 那些用例本来只能在**真设备**上跑：

    RequiresRtspFact 要环境变量 VIDLOG_TEST_RTSP_URL 指向一个真的网络源。
    没设就整组 Skip ⇒ 「因为跳过所以绿」，CI 上那 15 条一次都没真跑过。

而在 CI 里想要一个真网络源，只剩两条路：借一台真摄像头（会打死人家正在用的
推流），或者**自己造一个**。这个脚本就是后者 —— 造一份 1080p25、H.264 + AAC 的
片子，再以 MPEG-TS over HTTP 伺服出去。

## ⚠️ 两个刻意的选择，别顺手改掉

1. **必须带音轨。** 用例 `没开声音时_产物里不许有音轨` 的判据是「**源有声音**、
   产物里没有」—— 源没声音的话它就变成了恒真（正是 TestHygieneTests 那条绊线
   要防的假绿）。所以 `-f lavfi -i sine=...` 那一半不能省。

2. **每个连接起一个 ffmpeg，而不是 `-listen 1` 加循环。** `-listen 1` 服务完
   一个客户端就退出，循环重启之间有几十到几百毫秒**端口空窗**，撞上的那条用例
   会以「连不上」红掉 —— 那是 flaky，不是真缺陷。这里让监听套接字一直活着，
   每个连接各起一个 ffmpeg 子进程，空窗就不存在了。

## 用法

    python scripts/fake-network-camera.py                 # 默认 8081
    python scripts/fake-network-camera.py --port 8082
    # 另开一个终端：
    $env:VIDLOG_TEST_RTSP_URL = 'http://127.0.0.1:8081'
    $env:VIDLOG_TEST_CAMERA_SIZE = '1920x1080'
    dotnet test --filter "FullyQualifiedName~NetworkCamera"

⚠️ 它只是「一个能出帧的网络源」，所以走的是 **非 RTSP 分支**
（`CameraSource.IsRtsp` 只看 URL 协议）—— `-rtsp_transport tcp` 那条分支它盖不到。
ffmpeg 自己也当不了 RTSP 服务端（rtsp 复用器没有 listen 开关，实测：
它会把 URL 当客户端去连）。真要盖 RTSP 分支，得另装一个 RTSP 服务端。
"""

import argparse
import http.server
import os
import shutil
import socketserver
import subprocess
import sys
import threading

WIDTH, HEIGHT, FPS, SECONDS = 1920, 1080, 25, 30


def ffmpeg_exe() -> str:
    """本仓的约定是环境变量优先（CI 就是这么传的），退回到 PATH。"""
    return os.environ.get("FFMPEG_EXE") or shutil.which("ffmpeg") or "ffmpeg"


def build_clip(path: str) -> None:
    if os.path.exists(path):
        return

    print(f"building clip {WIDTH}x{HEIGHT}@{FPS} {SECONDS}s H.264+AAC -> {path}")
    subprocess.run(
        [
            ffmpeg_exe(), "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", f"testsrc2=size={WIDTH}x{HEIGHT}:rate={FPS}",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-t", str(SECONDS),
            "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
            "-g", str(FPS),
            "-c:a", "aac", "-b:a", "128k",
            path,
        ],
        check=True,
    )


class Handler(http.server.BaseHTTPRequestHandler):
    clip = ""

    def do_GET(self):  # noqa: N802（BaseHTTPRequestHandler 的命名）
        # `-re` 是**实时**播放（客户端按 25fps 收到，而不是一口气灌完），
        # `-stream_loop -1` 让它永不结束 —— 用例要连录十几秒。
        process = subprocess.Popen(
            [
                ffmpeg_exe(), "-hide_banner", "-loglevel", "error",
                "-re", "-stream_loop", "-1", "-i", self.clip,
                "-c", "copy", "-f", "mpegts", "-",
            ],
            stdout=subprocess.PIPE,
        )

        try:
            # ⚠️ 响应头也在 try 里：客户端连上就走（探针和最简用例都这样）时，
            # 这几行本身就抛 WinError 10053，落到 try 外面就会由 socketserver
            # 印一整段 traceback —— 而这份 stderr 正是起不来时给人看的那份。
            self.send_response(200)
            self.send_header("Content-Type", "video/mp2t")
            self.end_headers()

            while True:
                chunk = process.stdout.read(64 * 1024)
                if not chunk:
                    break
                self.wfile.write(chunk)
        except ConnectionError:
            pass  # 客户端走了（用例停录就是这样），不是错误
        finally:
            process.kill()
            process.wait()

    def log_message(self, fmt, *args):
        pass  # 每个连接两行日志，CI 上没人看，还会把真输出淹掉


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=8081)
    parser.add_argument("--clip", default=os.path.join(
        os.environ.get("TEMP", "/tmp"), "vidlog-fakesrc.mp4"))
    args = parser.parse_args()

    build_clip(args.clip)
    Handler.clip = args.clip

    url = f"http://127.0.0.1:{args.port}"
    print(f"fake network camera serving: {url}")
    print(f"  $env:VIDLOG_TEST_RTSP_URL = '{url}'")
    print(f"  $env:VIDLOG_TEST_CAMERA_SIZE = '{WIDTH}x{HEIGHT}'")
    sys.stdout.flush()

    with Server(("127.0.0.1", args.port), Handler) as server:
        server.serve_forever()

    return 0


if __name__ == "__main__":
    sys.exit(main())
