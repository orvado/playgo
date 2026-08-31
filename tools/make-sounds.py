#!/usr/bin/env python3
"""Generates the tiny WAV effects used when stones are placed and captured.

The sounds are synthesised rather than recorded so the repository stays free of
large opaque binaries and the tone can be retuned by editing numbers here.

    place.wav    a short wooden knock: stone meeting board
    capture.wav  a deeper, doubled knock: stones being lifted off

Run from the repository root:

    python tools/make-sounds.py

Each "hit" is a handful of damped sine partials (the resonant body of the board
and the stone) plus a very short noise burst, which is what gives a knock its
attack.
"""

import math
import random
import struct
import wave
from pathlib import Path

SAMPLE_RATE = 22050
AMPLITUDE = 0.92  # headroom so the loudest hit stays unclipped

OUT_DIR = Path(__file__).resolve().parent.parent / "src" / "PlayGo.App" / "Sounds"


def hit(t, partials, noise_amp, noise_tau, env_tau, rng):
    """One knock, starting at time t (seconds). Returns the sample value."""
    if t < 0:
        return 0.0
    value = 0.0
    for freq, amp, tau in partials:
        value += amp * math.sin(2 * math.pi * freq * t) * math.exp(-t / tau)
    if noise_tau > 0:
        value += noise_amp * (rng.random() * 2 - 1) * math.exp(-t / noise_tau)
    # Global envelope plus a sub-millisecond fade-in so it does not click.
    return value * math.exp(-t / env_tau) * min(1.0, t / 0.0004)


def render(path, duration, hits, seed=7):
    """Sums several hits into a mono 16-bit WAV file."""
    rng = random.Random(seed)
    frames = int(SAMPLE_RATE * duration)
    data = bytearray()

    for i in range(frames):
        now = i / SAMPLE_RATE
        sample = sum(hit(now - offset, *spec, rng) for offset, *spec in hits)
        sample = max(-1.0, min(1.0, sample)) * AMPLITUDE
        data += struct.pack("<h", int(sample * 32767))

    path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SAMPLE_RATE)
        w.writeframes(bytes(data))

    print(f"{path.name}: {duration * 1000:.0f} ms, {path.stat().st_size} bytes")


def main():
    # A stone on wood: bright and tight, with a little low body under it.
    place = [
        (0.000, [(770, 0.55, 0.018), (1180, 0.32, 0.012), (165, 0.30, 0.030)], 0.50, 0.0025, 0.024),
    ]

    # Captures lift several stones, so: deeper, longer, and doubled.
    capture = [
        (0.000, [(430, 0.50, 0.030), (690, 0.30, 0.022), (140, 0.35, 0.045)], 0.45, 0.0035, 0.040),
        (0.055, [(390, 0.34, 0.026), (620, 0.20, 0.018), (130, 0.24, 0.038)], 0.30, 0.0030, 0.034),
    ]

    render(OUT_DIR / "place.wav", 0.09, place)
    render(OUT_DIR / "capture.wav", 0.16, capture)


if __name__ == "__main__":
    main()
