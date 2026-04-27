"""
Converts the Advanced Voice Mode thinking clip in docs/voice/ to the wire format
Telnyx expects: 8 kHz mono A-law, headerless raw bytes.

Output: docs/voice/thinking-clip.al — directly streamable as Telnyx Media Streaming
payload. ffmpeg handles the resample + downmix + codec conversion in one pass.

Uses imageio-ffmpeg's bundled ffmpeg binary; no system install required.
"""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path


SOURCE_NAME = "ChatGPT Advanced Voice Mode _NEW_ Thinking Sound Effect.wav"
OUTPUT_NAME = "thinking-clip.al"


def ensure_imageio_ffmpeg() -> str:
    try:
        import imageio_ffmpeg
    except ImportError:
        print("imageio-ffmpeg not installed; installing now...", flush=True)
        subprocess.check_call(
            [sys.executable, "-m", "pip", "install", "--quiet", "imageio-ffmpeg"]
        )
        import imageio_ffmpeg  # noqa: WPS433

    return imageio_ffmpeg.get_ffmpeg_exe()


def main() -> int:
    repo_root = Path(__file__).resolve().parent.parent
    voice_dir = repo_root / "docs" / "voice"
    src = voice_dir / SOURCE_NAME
    dst = voice_dir / OUTPUT_NAME

    if not src.is_file():
        print(f"Source clip not found: {src}", file=sys.stderr)
        return 1

    ffmpeg = ensure_imageio_ffmpeg()

    # -ac 1 mono | -ar 8000 8 kHz | pcm_alaw codec | alaw raw container (no header).
    # -y overwrites silently so reruns are idempotent.
    cmd = [
        ffmpeg, "-y", "-i", str(src),
        "-ac", "1", "-ar", "8000",
        "-acodec", "pcm_alaw", "-f", "alaw",
        str(dst),
    ]
    subprocess.run(cmd, check=True, capture_output=True)

    size = dst.stat().st_size
    duration = size / 8000  # 1 byte per sample at 8 kHz A-law
    print(f"  {src.name}")
    print(f"    -> {dst.name}  ({size:,} bytes, ~{duration:.3f}s of audio)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
