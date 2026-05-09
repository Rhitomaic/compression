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
git clone https://github.com/Mitzingdash/compression.git
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

It'll walk you through six steps:

1. **Input file** - drag & drop right into the terminal, or paste the path
2. **Preset** - Discord Free/Nitro, Smallest Possible, or Custom size limit
3. **Encoder** - shows everything working on your hardware with descriptions and a recommendation
4. **Resolution** - pick a specific output resolution, or let the tool decide automatically
5. **Output filename** - or just hit Enter for the default
6. **Output folder** - defaults to `out/` next to the script

Then it confirms your settings and waits for Enter before doing anything. Output lands in `out/` by default.

---

## Presets

| Preset | Size Limit | Codec default |
|---|---|---|
| Discord Free | 10 MB | H.264 |
| Discord Nitro Basic | 25 MB | H.264 |
| Discord Nitro | 500 MB | H.264 |
| Smallest Possible | No limit | Best available |
| Custom | You decide | Best available |

The limit is a **ceiling, not a goal.** The tool always chases the smallest great-looking file it can produce - it won't pad a 2 MB clip to 500 MB just because Nitro allows it.

### H.265 and Discord

Discord plays H.264 inline. H.265 forces a download button instead of playing in chat - which most people won't bother clicking. So Discord presets recommend H.264 by default, but the encoder step lets you switch to H.265 (or anything else) if you know what you're doing.

---

## What it actually does under the hood

### It probes your hardware first

Before you pick an encoder, it test-encodes a tiny dummy clip through every candidate on your machine. GPU encoders can be registered in ffmpeg but fail at runtime if the driver isn't cooperating (CUDA not loading, AMF not initializing, etc.). The probe catches all of that upfront - so you only see encoders that actually work.

It also recommends the best one for your situation. For "Smallest Possible" it skips slow software encoders (like `libaom-av1`) and recommends the fastest GPU encoder that gives the best quality.

### It doesn't guess bitrate - it searches CQP

Most tools ask "what bitrate do you want?" and you have to know the answer. SmartCompress uses **Constant Quality Parameter (CQP)** encoding instead, which means it targets a quality level rather than a fixed bitrate. Then it binary searches the CQP value until the output fits your target size.

It uses bitrate math to make a smart starting guess, so it converges in 3-5 passes instead of brute-forcing from one end:

```
estimated_delta = 6 * log2(source_bitrate / target_bitrate)
```

Every +6 CQP roughly halves the file size, so this gets close on the first pass and fine-tunes from there.

### It checks how complex your video is

Before encoding, it calculates a **bits-per-pixel-per-frame** score:

```
bppf = bitrate / (width x height x fps)
```

High bppf means complex content (fast motion, film grain, lots of detail) - harder to compress without losing quality. Low bppf means clean, simple footage that squishes down easily. This affects the quality floors and CQP limits used during the search.

### It measures quality with SSIM

Every pass is scored with **SSIM** (Structural Similarity Index) - a perceptual quality metric that compares the compressed output against the original. The comparison is resolution-normalized, so dropping to 720p doesn't fake a perfect score just because the frames are smaller.

Quality floors by content type:

| Content | SSIM floor |
|---|---|
| Simple | 0.95 |
| Medium | 0.93 |
| Complex (gameplay, fast motion) | 0.96 |

Both the encoding pass and the SSIM check show live progress bars so you always know what's happening.

### It drops resolution if it has to

If quality falls below the floor at the current resolution, it steps down the ladder and tries again rather than giving up or producing a bad file:

```
original → 1080p → 720p → 540p → 480p → 360p
```

You can also skip the auto ladder entirely and force a specific resolution in step 4. For fast-moving gameplay, locking to 720p often looks sharper than letting a heavily-compressed 1080p through.

---

## Dependencies

```
imageio-ffmpeg   bundled ffmpeg - no system install needed
Pillow           image processing (for future image pipeline)
vtracer          SVG vectorization (for future image pipeline)
```

---

## Config

Everything tunable lives in `config.json` - no Python needed to adjust it.

```json
{
  "ssim_floors":          { "Simple": 0.95, "Medium": 0.93, "Complex": 0.96 },
  "default_cqp":          { "hevc_amf": 28, "h264_amf": 23, ... },
  "max_cqp":              { "h265": 45, "h264": 45, "av1": 55 },
  "max_cqp_by_complexity": { "Simple": 35, "Medium": 42, "Complex": 45 },
  "resolution_steps":     [2160, 1440, 1080, 720, 540, 480, 360],
  "min_output_height":    [[1440, 1080], [1080, 720], [720, 540], [0, 360]]
}
```

---

## Roadmap

### Done
- [x] Interactive CLI wizard - guided step-by-step, no flags to memorize
- [x] Hardware encoder detection + runtime validation - test-encode, not just compile-time probing
- [x] Encoder selection step - all working encoders listed with descriptions and a recommendation
- [x] Resolution selection step - pick specific resolution or let Auto decide
- [x] CQP binary search - bitrate-math starting estimate, converges in ~3-5 passes
- [x] SSIM quality scoring - resolution-normalized comparison, live progress bar
- [x] Content complexity analysis - bits-per-pixel-per-frame, affects floors and CQP limits
- [x] Resolution ladder - auto-drops when quality floor isn't met
- [x] Codec fallback - H.264 can offer H.265 if it can't hit the target
- [x] Live progress bars - encoding and quality check both show real-time bars

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
