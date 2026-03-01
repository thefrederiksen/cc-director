"""Standalone pixel analysis detector for UI elements.

Extracts the 3 non-OCR pixel strategies from the benchmark (color-segment,
button-detect, symbol-detect) into a CLI tool the C# detection pipeline can
call via subprocess.

Usage:
    python tools/pixel_detect.py <screenshot.png>
    -> stdout: {"elements": [...], "elapsed_ms": 150}
"""

import json
import sys
import time

try:
    from PIL import Image, ImageFilter
    HAS_PIL = True
except ImportError:
    HAS_PIL = False

try:
    import numpy as np
    from scipy.ndimage import label as ndimage_label, find_objects
    HAS_SCIPY = True
except ImportError:
    HAS_SCIPY = False


# ── Color segmentation helpers ─────────────────────────────────────────


def _find_color_regions(pixels, target_rgb, tolerance=5,
                        min_w=5, min_h=5, max_w=2000, max_h=2000):
    """Find connected regions of pixels matching target_rgb using scipy."""
    target = np.array(target_rgb, dtype=np.int16)
    mask = np.all(np.abs(pixels.astype(np.int16) - target) <= tolerance, axis=2)

    labeled, num = ndimage_label(mask)
    slices = find_objects(labeled)
    boxes = []
    for s in slices:
        if s is None:
            continue
        y_slice, x_slice = s
        x1, x2 = x_slice.start, x_slice.stop
        y1, y2 = y_slice.start, y_slice.stop
        w, h = x2 - x1, y2 - y1
        if min_w <= w <= max_w and min_h <= h <= max_h:
            boxes.append((x1, y1, x2, y2))
    return boxes


def _detect_window_controls_from_close_button(close_boxes):
    """Infer minimize/maximize button locations from close button position."""
    extra = []
    for cx1, cy1, cx2, cy2 in close_boxes:
        btn_w = cx2 - cx1
        min_box = (cx1 - 2 * btn_w, cy1, cx1 - btn_w, cy2)
        max_box = (cx1 - btn_w, cy1, cx1, cy2)
        if min_box[0] >= 0:
            extra.append(min_box)
            extra.append(max_box)
    return extra


