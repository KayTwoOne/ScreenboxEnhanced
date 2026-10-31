"""Regenerates every Screenbox Enhanced logo asset from one vector master.

The mark descends from the original Screenbox logo - a rounded box, split horizontally
into two tones, carrying a play glyph - but is redrawn rather than recoloured: a warm
coral "screen" sits at the front of a stack of progressively narrower layers, so the
silhouette reads as a library of media rather than a single player. That keeps the
lineage visible while giving the fork its own identity.

A single centred card behind the screen was tried first and rejected: at taskbar sizes
it reads as a briefcase handle. Two inset layers read as a stack.

Run from anywhere:  python branding/render_icons.py
Requires ImageMagick 7 with the librsvg delegate (`magick -list format` shows RSVG).

Placement ratios are measured from the original asset set so the new icons sit exactly
where Windows expects them. Unlike the original, this renders the full set of
Square44x44 target sizes (16-256), so the taskbar and search never have to scale an
icon at common DPIs.
"""
from __future__ import annotations

import math
import pathlib
import subprocess
import tempfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
ASSETS = ROOT / "Screenbox" / "Assets"
BRANDING = ROOT / "branding"

# One vector template. Geometry is on a 256 grid with a 4-unit safety margin so
# anti-aliased edges never clip at full bleed.
FRONT = 'x="4" y="46" width="248" height="206" rx="44"'
NEAR = 'x="22" y="24" width="212" height="100" rx="34"'
FAR = 'x="44" y="4" width="168" height="100" rx="26"'
# Below 32px the two thin bands blur into one, so small sizes draw a single wider layer.
SMALL_NEAR = 'x="22" y="10" width="212" height="100" rx="34"'
PLAY = "113.3,113 113.3,185 175.3,149"

MARK = """<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <defs>
    <linearGradient id="far" x1="0" y1="4" x2="0" y2="28" gradientUnits="userSpaceOnUse">
      <stop offset="0" stop-color="{far_top}"/>
      <stop offset="1" stop-color="{far_bottom}"/>
    </linearGradient>
    <linearGradient id="near" x1="0" y1="10" x2="0" y2="50" gradientUnits="userSpaceOnUse">
      <stop offset="0" stop-color="{near_top}"/>
      <stop offset="1" stop-color="{near_bottom}"/>
    </linearGradient>
    <linearGradient id="front" x1="0" y1="46" x2="0" y2="252" gradientUnits="userSpaceOnUse">
      <stop offset="0" stop-color="{front_top}"/>
      <stop offset="0.5" stop-color="{front_mid_top}"/>
      <stop offset="0.5" stop-color="{front_mid_bottom}"/>
      <stop offset="1" stop-color="{front_bottom}"/>
    </linearGradient>
    <linearGradient id="sheen" x1="0" y1="46" x2="0" y2="96" gradientUnits="userSpaceOnUse">
      <stop offset="0" stop-color="#FFFFFF" stop-opacity="0.30"/>
      <stop offset="1" stop-color="#FFFFFF" stop-opacity="0"/>
    </linearGradient>
    <clipPath id="frontClip"><rect {front}/></clipPath>
    <!-- Shadows may only fall on the layers behind, never outside the mark: on a light
         taskbar an unclipped blur shows as a grey halo around the icon. -->
    <clipPath id="behindFront"><rect {near}/>{far_clip}</clipPath>
    <clipPath id="behindNear"><rect {far}/></clipPath>
    <filter id="ambient" x="-20%" y="-20%" width="140%" height="140%"><feGaussianBlur stdDeviation="6"/></filter>
    <filter id="glyph" x="-30%" y="-30%" width="160%" height="160%"><feGaussianBlur stdDeviation="4"/></filter>
  </defs>
  {far_layer}
  <rect {near} fill="url(#near)"/>
  {front_shadow}
  <!-- the screen: two tones split at the horizon, as in the original mark -->
  <rect {front} fill="url(#front)"/>
  <rect x="4" y="46" width="248" height="60" fill="url(#sheen)" clip-path="url(#frontClip)"/>
  {glyph_shadow}
  <polygon points="{play_pts}" fill="{play}" stroke="{play}" stroke-width="20" stroke-linejoin="round"/>
</svg>
"""

