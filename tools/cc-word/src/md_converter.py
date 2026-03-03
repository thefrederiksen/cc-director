"""Convert Word (DOCX) files to Markdown with image extraction."""

import re
from pathlib import Path

import mammoth
from markdownify import markdownify

try:
    from cc_shared.image_extractor import ExtractedImage, save_extracted_images
except ImportError:
    import sys
    import os
    if hasattr(sys, '_MEIPASS'):
        sys.path.insert(0, os.path.join(sys._MEIPASS, 'cc_shared'))
    else:
        sys.path.insert(0, str(Path(__file__).resolve().parent.parent.parent / "cc_shared"))
    from image_extractor import ExtractedImage, save_extracted_images


def convert_docx_to_markdown(
    input_path: Path,
    output_path: Path,
) -> str:
    """Convert a Word document to Markdown, extracting embedded images.

    Uses mammoth to convert DOCX -> HTML (with image callbacks), then
    markdownify to convert HTML -> Markdown.

    Args:
        input_path: Path to the ``.docx`` file.
        output_path: Path to the output ``.md`` file (used to derive
            the image directory).

    Returns:
        Markdown string with image references pointing to extracted files.
    """
    images: list[ExtractedImage] = []
    image_counter = 0

    def _convert_image(image):
        """Mammoth image conversion callback."""
        nonlocal image_counter
        image_counter += 1

        with image.open() as img_stream:
            data = img_stream.read()

        content_type = image.content_type or "image/png"
        ext = content_type.split("/")[-1]
        if ext == "jpeg":
            ext = "jpg"
        if ext == "svg+xml":
            ext = "svg"

        alt = getattr(image, "alt_text", "") or ""
        name = f"image_{image_counter:03d}.{ext}"

        images.append(ExtractedImage(
            data=data,
            extension=ext,
            original_name=name,
            alt_text=alt,
        ))

        # Return a placeholder src that we'll replace after saving
        return {"src": f"__docx_image_{image_counter}__"}

    # Convert DOCX to HTML via mammoth
    with open(input_path, "rb") as f:
        result = mammoth.convert_to_html(
            f,
            convert_image=mammoth.images.img_element(_convert_image),
        )

    html = result.value

    # Save extracted images and build replacement map
    if images:
        path_map = save_extracted_images(images, output_path)

        # Replace placeholders with actual paths
        for idx, img in enumerate(images, start=1):
            placeholder = f"__docx_image_{idx}__"
            rel_path = path_map.get(img.original_name, placeholder)
            html = html.replace(placeholder, rel_path)

    # Convert HTML to Markdown
    markdown = markdownify(
        html,
        heading_style="ATX",
        bullets="-",
    )

    # Clean up excessive blank lines
    markdown = re.sub(r"\n{3,}", "\n\n", markdown)

    return markdown.strip() + "\n"