def _run_color_segment(image_path):
    """Detect UI elements by color using known palette + scipy connected components."""
    img = Image.open(image_path).convert("RGB")
    pixels = np.array(img)
    elements = []

    # 1. Standard buttons: (225, 225, 225) fill
    for box in _find_color_regions(pixels, (225, 225, 225), tolerance=3,
                                   min_w=25, min_h=15, max_w=300, max_h=80):
        elements.append({"type": "button", "bbox": list(box)})

    # 2. Close button: (232, 17, 35) red fill
    close_boxes = _find_color_regions(pixels, (232, 17, 35), tolerance=5,
                                      min_w=15, min_h=15, max_w=80, max_h=50)
    for box in close_boxes:
        elements.append({"type": "button", "bbox": list(box)})

    # 3. Infer minimize/maximize from close button position
    for box in _detect_window_controls_from_close_button(close_boxes):
        elements.append({"type": "button", "bbox": list(box)})

    # 4. Icon placeholders: (0, 120, 215) blue
    blue_regions = _find_color_regions(pixels, (0, 120, 215), tolerance=3,
                                       min_w=8, min_h=8, max_w=2000, max_h=2000)
    for box in blue_regions:
        w = box[2] - box[0]
        h = box[3] - box[1]
        if w <= 24 and h <= 24:
            elements.append({"type": "icon", "bbox": list(box)})
        elif h <= 24 and w > 50:
            elements.append({"type": "slider", "bbox": list(box)})
        elif w <= 200 and h <= 80 and w * h < 15000:
            elements.append({"type": "button", "bbox": list(box)})

    # 5. Scrollbar thumb: (205, 205, 205)
    for box in _find_color_regions(pixels, (205, 205, 205), tolerance=3,
                                   min_w=5, min_h=15, max_w=30, max_h=2000):
        elements.append({"type": "scrollbar", "bbox": list(box)})

    # 6. Calculator digit buttons: (59, 59, 59)
    for box in _find_color_regions(pixels, (59, 59, 59), tolerance=3,
                                   min_w=30, min_h=25, max_w=120, max_h=70):
        elements.append({"type": "button", "bbox": list(box)})

    # 7. Calculator operator/clear buttons: (50, 50, 50)
    for box in _find_color_regions(pixels, (50, 50, 50), tolerance=2,
                                   min_w=30, min_h=25, max_w=120, max_h=70):
        elements.append({"type": "button", "bbox": list(box)})

    # 8. Inactive tabs: (218, 218, 218)
    for box in _find_color_regions(pixels, (218, 218, 218), tolerance=3,
                                   min_w=50, min_h=15, max_w=200, max_h=50):
        elements.append({"type": "tab", "bbox": list(box)})

    # 9. Text field borders: (122, 122, 122)
    for box in _find_color_regions(pixels, (122, 122, 122), tolerance=3,
                                   min_w=50, min_h=20, max_w=500, max_h=40):
        elements.append({"type": "textfield", "bbox": list(box)})

    # 10. Checkbox/radio borders: (100, 100, 100)
    for box in _find_color_regions(pixels, (100, 100, 100), tolerance=3,
                                   min_w=10, min_h=10, max_w=25, max_h=25):
        elements.append({"type": "checkbox", "bbox": list(box)})

    # 11. Browser dark inactive tabs: (60, 60, 61)
    for box in _find_color_regions(pixels, (60, 60, 61), tolerance=3,
                                   min_w=50, min_h=20, max_w=250, max_h=50):
        elements.append({"type": "tab", "bbox": list(box)})

    # 12. Toggle switch track off: (180, 180, 180)
    for box in _find_color_regions(pixels, (180, 180, 180), tolerance=3,
                                   min_w=20, min_h=10, max_w=60, max_h=30):
        elements.append({"type": "toggle", "bbox": list(box)})

    # 14. Address bar / search bar: (243, 243, 243)
    for box in _find_color_regions(pixels, (243, 243, 243), tolerance=2,
                                   min_w=100, min_h=20, max_w=2000, max_h=40):
        w = box[2] - box[0]
        h = box[3] - box[1]
        if w > h * 3:
            elements.append({"type": "textfield", "bbox": list(box)})

    # Assign centers
    for e in elements:
        x1, y1, x2, y2 = e["bbox"]
        e["center"] = [(x1 + x2) // 2, (y1 + y2) // 2]

    return elements


def _run_button_detector(image_path):
    """Detect UI buttons using edge analysis with Pillow."""
    img = Image.open(image_path).convert("RGB")
    w, h = img.size
    pixels = img.load()

    edges_img = img.filter(ImageFilter.FIND_EDGES)
    edges = edges_img.convert("L")
    edge_pixels = edges.load()

    elements = []
    threshold = 30
    min_btn_w, max_btn_w = 25, 300
    min_btn_h, max_btn_h = 20, 80
    step_x = 5
    step_y = 5

    found_rects = []

    for scan_y in range(0, h - min_btn_h, step_y):
        for scan_x in range(0, w - min_btn_w, step_x):
            if edge_pixels[scan_x, scan_y] < threshold:
                continue

            for bw in range(min_btn_w, min(max_btn_w, w - scan_x), 5):
                if edge_pixels[scan_x + bw, scan_y] < threshold:
                    continue

                for bh in range(min_btn_h, min(max_btn_h, h - scan_y), 5):
                    if (edge_pixels[scan_x, scan_y + bh] < threshold or
                            edge_pixels[scan_x + bw, scan_y + bh] < threshold):
                        continue

                    cx = scan_x + bw // 2
                    cy = scan_y + bh // 2
                    center_color = pixels[cx, cy]

                    uniform = True
                    for dx in [-bw // 4, 0, bw // 4]:
                        for dy in [-bh // 4, 0, bh // 4]:
                            px, py = cx + dx, cy + dy
                            if 0 <= px < w and 0 <= py < h:
                                pc = pixels[px, py]
                                diff = sum(abs(a - b) for a, b in zip(center_color, pc))
                                if diff > 60:
                                    uniform = False
                                    break
                        if not uniform:
                            break

                    if uniform:
                        found_rects.append((scan_x, scan_y, scan_x + bw, scan_y + bh))
                        break
                break

    if not found_rects:
        return []

    # Deduplicate: keep largest, skip rects whose center is inside a kept rect
    kept = []
    for rect in sorted(found_rects, key=lambda r: (r[2] - r[0]) * (r[3] - r[1]), reverse=True):
        rx1, ry1, rx2, ry2 = rect
        rcx, rcy = (rx1 + rx2) // 2, (ry1 + ry2) // 2
        overlap = False
        for kx1, ky1, kx2, ky2 in kept:
            if kx1 <= rcx <= kx2 and ky1 <= rcy <= ky2:
                overlap = True
                break
        if not overlap:
            kept.append(rect)

    for rect in kept:
        x1, y1, x2, y2 = rect
        elements.append({
            "type": "button",
            "bbox": [x1, y1, x2, y2],
            "center": [(x1 + x2) // 2, (y1 + y2) // 2],
        })

    return elements


def _run_symbol_detect(image_path):
    """Detect isolated symbols/icons using local contrast analysis."""
    from scipy.ndimage import uniform_filter, binary_dilation

    img = Image.open(image_path).convert("L")
    arr = np.array(img, dtype=np.float32)

    local_mean = uniform_filter(arr, size=21)
    local_dev = np.abs(arr - local_mean)
    content_mask = local_dev > 25

    all_boxes = []
    for dilation in [0, 2, 4]:
        if dilation == 0:
            mask = content_mask
        else:
            mask = binary_dilation(content_mask, iterations=dilation)
        labeled, num = ndimage_label(mask)
        slices = find_objects(labeled)
        min_size = 6 if dilation == 0 else 8
        for s in slices:
            if s is None:
                continue
            y_slice, x_slice = s
            all_boxes.append((x_slice.start, y_slice.start, x_slice.stop, y_slice.stop, min_size))

    seen = set()
    elements = []
    for box in all_boxes:
        x1, y1, x2, y2, min_size = box
        w, h = x2 - x1, y2 - y1
        if min_size <= w <= 50 and min_size <= h <= 50:
            cx, cy = (x1 + x2) // 2, (y1 + y2) // 2
            key = (round(cx / 10), round(cy / 10))
            if key not in seen:
                seen.add(key)
                elements.append({
                    "type": "button",
                    "bbox": [x1, y1, x2, y2],
                    "center": [cx, cy],
                })

    return elements


def run_pixel_analysis(image_path):
    """Run all 3 pixel strategies and merge with 10px grid dedup."""
    all_elements = []
    existing_centers = set()

    def _add_elements(elems):
        for e in elems:
            c = e.get("center", [0, 0])
            key = (round(c[0] / 10), round(c[1] / 10))
            if key not in existing_centers:
                existing_centers.add(key)
                all_elements.append(e)

    # Priority: color-segment first (most precise), then buttons, then symbols
    _add_elements(_run_color_segment(image_path))
    _add_elements(_run_button_detector(image_path))
    _add_elements(_run_symbol_detect(image_path))

    # Assign IDs and ensure all fields
    for i, e in enumerate(all_elements, 1):
        e["id"] = i
        x1, y1, x2, y2 = e["bbox"]
        e.setdefault("center", [(x1 + x2) // 2, (y1 + y2) // 2])
        e.setdefault("confidence", 0.8)
        e.setdefault("label", "")
        e.setdefault("interactable", True)

    return all_elements


def main():
    if len(sys.argv) < 2:
        print(json.dumps({"error": "Usage: pixel_detect.py <screenshot.png>"}),
              file=sys.stderr)
        sys.exit(1)

    image_path = sys.argv[1]

    if not HAS_PIL:
        print(json.dumps({"error": "Pillow not installed"}), file=sys.stderr)
        sys.exit(1)

    if not HAS_SCIPY:
        print(json.dumps({"error": "numpy/scipy not installed"}), file=sys.stderr)
        sys.exit(1)

    t0 = time.time()
    try:
        elements = run_pixel_analysis(image_path)
        elapsed_ms = int((time.time() - t0) * 1000)
        result = {"elements": elements, "elapsed_ms": elapsed_ms}
        print(json.dumps(result))
    except Exception as e:
        print(json.dumps({"error": str(e), "elements": []}), file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