FAR_LAYER = """<rect {far} fill="url(#far)"/>
  <rect x="22" y="22" width="212" height="100" rx="34" fill="#6B2A05" opacity="0.30"
        filter="url(#ambient)" clip-path="url(#behindNear)"/>"""
FRONT_SHADOW = ('<rect x="4" y="44" width="248" height="206" rx="44" fill="#5A1405" opacity="0.32" '
                'filter="url(#ambient)" clip-path="url(#behindFront)"/>')
GLYPH_SHADOW = ('<polygon points="113.3,117 113.3,189 175.3,153" fill="#7A1A0A" opacity="0.28" '
                'stroke="#7A1A0A" stroke-width="20" stroke-linejoin="round" filter="url(#glyph)"/>')

# Warmth rises toward the viewer: gold at the back, orange, then the coral/ember screen.
# Each layer steps a clear distance in hue - pale gold, amber, coral - because at 32-48px
# a near-coral middle band merges with the screen and the stack stops reading.
DARK = dict(far_top="#FFE08A", far_bottom="#FFC547",
            near_top="#FFB64F", near_bottom="#FF9933",
            front_top="#FF8A5C", front_mid_top="#FF6D45",
            front_mid_bottom="#EE4E31", front_bottom="#D63A22", play="#FFF8F1")

# A light taskbar is near-white, so the back layers need more weight to keep their edges.
LIGHT = dict(far_top="#F7C344", far_bottom="#EBA61C",
             near_top="#FFA23D", near_bottom="#F2851C",
             front_top="#FF8052", front_mid_top="#FF6640",
             front_mid_bottom="#E5432A", front_bottom="#C9311C", play="#FFF8F1")

# Monochrome badge: white silhouette, layers separated by knocked-out gaps, play glyph
# knocked out.
BADGE = f"""<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <defs>
    <mask id="m" maskUnits="userSpaceOnUse" x="0" y="0" width="256" height="256">
      <rect {FAR} fill="#fff"/>
      <rect {NEAR} fill="#000" stroke="#000" stroke-width="16"/>
      <rect {NEAR} fill="#fff"/>
      <rect {FRONT} fill="#000" stroke="#000" stroke-width="16"/>
      <rect {FRONT} fill="#fff"/>
      <polygon points="{PLAY}" fill="#000" stroke="#000" stroke-width="20" stroke-linejoin="round"/>
    </mask>
  </defs>
  <rect width="256" height="256" fill="#fff" mask="url(#m)"/>
</svg>
"""


def mark_svg(palette: dict[str, str], *, size: int) -> str:
    # Blurs and the thinnest band collapse to noise below ~32px, so tiny sizes get a
    # single back layer and flat, crisp shapes.
    detailed = size >= 32
    near = NEAR if detailed else SMALL_NEAR
    return MARK.format(
        front=FRONT, near=near, far=FAR, play_pts=PLAY,
        far_clip=f"<rect {FAR}/>" if detailed else "",
        far_layer=FAR_LAYER.format(far=FAR) if detailed else "",
        front_shadow=FRONT_SHADOW if detailed else "",
        glyph_shadow=GLYPH_SHADOW if detailed else "",
        **palette)


def render(svg: str, logo: int, canvas: tuple[int, int], out: pathlib.Path, tmp: pathlib.Path) -> None:
    """Rasterise the 256-unit SVG at exactly `logo` px, centred on a transparent canvas."""
    src = tmp / "src.svg"
    src.write_text(svg, encoding="utf-8")
    density = 96 * logo / 256
    w, h = canvas
    subprocess.run(
        ["magick", "-background", "none", "-density", f"{density:.4f}", f"svg:{src}",
         "-resize", f"{logo}x{logo}!", "-gravity", "center", "-extent", f"{w}x{h}",
         "-define", "png:color-type=6", str(out)],
        check=True)


