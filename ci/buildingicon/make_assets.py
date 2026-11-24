#!/usr/bin/env python3
"""Generate synthetic Architect-menu fixtures for the real-host probe.

The host repository carries the shipped Defs and managed assemblies but only a small texture
subset. These fixtures use the exact paths requested by Core's RoyalBed, NutrientPasteDispenser and
PowerConduit defs. No game art is redistributed.
"""

from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image, ImageDraw


FIXTURES = {
    "Things/Building/Furniture/Bed/RoyalBed": (128, 128, (170, 118, 48, 255)),
    "Things/Building/Production/NutrientDispenser": (192, 256, (166, 206, 150, 255)),
    "Things/Building/Linked/PowerConduit_Atlas": (128, 128, (92, 104, 112, 255)),
    "Things/Building/Linked/PowerConduit_MenuIcon": (128, 128, (120, 132, 140, 255)),
}


def make(path: Path, size: tuple[int, int], colour: tuple[int, int, int, int]) -> None:
    width, height = size
    image = Image.new("RGBA", size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    margin = max(4, min(width, height) // 16)
    draw.rounded_rectangle(
        (margin, margin, width - margin - 1, height - margin - 1),
        radius=max(3, margin // 2),
        fill=colour,
        outline=(245, 245, 235, 255),
        width=max(2, margin // 3),
    )
    draw.rectangle(
        (width // 3, height // 3, width * 2 // 3, height * 2 // 3),
        fill=(245, 196, 80, 255),
    )
    path.parent.mkdir(parents=True, exist_ok=True)
    image.save(path, format="PNG")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--resources", type=Path, required=True,
                        help="the host's RimWorld_Data/Resources directory")
    args = parser.parse_args()
    root = args.resources / "Textures"
    if not root.is_dir():
        raise SystemExit(f"missing Resources/Textures: {root}")

    for relative, (width, height, colour) in FIXTURES.items():
        base = root / relative
        if relative.endswith(("RoyalBed", "NutrientDispenser")):
            for direction in ("north", "east", "south", "west"):
                target = base.with_name(base.name + "_" + direction + ".png")
                make(target, (width, height), colour)
                print(target.relative_to(args.resources))
        else:
            target = base.with_suffix(".png")
            make(target, (width, height), colour)
            print(target.relative_to(args.resources))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
