# SmartCompress
> Drag in a video, pick a target, get a compressed file. No uploads, no manual settings, no potato quality.

---

## Why does this exist?

Compressing a video for Discord shouldn't require a PhD in ffmpeg. But here's the thing - every existing option is terrible in its own way:

- **Online tools** make you upload your file to some random server (no thanks), and they just smash it with a fixed bitrate regardless of what your video actually looks like
- **HandBrake** is great, but it assumes you already know what CRF means and why it matters
- **Raw ffmpeg** is basically a superpower, except most people don't want to spend 45 minutes reading the docs just to send a clip to their friends

So I built this. You drop a file in, pick what you're compressing for (Discord, smallest possible, whatever), and it figures out the rest. Codec, settings, resolution - all automatic. Runs entirely on your machine.

---

## Getting started

You'll need Python 3.10 or newer. Check with `python --version`.

```bash
# 1. Clone the repo
git clone https://github.com/your-username/smartcompress.git
cd smartcompress

# 2. (Optional but recommended) create a virtual environment
python -m venv .venv
.venv\Scripts\activate       # Windows
# source .venv/bin/activate  # Mac / Linux

# 3. Install dependencies
pip install -r requirements.txt
```

That's it. No ffmpeg install needed - it's bundled.

---

## How to use it

```bash
python compress.py
```

That's it. It'll walk you through everything:

1. Drop in your file (drag & drop right into the terminal works)
2. Pick a preset
3. Choose a filename and output folder (or just hit Enter for the defaults)
4. Press Enter and wait

Output lands in an `out/` folder by default.

---

## Presets

| Preset | Size Limit | Notes |
|---|---|---|
| Discord Free | 10 MB | H.264 for inline playback - H.265 opt-in available |
| Discord Nitro Basic | 25 MB | Same as above |
| Discord Nitro | 500 MB | Same as above |
| Smallest Possible | No limit | Any codec, quality-first |
| Custom | You decide | You decide |

The limit is a **ceiling, not a goal.** The tool always chases the smallest great-looking file it can produce - it won't pad a 2 MB clip to 500 MB just because Nitro allows it.

### H.265 and Discord

Discord plays H.264 inline. H.265 forces a download button instead of playing in the chat - which most people won't bother clicking. So Discord presets default to H.264, but you'll be asked if you want H.265 before the encode starts. Your call every time, never a silent switch.

---

## What it actually does under the hood

### It doesn't guess bitrate - it searches CQP

Most tools ask "what bitrate do you want?" and you have to know the answer. SmartCompress uses **Constant Quality Parameter (CQP)** encoding instead, which means it targets a quality level rather than a fixed bitrate. Then it binary searches the CQP value until the output fits your target size.

It also uses bitrate math to make a smart starting guess, so it converges in 3–5 passes instead of brute-forcing from one end:

```
estimated_delta = 6 * log2(source_size / target_size)
```

Every +6 CQP roughly halves the file size, so this gets close on the first pass and fine-tunes from there.

### It checks how complex your video is

Before encoding, it calculates a **bits-per-pixel-per-frame** score:

```
bppf = bitrate / (width × height × fps)
```

High bppf means complex content (fast motion, film grain, lots of detail) - harder to compress without losing quality. Low bppf means clean, simple footage that squishes down easily. This shows up in the output so you know what you're working with.

### It drops resolution if it has to

If the quality score (SSIM) falls below an acceptable floor at the current resolution, it doesn't just give up or silently produce a bad file - it steps down the resolution ladder and tries again:

```
original → 1080p → 720p → 540p → 480p → ...
```

SSIM comparison is resolution-normalized too, so it's measuring compression quality, not just the fact that the resolution changed.

### It validates encoders before using them

GPU encoders can be registered in ffmpeg but fail at runtime if the driver isn't cooperating (CUDA not loading, AMF not initializing, etc.). SmartCompress test-encodes a tiny dummy clip on each candidate before committing - so it never starts a real encode only to crash halfway through.

Hardware priority order:
| Hardware | Priority |
|---|---|
| NVIDIA GPU (RTX 40xx) | `av1_nvenc` → `hevc_nvenc` → `h264_nvenc` |
| AMD GPU (RX 7000+ / Radeon iGPU) | `av1_amf` → `hevc_amf` → `h264_amf` |
| CPU fallback | `libx265` → `libx264` |

---

## Dependencies

```bash
pip install imageio-ffmpeg Pillow vtracer
```

ffmpeg is bundled via `imageio-ffmpeg` - no system install needed. If you have ffmpeg in PATH anyway (for ffprobe), that works too and is used automatically.

---

## Config

Everything tunable lives in `config.json` - presets, CQP defaults, max CQP per codec, SSIM thresholds. You can edit it without touching any Python.

```json
{
  "ssim_floor": 0.87,
  "default_cqp": { "hevc_amf": 28, "h264_amf": 23, ... },
  "resolution_steps": [2160, 1440, 1080, 720, 540, 480, 360]
}
```

---

## Current Stack

- `imageio-ffmpeg` - bundled ffmpeg binary
- `subprocess` - ffmpeg encoding and analysis
- `Pillow` - image processing (installed, not yet wired up)
- `vtracer` - SVG vectorization (installed, not yet wired up)
- Config-driven via `config.json`

A C# / Avalonia UI port is planned down the road for a proper cross-platform GUI.

---

## Roadmap

### Done
- [x] Interactive CLI wizard - guided step-by-step, no flags to memorize
- [x] CQP binary search loop - bitrate-math starting estimate, converges in ~3-5 passes
- [x] Hardware detection + encoder validation - runtime test, not just compile-time probing
- [x] SSIM quality scoring - resolution-normalized comparison against source
- [x] Resolution ladder - auto-drops when quality floor isn't met
- [x] Content complexity analysis - bits-per-pixel-per-frame shown before encode
- [x] H.265 opt-in - choose at preset selection, or offered as fallback when H.264 can't hit the target

### Up next
- [ ] Image pipeline (WebP / AVIF / SVG vectorization for flat graphics)
- [ ] Benchmark mode - one-time hardware calibration, OBS-style
- [ ] Community lookup table - opt-in crowdsourced encode data, hardware-bucketed

### Later
- [ ] C# / Avalonia UI port
- [ ] Neural network on top of lookup table (long-term)

---

## What this isn't

- Not a cloud tool - everything runs on your machine, nothing gets uploaded
- Not a codec - it's an orchestration layer on top of ffmpeg
- Not a replacement for HandBrake if you're a power user who likes manual control - this is for everyone else
