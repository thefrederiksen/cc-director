"""Image manipulation: resize, convert, info."""

from pathlib import Path
from typing import Optional

from PIL import Image, ImageEnhance


def image_info(image_path: Path) -> dict:
    """Get image metadata."""
    image_path = Path(image_path)
    if not image_path.exists():
        raise FileNotFoundError(f"Image not found: {image_path}")

    with Image.open(image_path) as img:
        return {
            "path": str(image_path),
            "width": img.size[0],
            "height": img.size[1],
            "format": img.format,
            "mode": img.mode,
            "size_bytes": image_path.stat().st_size,
        }


def resize(
    input_path: Path,
    output_path: Path,
    width: Optional[int] = None,
    height: Optional[int] = None,
    maintain_aspect: bool = True,
    quality: int = 95,
) -> Path:
    """Resize image with high quality."""
    input_path = Path(input_path)
    output_path = Path(output_path)

    if not input_path.exists():
        raise FileNotFoundError(f"Image not found: {input_path}")
    if width is None and height is None:
        raise ValueError("Must specify width or height")

    with Image.open(input_path) as img:
        orig_w, orig_h = img.size

        if maintain_aspect:
            if width and height:
                ratio = min(width / orig_w, height / orig_h)
                target = (int(orig_w * ratio), int(orig_h * ratio))
            elif width:
                ratio = width / orig_w
                target = (width, int(orig_h * ratio))
            else:
                ratio = height / orig_h
                target = (int(orig_w * ratio), height)
        else:
            target = (width or orig_w, height or orig_h)

        is_upscaling = target[0] > orig_w or target[1] > orig_h
        resized = img.resize(target, Image.Resampling.LANCZOS)

        if is_upscaling:
            resized = ImageEnhance.Sharpness(resized).enhance(1.1)

        resized = _prepare_for_save(resized, output_path)
        output_path.parent.mkdir(parents=True, exist_ok=True)
        _save_image(resized, output_path, quality)

    return output_path


def convert(input_path: Path, output_path: Path, quality: int = 95) -> Path:
    """Convert image format."""
    input_path = Path(input_path)
    output_path = Path(output_path)

    if not input_path.exists():
        raise FileNotFoundError(f"Image not found: {input_path}")

    with Image.open(input_path) as img:
        converted = _prepare_for_save(img.copy(), output_path)
        output_path.parent.mkdir(parents=True, exist_ok=True)
        _save_image(converted, output_path, quality)

    return output_path


def _prepare_for_save(img: Image.Image, output_path: Path) -> Image.Image:
    """Prepare image for saving (handle transparency for JPEG)."""
    fmt = output_path.suffix.upper().lstrip(".")
    if fmt == "JPG":
        fmt = "JPEG"

    if fmt == "JPEG" and img.mode in ("RGBA", "LA", "P"):
        rgb = Image.new("RGB", img.size, (255, 255, 255))
        if img.mode == "RGBA":
            rgb.paste(img, mask=img.split()[3])
        elif img.mode == "P":
            img = img.convert("RGBA")
            rgb.paste(img, mask=img.split()[3])
        else:
            rgb.paste(img)
        return rgb
    return img


def _save_image(img: Image.Image, path: Path, quality: int) -> None:
    """Save image with format-appropriate settings."""
    fmt = path.suffix.upper().lstrip(".")
    if fmt == "JPG":
        fmt = "JPEG"

    if fmt == "JPEG":
        img.save(path, "JPEG", quality=quality, optimize=True)
    elif fmt == "PNG":
        img.save(path, "PNG", optimize=True)
    elif fmt == "WEBP":
        img.save(path, "WEBP", quality=quality)
    else:
        img.save(path, fmt)
