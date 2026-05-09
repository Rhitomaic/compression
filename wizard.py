"""Interactive wizard steps — collects input file, preset, and output location."""

from pathlib import Path

import config

HERE = Path(__file__).parent


def ask(prompt: str, default: str = "") -> str:
    hint = f" (default: {default})" if default else ""
    val = input(f"  {prompt}{hint}:\n  > ").strip().strip('"').strip("'")
    return val if val else default


def divider():
    print("\n" + "─" * 44 + "\n")


def _clean_path(raw: str) -> str:
    raw = raw.strip()
    if raw.startswith("& "):   # PowerShell drag-and-drop prefix
        raw = raw[2:]
    return raw.strip('"').strip("'")


def step_input() -> Path:
    print("STEP 1 - Input file")
    print("  Drag & drop a file into this window, or paste the full path.\n")
    while True:
        path = Path(_clean_path(input("  > ")))
        if path.exists() and path.is_file():
            return path
        print(f"\n  [!] Can't find that file — try again.\n")


def step_preset() -> tuple[int | None, list[str], str]:
    """Returns (size_limit_bytes or None, codec_priority, label)."""
    print("STEP 2 — Target preset")
    print("  What are you compressing for?\n")

    preset_list = config.presets()
    for p in preset_list:
        print(f"  [{p['key']}]  {p['label']}")
    print()

    preset_map = {p["key"]: p for p in preset_list}
    while True:
        choice = input("  > ").strip()
        if choice in preset_map:
            p = preset_map[choice]
            break
        print(f"  [!] Enter a number 1–{len(preset_list)}.\n")

    size_limit     = int(p["size_mb"] * 1024 * 1024) if p["size_mb"] else None
    codec_priority = list(p["codecs"])
    label          = p["label"]

    if p["id"] == "custom":
        print()
        while True:
            raw = input("  Custom size limit in MB (or leave blank for none):\n  > ").strip()
            if not raw:
                size_limit = None
                break
            try:
                size_limit = int(float(raw) * 1024 * 1024)
                label = f"Custom ({raw} MB)"
                break
            except ValueError:
                print("  [!] Enter a number like 50\n")

    return size_limit, codec_priority, label


def step_encoder(working: list[dict], recommended: str | None) -> str | None:
    """Show working encoders with descriptions; let the user pick one."""
    print("STEP 3 - Encoder")
    print("  Choose which encoder to use. Press Enter for the recommended option.\n")

    if not working:
        return None

    for i, enc in enumerate(working, 1):
        rec  = "  <- recommended" if enc["name"] == recommended else ""
        slow = "  [!] very slow"  if enc["slow"] else ""
        print(f"  [{i}]  {enc['label']}{rec}{slow}")
        print(f"       {enc['desc']}")
        print()

    default_i = next((i for i, e in enumerate(working, 1) if e["name"] == recommended), 1)

    while True:
        raw = input(f"  > (default: {default_i})\n  > ").strip()
        if not raw:
            return working[default_i - 1]["name"]
        if raw.isdigit():
            idx = int(raw)
            if 1 <= idx <= len(working):
                chosen = working[idx - 1]
                if chosen["slow"]:
                    print()
                    print("  [!] This encoder is extremely slow.")
                    print("      A 10-minute video can easily take several hours.")
                    print()
                    if input("  Are you sure? [y/N]: ").strip().lower() != "y":
                        print()
                        continue
                return chosen["name"]
        print(f"  [!] Enter a number 1-{len(working)}, or press Enter for the default.\n")


def step_resolution(src_height: int, complexity: str, steps: list[int]) -> int:
    """
    Returns 0 for Auto, or a forced output height (int).
    If the returned height >= src_height, no scaling is applied.
    """
    print("STEP 4 - Resolution")
    print("  Choose an output resolution, or Auto to let the tool decide.\n")

    res_info = config.resolution_info()

    # Build option list: Auto (0) + source height + all standard steps below source
    options: list[int] = [0]
    if src_height not in steps:
        options.append(src_height)
    for h in steps:
        if h <= src_height:
            options.append(h)

    # Recommendation: for Complex content at 1080p+, suggest one step down
    if complexity == "Complex" and src_height >= 1080:
        recommended = next((h for h in steps if h < src_height), 0)
    else:
        recommended = 0  # Auto

    for i, res in enumerate(options, 1):
        if res == 0:
            label = "Auto"
            desc  = res_info.get("auto", "")
        elif res == src_height:
            label = f"{res}p (original)"
            desc  = res_info.get(str(res), "") or "Your source resolution."
        else:
            label = f"{res}p"
            desc  = res_info.get(str(res), "")
        rec = "  <- recommended" if res == recommended else ""
        print(f"  [{i}]  {label}{rec}")
        print(f"       {desc}")
        print()

    default_i = next((i for i, r in enumerate(options, 1) if r == recommended), 1)

    while True:
        raw = input(f"  > (default: {default_i})\n  > ").strip()
        if not raw:
            return options[default_i - 1]
        if raw.isdigit():
            idx = int(raw)
            if 1 <= idx <= len(options):
                return options[idx - 1]
        print(f"  [!] Enter a number 1-{len(options)}, or press Enter for the default.\n")


def step_output(input_path: Path) -> tuple[str, Path]:
    """Returns (filename, output_folder)."""
    print("STEP 5 - Output filename")
    default_name = f"{input_path.stem}_compressed.mp4"
    out_name = ask("Filename", default_name)
    if not out_name.lower().endswith(".mp4"):
        out_name += ".mp4"

    divider()

    print("STEP 6 - Output folder")
    default_folder = str(HERE / "out")
    raw_folder = ask("Folder", default_folder)
    out_folder = Path(_clean_path(raw_folder))
    out_folder.mkdir(parents=True, exist_ok=True)

    # Warn if output already exists
    out_path = out_folder / out_name
    while out_path.exists():
        print(f"\n  [!] '{out_name}' already exists in that folder.")
        print("  [1]  Overwrite it")
        print("  [2]  Choose a different name")
        print()
        choice = input("  > ").strip()
        if choice == "1":
            break
        elif choice == "2":
            base = Path(out_name).stem.removesuffix("_compressed")
            counter = 2
            suggested = f"{base}_compressed_{counter}.mp4"
            while (out_folder / suggested).exists():
                counter += 1
                suggested = f"{base}_compressed_{counter}.mp4"
            out_name = ask("New filename", suggested)
            if not out_name.lower().endswith(".mp4"):
                out_name += ".mp4"
            out_path = out_folder / out_name
        else:
            print("  [!] Enter 1 or 2.\n")

    return out_name, out_folder
