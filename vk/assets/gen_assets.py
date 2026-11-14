#!/usr/bin/env python3
"""Generates the test textures. Synthetic on purpose: no game art is redistributed.

The layout mirrors the real icon textures measured on the device (95.6% / 92.4% opaque, and the very
last texel transparent) - that single transparent texel is what turns a degenerate uv into a fully
blank tile, because `uv = (1,1)` clamps to it.
"""
import os

HERE = os.path.dirname(os.path.abspath(__file__))


def write(name, w, h, pixel):
    buf = bytearray()
    for y in range(h):
        for x in range(w):
            buf += bytes(pixel(x, y, w, h))
    with open(os.path.join(HERE, name), "wb") as f:
        f.write(buf)
    print(f"{name}: {w}x{h} {len(buf)} bytes")


def icon_pixel(x, y, w, h):
    # transparent 2px border + fully transparent last texel
    if x < 2 or y < 2 or x >= w - 2 or y >= h - 2:
        return (0, 0, 0, 0)
    if x == w - 1 and y == h - 1:
        return (0, 0, 0, 0)
    r = 60 + (x * 120) // max(1, w - 1)
    g = 40 + (y * 140) // max(1, h - 1)
    return (r, g, 90, 255)


def mask_pixel(x, y, w, h):
    # the engine's black default: RGB 0 keeps MaskGate(colPower)=0, i.e. no colour multiplication
    return (0, 0, 0, 255)


def clear_pixel(x, y, w, h):
    return (0, 0, 0, 0)


if __name__ == "__main__":
    write("icon.rgba", 128, 128, icon_pixel)
    write("icon_big.rgba", 192, 256, icon_pixel)
    write("mask.rgba", 128, 128, mask_pixel)
    write("clear.rgba", 128, 128, clear_pixel)
