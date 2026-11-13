"""Decides whether the Deoxys on screen is shiny.

Two independent checks against the shiny sprite PNG; either one is enough to
call a shiny (missing a shiny is far worse than a false alarm):

1. Template match: the shiny PNG (transparent background ignored) is scaled and
   slid over the battle area. Score = colour similarity 0..1 (1 = identical).
2. Colour check: shiny Deoxys is yellow/green, normal Deoxys is orange/teal.
   We count pixels with the shiny yellow hue (taken from the PNG) and compare
   with how many a normal Deoxys has (measured during calibration). This keeps
   working even if the form or capture colours differ slightly from the PNG.

"Is Deoxys on screen at all" comes from a reference crop of the NORMAL Deoxys
that you capture once with the calibrator.
"""
from __future__ import annotations

from dataclasses import dataclass

import cv2
import numpy as np


def match_in_region(frame, ref, box, pad=30) -> float:
    """Similarity (0..1) of a reference crop to the frame near where it was captured."""
    if ref is None or box is None:
        return -1.0
    x, y, w, h = box
    H, W = frame.shape[:2]
    x0, y0 = max(0, x - pad), max(0, y - pad)
    x1, y1 = min(W, x + w + pad), min(H, y + h + pad)
    region = frame[y0:y1, x0:x1]
    if region.shape[0] < ref.shape[0] or region.shape[1] < ref.shape[1]:
        return -1.0
    f = min(1.0, 160.0 / max(ref.shape[:2]))
    r = cv2.resize(ref, None, fx=f, fy=f, interpolation=cv2.INTER_AREA)
    s = cv2.resize(region, None, fx=f, fy=f, interpolation=cv2.INTER_AREA)
    if s.shape[0] < r.shape[0] or s.shape[1] < r.shape[1]:
        return -1.0
    return float(cv2.matchTemplate(s, r, cv2.TM_CCOEFF_NORMED).max())


@dataclass
class Result:
    shiny_score: float      # template similarity to the shiny PNG (0..1)
    yellow_frac: float      # shiny-yellow pixels / expected sprite area
    normal_score: float     # similarity to the normal-Deoxys reference (0..1, -1 = no ref)
    is_shiny: bool
    is_normal: bool
    present: bool
    location: tuple | None  # (x, y, w, h) of best shiny-template match inside the ROI

    def __str__(self):
        return (f"shiny={self.shiny_score:.3f} yellow={self.yellow_frac:.3f} "
                f"normal={self.normal_score:.3f} -> "
                f"{'SHINY' if self.is_shiny else 'normal' if self.is_normal else 'not visible yet'}")


