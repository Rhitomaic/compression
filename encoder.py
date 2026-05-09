"""Encoder probing, validation, selection, and raw ffmpeg encoding."""

import subprocess
import threading
from pathlib import Path

import config


def progress_bar(pct: float, width: int = 28) -> str:
    filled = int(width * pct)
    if pct < 1.0 and filled < width:
        bar = "=" * filled + ">" + " " * (width - filled - 1)
    else:
        bar = "=" * width
    return f"[{bar}] {pct * 100:5.1f}%"


def probe_encoders(ffmpeg: str) -> set[str]:
    """Return all video encoder names compiled into this ffmpeg build."""
    result = subprocess.run(
        [ffmpeg, "-encoders", "-v", "quiet"],
        capture_output=True, text=True,
    )
    found = set()
    for line in result.stdout.splitlines():
        parts = line.strip().split()
        if len(parts) >= 2 and len(parts[0]) >= 6 and parts[0][0] == "V":
            found.add(parts[1])
    return found


def test_encoder(ffmpeg: str, encoder: str) -> bool:
    """Encode a tiny dummy clip to catch CUDA/AMF runtime failures.
    AMF requires yuv420p and a minimum resolution to initialize."""
    cmd = [
        ffmpeg, "-y",
        "-f", "lavfi", "-i", "color=black:size=256x144:rate=30:duration=0.5",
        "-pix_fmt", "yuv420p",
        "-c:v", encoder,
        "-f", "null", "-",
    ]
    return subprocess.run(cmd, capture_output=True).returncode == 0


def probe_working_encoders(ffmpeg: str) -> list[dict]:
    """Test every encoder candidate; return working ones with display metadata."""
    available = probe_encoders(ffmpeg)
    infos = config.encoder_info()
    to_test = [
        (family, enc)
        for family, cands in config.encoder_candidates().items()
        for enc in cands
        if enc in available
    ]
    total = len(to_test)
    result = []
    for i, (family, enc) in enumerate(to_test, 1):
        print(f"  [{i}/{total}]  {enc:<16}", end="  ", flush=True)
        ok = test_encoder(ffmpeg, enc)
        print("OK" if ok else "failed - skipping")
        if ok:
            info = infos.get(enc, {})
            result.append({
                "name":   enc,
                "family": family,
                "label":  info.get("label", enc),
                "desc":   info.get("desc", ""),
                "slow":   info.get("slow", False),
            })
    return result


def pick_recommended(working: list[dict], codec_priority: list[str]) -> str | None:
    """Return the best working encoder for the priority list, skipping slow encoders."""
    for family in codec_priority:
        for enc in working:
            if enc["family"] == family and not enc["slow"]:
                return enc["name"]
    # Fall back to slow encoders if nothing else is available
    for family in codec_priority:
        for enc in working:
            if enc["family"] == family:
                return enc["name"]
    return working[0]["name"] if working else None


def pick_encoder(ffmpeg: str, codec_priority: list[str], available: set[str]) -> str | None:
    """Walk the priority list and return the first encoder that actually works."""
    candidates = config.encoder_candidates()
    for family in codec_priority:
        for encoder in candidates[family]:
            if encoder in available:
                if test_encoder(ffmpeg, encoder):
                    return encoder
                print(f"  {encoder} - registered but not usable, skipping...")
    return None


def run_encode(
    ffmpeg: str, src: Path, dst: Path, encoder: str, cqp: int,
    scale_height: int | None = None,
    duration_s: float = 0.0,
) -> bool:
    """Run a single ffmpeg encode. Streams a progress bar when duration is known."""
    if "nvenc" in encoder:
        quality = ["-rc", "vbr", "-cq", str(cqp), "-b:v", "0"]
    elif "amf" in encoder:
        quality = ["-rc", "cqp", "-qp_i", str(cqp), "-qp_p", str(cqp), "-qp_b", str(cqp)]
    else:
        quality = ["-crf", str(cqp)]

    scale = ["-vf", f"scale=-2:{scale_height}:flags=lanczos"] if scale_height else []

    cmd = [
        ffmpeg, "-y",
        "-progress", "pipe:1", "-nostats", "-loglevel", "error",
        "-i", str(src),
        "-c:v", encoder, *quality,
        *scale,
        "-c:a", "aac", "-b:a", "128k",
        str(dst),
    ]

    proc = subprocess.Popen(
        cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        text=True, errors="replace",
    )

    stderr_lines: list[str] = []

    def _drain_stderr() -> None:
        for line in proc.stderr:
            stderr_lines.append(line)

    t = threading.Thread(target=_drain_stderr, daemon=True)
    t.start()

    shown_bar = False
    for line in proc.stdout:
        key, _, val = line.strip().partition("=")
        if key == "out_time_us" and duration_s > 0:
            try:
                us = int(val)
                if us >= 0:
                    pct = min(us / (duration_s * 1_000_000), 1.0)
                    print(f"\r    Encoding  {progress_bar(pct)}", end="", flush=True)
                    shown_bar = True
            except ValueError:
                pass

    proc.wait()
    t.join()

    if shown_bar:
        print(f"\r    Encoding  {progress_bar(1.0)}", flush=True)

    if proc.returncode != 0:
        print(f"\n[ERROR] ffmpeg failed:\n{''.join(stderr_lines[-50:])}")
        return False
    return True