def px(value: float) -> int:
    """Round half up. Python's round() is banker's rounding, which turns 62.5 into 62."""
    return math.floor(value + 0.5)


def even_pad(canvas: int, ratio: float) -> int:
    """Logo size near `ratio * canvas` whose padding splits evenly on both sides."""
    logo = px(canvas * ratio)
    if (canvas - logo) % 2:
        logo -= 1
    return logo


def even_pad_2d(w: int, h: int, ratio: float) -> int:
    """As even_pad, but the padding must split evenly on both axes of a non-square canvas."""
    logo = even_pad(h, ratio)
    while (w - logo) % 2 or (h - logo) % 2:
        logo -= 1
    return logo


def main() -> None:
    BRANDING.joinpath("screenbox-enhanced-logo.svg").write_text(mark_svg(DARK, size=256), encoding="utf-8")
    BRANDING.joinpath("screenbox-enhanced-logo-light.svg").write_text(mark_svg(LIGHT, size=256), encoding="utf-8")
    BRANDING.joinpath("screenbox-enhanced-badge.svg").write_text(BADGE, encoding="utf-8")

    scales = {100: 1.0, 125: 1.25, 150: 1.5, 200: 2.0, 400: 4.0}
    jobs: list[tuple[str, int, tuple[int, int], str]] = []  # (kind, logo px, canvas, filename)

    # Taskbar / Start / search. Unplated = full bleed; plated = 75% centred.
    for t in (16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256):
        jobs.append(("dark", t, (t, t), f"Square44x44Logo.targetsize-{t}_altform-unplated.png"))
        jobs.append(("light", t, (t, t), f"Square44x44Logo.targetsize-{t}_altform-lightunplated.png"))
        jobs.append(("dark", even_pad(t, 0.75), (t, t), f"Square44x44Logo.targetsize-{t}.png"))

    for s, k in scales.items():
        c = px(44 * k)
        jobs.append(("dark", even_pad(c, 0.75), (c, c), f"Square44x44Logo.scale-{s}.png"))
        c = px(150 * k)
        jobs.append(("dark", even_pad(c, 1 / 3), (c, c), f"Square150x150Logo.scale-{s}.png"))
        w, h = px(310 * k), px(150 * k)
        jobs.append(("dark", even_pad_2d(w, h, 1 / 3), (w, h), f"Wide310x150Logo.scale-{s}.png"))
        c = px(310 * k)
        jobs.append(("dark", even_pad(c, 102 / 310), (c, c), f"LargeTile.scale-{s}.png"))
        c = px(71 * k)
        jobs.append(("dark", even_pad(c, 35 / 71), (c, c), f"SmallTile.scale-{s}.png"))
        c = px(50 * k)
        jobs.append(("dark", c, (c, c), f"StoreLogo.scale-{s}.png"))
        w, h = px(620 * k), px(300 * k)
        jobs.append(("dark", even_pad_2d(w, h, 1 / 3), (w, h), f"SplashScreen.scale-{s}.png"))

    jobs.append(("badge", 42, (48, 48), "LockScreenLogo.scale-200.png"))

    with tempfile.TemporaryDirectory() as d:
        tmp = pathlib.Path(d)
        for kind, logo, canvas, name in jobs:
            if kind == "badge":
                svg = BADGE
            else:
                svg = mark_svg(DARK if kind == "dark" else LIGHT, size=logo)
            render(svg, logo, canvas, ASSETS / name, tmp)
    print(f"rendered {len(jobs)} assets into {ASSETS}")


if __name__ == "__main__":
    main()
