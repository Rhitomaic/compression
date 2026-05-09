"""CQP estimation, SSIM scoring, and the smart compress-to-target loop."""

import math
import subprocess
import threading
from pathlib import Path

import config
from encoder import run_encode, progress_bar


def estimate_start_cqp(
    encoder: str,
    src_bitrate_kbps: int,
    target_size: int,
    duration_s: float,
    complexity: str,
    default_cqp: int,
) -> int:
    """
    Estimate CQP starting point using actual video bitrate and duration.

    1. Compute available target bitrate from size limit + duration.
    2. Every +6 CQP roughly halves bitrate, so:
         delta = 6 * log2(src_bitrate / target_bitrate)
    3. Nudge higher for complex content (resists compression more).
    4. Back off 3 steps to leave room for the binary search quality sweep.
    """
    if duration_s <= 0 or src_bitrate_kbps <= 0:
        return default_cqp

    target_kbps = (target_size * 8) / (duration_s * 1000)
    if target_kbps >= src_bitrate_kbps:
        return default_cqp

    delta = int(6 * math.log2(src_bitrate_kbps / target_kbps))
    nudge = {"Simple": -1, "Medium": 0, "Complex": 3}.get(complexity, 0)

    estimated = default_cqp + delta + nudge
    cap = config.max_cqp(config.codec_family(encoder)) - 2
    return max(default_cqp, min(estimated - 3, cap))


def calc_ssim(
    ffmpeg: str, src: Path, compressed: Path,
    match_height: int | None = None,
    duration_s: float = 0.0,
) -> float | None:
    """
    Run ffmpeg's SSIM filter with a live progress bar.
    When match_height is set, scale src to the same height first so the score
    reflects compression quality, not resolution difference.
    """
    if match_height:
        lavfi = f"[0:v]scale=-2:{match_height}:flags=lanczos[ref];[ref][1:v]ssim"
    else:
        lavfi = "[0:v][1:v]ssim"

    cmd = [
        ffmpeg, "-y",
        "-progress", "pipe:1", "-nostats",
        "-i", str(src),
        "-i", str(compressed),
        "-lavfi", lavfi,
        "-f", "null", "-",
    ]

    proc = subprocess.Popen(
        cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        text=True, errors="replace",
    )

    ssim_val: list[float] = []
    stderr_lines: list[str] = []

    def _drain() -> None:
        for line in proc.stderr:
            stderr_lines.append(line)
            if "All:" in line:
                try:
                    ssim_val.append(float(line.split("All:")[1].split()[0]))
                except (IndexError, ValueError):
                    pass

    t = threading.Thread(target=_drain, daemon=True)
    t.start()

    shown_bar = False
    for line in proc.stdout:
        key, _, val = line.strip().partition("=")
        if key == "out_time_us" and duration_s > 0:
            try:
                us = int(val)
                if us >= 0:
                    pct = min(us / (duration_s * 1_000_000), 1.0)
                    print(f"\r    Quality   {progress_bar(pct)}", end="", flush=True)
                    shown_bar = True
            except ValueError:
                pass

    proc.wait()
    t.join()

    if shown_bar:
        print(f"\r    Quality   {progress_bar(1.0)}", flush=True)

    return ssim_val[0] if ssim_val else None


def compress_to_target(
    ffmpeg: str,
    src: Path,
    dst: Path,
    encoder: str,
    default_cqp: int,
    size_limit: int | None,
    scale_height: int | None = None,
    src_bitrate_kbps: int = 0,
    duration_s: float = 0.0,
    complexity: str = "Medium",
) -> bool:
    """
    Binary search CQP at a fixed resolution until output fits size_limit.
    Returns True if a file was written to dst.
    """
    if size_limit is None:
        res_label = f" at {scale_height}p" if scale_height else ""
        print(f"  CQP {default_cqp}{res_label}  (no size limit)\n")
        return run_encode(ffmpeg, src, dst, encoder, default_cqp, scale_height, duration_s)

    lo = estimate_start_cqp(encoder, src_bitrate_kbps, size_limit, duration_s, complexity, default_cqp)
    hi = config.max_cqp(config.codec_family(encoder), complexity)
    best: Path | None = None

    res_label = f" at {scale_height}p" if scale_height else ""
    print(f"  Target: {config.fmt_mb(size_limit)}  |  CQP {lo}-{hi}{res_label}\n")

    for attempt in range(1, 9):
        cqp = (lo + hi) // 2
        tmp = dst.parent / f"_sc_tmp_{cqp}.mp4"

        print(f"  Pass {attempt}  CQP {cqp:>2}")
        if not run_encode(ffmpeg, src, tmp, encoder, cqp, scale_height, duration_s):
            return False

        size = tmp.stat().st_size
        fits = size <= size_limit
        print(f"    {config.fmt_mb(size)}  {'OK fits' if fits else 'X  too big'}")

        if fits:
            if best and best.exists():
                best.unlink()
            best = tmp
            hi = cqp - 1
        else:
            tmp.unlink()
            lo = cqp + 1

        if lo > hi:
            break

    if best:
        best.replace(dst)
        return True
    return False