class ShinyDetector:
    def __init__(self, sprite_path, cal: dict, det_cfg: dict, normal_ref=None):
        rgba = cv2.imread(str(sprite_path), cv2.IMREAD_UNCHANGED)
        if rgba is None:
            raise FileNotFoundError(sprite_path)
        if rgba.shape[2] == 4:
            alpha = rgba[:, :, 3]
        else:  # no transparency: treat the top-left pixel colour as background
            bg = rgba[0, 0]
            alpha = np.where(np.all(rgba == bg, axis=2), 0, 255).astype(np.uint8)
        ys, xs = np.nonzero(alpha > 0)
        y0, y1, x0, x1 = ys.min(), ys.max() + 1, xs.min(), xs.max() + 1
        self.tmpl = rgba[y0:y1, x0:x1, :3].copy()
        self.tmask = (alpha[y0:y1, x0:x1] > 0).astype(np.uint8)
        self.opaque_px = int(self.tmask.sum())

        # Shiny hue range derived from the bright, saturated colours of the PNG.
        hsv = cv2.cvtColor(self.tmpl, cv2.COLOR_BGR2HSV)
        sel = (self.tmask > 0) & (hsv[:, :, 1] > 120) & (hsv[:, :, 2] > 150)
        hues = hsv[:, :, 0][sel]
        # The dominant shiny body colour (yellow) is the most common hue bucket.
        hist = np.bincount(hues, minlength=180)
        peak = int(np.argmax(np.convolve(hist, np.ones(5), "same")))
        self.hue_lo, self.hue_hi = max(0, peak - 7), min(179, peak + 7)

        self.cal = cal
        self.cfg = det_cfg
        self.scale = float(cal.get("sprite_scale", 4.0))
        self.normal_ref = normal_ref
        self._scaled_cache = {}

    # ------------------------------------------------------------------ helpers
    def _scaled(self, s):
        key = round(s, 3)
        if key not in self._scaled_cache:
            w = max(4, int(round(self.tmpl.shape[1] * s)))
            h = max(4, int(round(self.tmpl.shape[0] * s)))
            t = cv2.resize(self.tmpl, (w, h), interpolation=cv2.INTER_NEAREST)
            m = cv2.resize(self.tmask, (w, h), interpolation=cv2.INTER_NEAREST)
            if min(w, h) > 40:  # ignore blurry outline pixels when large
                m = cv2.erode(m, np.ones((3, 3), np.uint8))
            m3 = cv2.merge([m, m, m]).astype(np.float32)
            self._scaled_cache[key] = (t.astype(np.float32), m3, float(m.sum()))
        return self._scaled_cache[key]

    def crop_roi(self, frame):
        x, y, w, h = self.cal["roi"]
        H, W = frame.shape[:2]
        x, y = max(0, x), max(0, y)
        return frame[y:min(H, y + h), x:min(W, x + w)]

    def template_score(self, roi):
        """Best colour similarity (0..1) of the shiny PNG anywhere in the ROI."""
        # Work at a reduced size for speed: template scale ~2x of GBA pixels.
        f = min(1.0, 2.0 / self.scale)
        small = cv2.resize(roi, None, fx=f, fy=f, interpolation=cv2.INTER_AREA) if f < 1 else roi
        small = small.astype(np.float32)
        best, best_loc = 0.0, None
        for k in np.arange(0.85, 1.151, 0.05):
            s = self.scale * f * k
            t, m3, n = self._scaled(s)
            if t.shape[0] > small.shape[0] or t.shape[1] > small.shape[1] or n < 10:
                continue
            res = cv2.matchTemplate(small, t, cv2.TM_SQDIFF, mask=m3)
            res = np.nan_to_num(res, nan=1e12, posinf=1e12)
            mn, _, loc, _ = cv2.minMaxLoc(res)
            rms = np.sqrt(max(mn, 0) / (n * 3))
            sim = 1.0 - rms / 255.0
            if sim > best:
                best = sim
                best_loc = (int(loc[0] / f), int(loc[1] / f), int(t.shape[1] / f), int(t.shape[0] / f))
        return best, best_loc

    def yellow_fraction(self, roi):
        hsv = cv2.cvtColor(roi, cv2.COLOR_BGR2HSV)
        lo = np.array([self.hue_lo, self.cfg.get("yellow_min_sat", 110), self.cfg.get("yellow_min_val", 110)])
        hi = np.array([self.hue_hi, 255, 255])
        n = int(cv2.countNonZero(cv2.inRange(hsv, lo, hi)))
        expected = self.opaque_px * self.scale * self.scale
        return n / max(expected, 1.0)

    def normal_score(self, roi):
        ref = self.normal_ref
        if ref is None:
            return -1.0
        if ref.shape[0] > roi.shape[0] or ref.shape[1] > roi.shape[1]:
            return -1.0
        f = min(1.0, 120.0 / max(ref.shape[:2]))  # speed: compare at small size
        r = cv2.resize(ref, None, fx=f, fy=f, interpolation=cv2.INTER_AREA)
        s = cv2.resize(roi, None, fx=f, fy=f, interpolation=cv2.INTER_AREA)
        res = cv2.matchTemplate(s, r, cv2.TM_CCOEFF_NORMED)
        return float(res.max())

    # ------------------------------------------------------------------ main
    def analyze(self, frame) -> Result:
        roi = self.crop_roi(frame)
        shiny, loc = self.template_score(roi)
        # Count yellow only where Deoxys stands (calibrated tight box, +15%),
        # so yellow/sandy backgrounds can't trigger it.
        nb = self.cal.get("normal_box")
        if nb:
            x, y, w, h = nb
            px, py = int(w * 0.15), int(h * 0.15)
            H, W = frame.shape[:2]
            body = frame[max(0, y - py):min(H, y + h + py), max(0, x - px):min(W, x + w + px)]
            yellow = self.yellow_fraction(body)
        else:
            yellow = self.yellow_fraction(roi)
        normal = self.normal_score(roi)

        base_yellow = float(self.cal.get("normal_yellow_frac", 0.0))
        yellow_hit = yellow >= max(self.cfg.get("yellow_min_frac", 0.15),
                                   base_yellow + self.cfg.get("yellow_margin", 0.10))
        template_hit = shiny >= self.cfg.get("shiny_threshold", 0.90)
        is_shiny = template_hit or yellow_hit
        is_normal = (not is_shiny) and normal >= self.cfg.get("normal_threshold", 0.80)
        return Result(float(shiny), float(yellow), float(normal), bool(is_shiny), bool(is_normal),
                      bool(is_shiny or is_normal), loc)

    def annotate(self, frame, r: Result):
        """Draw the ROI and scores onto a copy of the frame (for previews/screenshots)."""
        out = frame.copy()
        x, y, w, h = self.cal["roi"]
        color = (0, 255, 255) if r.is_shiny else (0, 200, 0) if r.is_normal else (200, 200, 200)
        cv2.rectangle(out, (x, y), (x + w, y + h), color, 2)
        if r.location:
            lx, ly, lw, lh = r.location
            cv2.rectangle(out, (x + lx, y + ly), (x + lx + lw, y + ly + lh), (255, 0, 255), 1)
        cv2.putText(out, str(r), (10, 30), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 0, 0), 4)
        cv2.putText(out, str(r), (10, 30), cv2.FONT_HERSHEY_SIMPLEX, 0.6, color, 1)
        return out
