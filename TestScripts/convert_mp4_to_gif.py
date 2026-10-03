"""
FlowAuto MP4 → GIF 转换脚本
将 TestScripts 目录下的 MP4 录屏转换为 GIF，供文档嵌入使用。

依赖: pip install moviepy pillow
（如 moviepy 不可用，自动回退到 ffmpeg 命令行）
"""

import os
import sys
import subprocess
import shutil
from pathlib import Path

# ── 配置 ──────────────────────────────────────────
SCRIPT_DIR = Path(__file__).resolve().parent          # TestScripts/
GIF_DIR = SCRIPT_DIR / "gifs"                         # 输出目录

# MP4 → GIF 映射（文件名不含扩展名 → 文档中嵌入名）
# 若未指定映射，脚本自动扫描 TestScripts 根目录下所有 .mp4
MP4_FILES = [
    "E:\AutoScript\FlowAuto\\test_click_element.flow.mp4",
    "E:\AutoScript\FlowAuto\\test_click_hsv.flow_template_match.mp4",
    "E:\AutoScript\FlowAuto\\test_click_matchhsv.flow.mp4",
    "E:\AutoScript\FlowAuto\\test_colormotion_hsv.flow.mp4",
    "E:\AutoScript\FlowAuto\\test_statechange.flow.mp4",
    "E:\AutoScript\FlowAuto\\test_colorcal.flow.mp4"
]

# GIF 参数
FPS = 10          # 帧率（降低可减小文件大小）
MAX_WIDTH = 480   # 最大宽度（等比缩放）


# ── 方法1: moviepy ────────────────────────────────
def convert_with_moviepy(mp4_path: Path, gif_path: Path, fps: int, max_width: int):
    try:
        from moviepy import VideoFileClip
    except ImportError:
        try:
            from moviepy.editor import VideoFileClip
        except ImportError:
            return False

    print(f"  [moviepy] {mp4_path.name} → {gif_path.name}")
    clip = VideoFileClip(str(mp4_path))

    # 限制时长（超过 15 秒裁剪前 15 秒）
    if clip.duration > 15:
        clip = clip.subclipped(0, 15)

    # 等比缩放
    if clip.w > max_width:
        clip = clip.resized(width=max_width)

    clip.write_gif(str(gif_path), fps=fps, logger=None)
    clip.close()
    return True


# ── 方法2: ffmpeg 命令行 ──────────────────────────
def convert_with_ffmpeg(mp4_path: Path, gif_path: Path, fps: int, max_width: int):
    if shutil.which("ffmpeg") is None:
        return False

    print(f"  [ffmpeg]  {mp4_path.name} → {gif_path.name}")
    # ffmpeg 两步法：调色板 → GIF（画质最佳）
    palette_path = gif_path.with_suffix(".palette.png")
    try:
        subprocess.run([
            "ffmpeg", "-y", "-hide_banner", "-loglevel", "error",
            "-i", str(mp4_path),
            "-t", "15",
            "-vf", f"fps={fps},scale={max_width}:-1:flags=lanczos,palettegen=stats_mode=diff",
            str(palette_path)
        ], check=True)

        subprocess.run([
            "ffmpeg", "-y", "-hide_banner", "-loglevel", "error",
            "-i", str(mp4_path),
            "-i", str(palette_path),
            "-t", "15",
            "-lavfi", f"fps={fps},scale={max_width}:-1:flags=lanczos [x]; [x][1:v] paletteuse=dither=bayer:bayer_scale=5"
        ], check=True)
    finally:
        if palette_path.exists():
            palette_path.unlink()

    return True


# ── 主流程 ────────────────────────────────────────
def main():
    GIF_DIR.mkdir(parents=True, exist_ok=True)

    # 收集 MP4 文件
    mp4_sources = []
    for name in MP4_FILES:
        p = SCRIPT_DIR / name
        if p.exists():
            mp4_sources.append(p)
        else:
            print(f"⚠️  未找到: {name}")

    # 如果指定列表全空，自动扫描
    if not mp4_sources:
        mp4_sources = sorted(SCRIPT_DIR.glob("*.mp4"))
        if not mp4_sources:
            print("❌ 没有找到任何 .mp4 文件")
            return 1

    print(f"📁 找到 {len(mp4_sources)} 个 MP4 文件\n")

    success = 0
    for mp4 in mp4_sources:
        gif_name = mp4.stem + ".gif"
        gif_path = GIF_DIR / gif_name

        # 尝试 moviepy → ffmpeg 回退
        if convert_with_moviepy(mp4, gif_path, FPS, MAX_WIDTH):
            success += 1
        elif convert_with_ffmpeg(mp4, gif_path, FPS, MAX_WIDTH):
            success += 1
        else:
            print(f"  ❌ 转换失败 (需要 moviepy 或 ffmpeg): {mp4.name}")

    # 汇总
    size_mb = sum(
        f.stat().st_size for f in GIF_DIR.glob("*.gif")
        if f.stem in [p.stem for p in mp4_sources]
    ) / (1024 * 1024)

    print(f"\n✅ 转换完成: {success}/{len(mp4_sources)} 个")
    print(f"📂 输出目录: {GIF_DIR}")
    print(f"📦 总大小:   {size_mb:.1f} MB")

    # 生成 Markdown 嵌入片段
    print(f"\n── Markdown 嵌入代码 (复制到 测试方法与文档.md) ──\n")
    for mp4 in mp4_sources:
        name = mp4.stem
        label = name.replace("test_", "").replace(".flow", "").replace("_", " ").title()
        rel_path = f"gifs/{name}.gif"
        print(f"![{label}]({rel_path})")
        print()

    return 0


if __name__ == "__main__":
    sys.exit(main())
