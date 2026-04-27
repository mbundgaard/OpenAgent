"""
Extracts audio tracks from every .mp4 in docs/voice/ and writes them as .wav
(PCM 16-bit, source sample rate preserved) next to the originals.

ffmpeg is supplied by imageio-ffmpeg, which is pip-installable and ships a
static binary — no system ffmpeg install required. The script auto-installs
imageio-ffmpeg into the current interpreter on first run if it's missing.

Usage:
    python scripts/extract-voice-audio.py
"""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path


def ensure_imageio_ffmpeg() -> str:
    """Returns the path to a usable ffmpeg binary, installing imageio-ffmpeg if needed."""
    try:
        import imageio_ffmpeg
    except ImportError:
        print("imageio-ffmpeg not installed; installing now...", flush=True)
        subprocess.check_call(
            [sys.executable, "-m", "pip", "install", "--quiet", "imageio-ffmpeg"]
        )
        import imageio_ffmpeg  # noqa: WPS433 — re-import after install

    return imageio_ffmpeg.get_ffmpeg_exe()


def extract(ffmpeg: str, mp4: Path) -> Path:
    """Runs ffmpeg to pull the audio track into a sibling .wav file. Returns the output path."""
    wav = mp4.with_suffix(".wav")
    # -vn drops video; -acodec pcm_s16le forces 16-bit PCM (universal, easy to analyse);
    # -y overwrites silently so reruns are idempotent.
    cmd = [ffmpeg, "-y", "-i", str(mp4), "-vn", "-acodec", "pcm_s16le", str(wav)]
    subprocess.run(cmd, check=True, capture_output=True)
    return wav


def main() -> int:
    repo_root = Path(__file__).resolve().parent.parent
    voice_dir = repo_root / "docs" / "voice"

    if not voice_dir.is_dir():
        print(f"docs/voice/ not found at {voice_dir}", file=sys.stderr)
        return 1

    mp4s = sorted(voice_dir.glob("*.mp4"))
    if not mp4s:
        print(f"No .mp4 files in {voice_dir}", file=sys.stderr)
        return 1

    ffmpeg = ensure_imageio_ffmpeg()
    print(f"Using ffmpeg: {ffmpeg}\n")

    for mp4 in mp4s:
        try:
            wav = extract(ffmpeg, mp4)
            print(f"  {mp4.name}  ->  {wav.name}  ({wav.stat().st_size:,} bytes)")
        except subprocess.CalledProcessError as exc:
            stderr = exc.stderr.decode("utf-8", errors="replace") if exc.stderr else ""
            print(f"  FAILED on {mp4.name}: ffmpeg exit {exc.returncode}\n{stderr}", file=sys.stderr)
            return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