def compress_no_limit(
    ffmpeg: str,
    src: Path,
    dst: Path,
    encoder: str,
    default_cqp: int,
    ssim_floor: float,
    scale_height: int | None = None,
    duration_s: float = 0.0,
) -> tuple[bool, float | None]:
    """
    No size limit mode — binary search for the HIGHEST CQP where SSIM
    stays above the floor. Gives the smallest file that still looks good.
    Returns (success, final_ssim).
    """
    max_q  = config.max_cqp(config.codec_family(encoder))
    lo, hi = default_cqp, max_q
    best: tuple[int, Path, float] | None = None   # (cqp, tmp_path, ssim)

    print(f"  Squeezing: SSIM floor {ssim_floor}  |  CQP {lo}-{hi}\n")

    for attempt in range(1, 9):
        cqp = (lo + hi) // 2
        tmp = dst.parent / f"_sc_tmp_{cqp}.mp4"

        print(f"  Pass {attempt}  CQP {cqp:>2}")
        if not run_encode(ffmpeg, src, tmp, encoder, cqp, scale_height, duration_s):
            return False, None

        size = tmp.stat().st_size
        ssim = calc_ssim(ffmpeg, src, tmp, match_height=scale_height, duration_s=duration_s)

        if ssim is None:
            print(f"    {config.fmt_mb(size)}  SSIM unavailable - accepting")
            if best:
                best[1].unlink(missing_ok=True)
            tmp.replace(dst)
            return True, None

        fits = ssim >= ssim_floor
        print(f"    {config.fmt_mb(size)}  SSIM {ssim:.4f}  {'OK' if fits else 'X quality too low'}")

        if fits:
            if best:
                best[1].unlink(missing_ok=True)
            best = (cqp, tmp, ssim)
            lo = cqp + 1        # still above floor — can we push harder?
        else:
            tmp.unlink(missing_ok=True)
            hi = cqp - 1        # went too far, back off

        if lo > hi:
            break

    if best:
        best[1].replace(dst)
        return True, best[2]   # reuse already-computed SSIM, no second check

    # Couldn't even pass the floor at default_cqp — just encode at default
    print("  Could not improve on default quality — encoding at default CQP.")
    ok = run_encode(ffmpeg, src, dst, encoder, default_cqp, scale_height, duration_s)
    return ok, None


def compress_smart(
    ffmpeg: str,
    src: Path,
    dst: Path,
    encoder: str,
    default_cqp: int,
    size_limit: int | None,
    src_height: int,
    info: dict,
    forced_res: int = 0,
) -> tuple[bool, float | None, int | None]:
    """
    Full smart pipeline. forced_res=0 means auto (full resolution ladder).
    Any other value forces a specific output height.

    Returns (success, ssim_score, scale_height_used).
    scale_height_used=None means original resolution was kept.
    """
    complexity  = info.get("complexity", "Medium")
    floor       = config.ssim_floor(complexity)
    steps       = config.resolution_steps()
    duration_s  = info.get("duration_s", 0.0)
    min_height  = config.min_output_height(src_height)

    # Build resolution ladder
    if forced_res == 0:
        ladder = [None] + [h for h in steps if h < src_height and h >= min_height]
    else:
        scale  = None if forced_res >= src_height else forced_res
        ladder = [scale]

    print(f"  Quality floor:  SSIM {floor}  ({complexity} content)")
    if size_limit and forced_res == 0:
        print(f"  Resolution cap: {min_height}p minimum  (source: {src_height}p)")
    print()

    # ── no size limit: squeeze at chosen resolution ───────────────────────────
    if size_limit is None:
        scale = ladder[0]
        label = f"{src_height}p (original)" if scale is None else f"{scale}p"
        print(f"\n--- Resolution: {label} ---\n")
        success, ssim = compress_no_limit(
            ffmpeg, src, dst, encoder, default_cqp, floor,
            scale_height=scale, duration_s=duration_s,
        )
        return success, ssim, scale

    # ── size-limited: try resolution(s) in order ─────────────────────────────
    for res in ladder:
        label = f"{res}p" if res else f"{src_height}p (original)"
        print(f"\n--- Resolution: {label} ---\n")

        success = compress_to_target(
            ffmpeg, src, dst, encoder, default_cqp, size_limit, res,
            src_bitrate_kbps=info.get("bitrate_kbps", 0),
            duration_s=duration_s,
            complexity=complexity,
        )

        if not success:
            if len(ladder) == 1:
                print(f"  Could not hit target at {label}.")
                print("  Try a lower resolution or a larger size limit.")
            elif res == ladder[-1]:
                print(f"  Could not hit target at {label} (minimum resolution reached).")
            else:
                print(f"  Could not hit target at {label} - dropping to lower resolution...")
            continue

        print()
        ssim = calc_ssim(ffmpeg, src, dst, match_height=res, duration_s=duration_s)

        if ssim is None:
            print(f"  SSIM unavailable - accepting result.")
            return True, None, res

        print(f"  SSIM {ssim:.4f}  -  {config.ssim_label(ssim)}  (floor: {floor}  [{complexity}])")

        if ssim >= floor:
            return True, ssim, res

        # Quality missed the floor
        if len(ladder) == 1:
            print(f"  Quality below {complexity} floor - but resolution was manually set, keeping result.")
            return True, ssim, res

        print(f"  Quality below {complexity} floor ({floor}) - dropping to lower resolution...")
        if dst.exists():
            dst.unlink()

    return False, None, None
