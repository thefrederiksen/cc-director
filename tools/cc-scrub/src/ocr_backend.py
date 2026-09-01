#!/usr/bin/env python
"""The one OCR seam for cc-scrub.

Everything above this file is platform independent. Everything below it is
the operating system's own text recognizer. There is exactly one way in:

    backend = get_backend()
    words = backend.recognize(pil_image, lang)

`recognize` returns a flat list of word dictionaries, in reading order:

    {"text": str, "x": float, "y": float, "w": float, "h": float, "line": int}

`x`, `y`, `w`, `h` are pixels measured in the coordinate space of the image
that was handed in, with the origin at the top left.

`line` is the index of the recognized line the word came from. Words that
share a `line` value are on one line, and the matcher above joins adjacent
words within a line so a term split across two OCR words still matches. It
is not optional and it is not cosmetic: a backend that gave every word a
different `line` value would silently switch the joining off and start
missing terms. Every recognizer worth using groups its output into lines
already, so both backends can fill this in honestly.

A backend also declares:

    name                  a human name, used in error messages
    max_image_dimension   the longest side the engine accepts, in pixels

No backend may fall back to another, and no backend may substitute a
different engine for the one it names. If the recognizer for this operating
system is not usable, the backend raises ScrubError saying what is missing
and how to fix it, and the run stops.
"""

import sys


class ScrubError(Exception):
    """A condition that must stop the run loudly.

    It lives here because the OCR seam is the lowest layer of the tool and
    raises it too; everything above imports it from this module.
    """


class OcrBackend(object):
    """The interface every platform backend implements."""

    name = "unnamed backend"
    max_image_dimension = 10000

    def recognize(self, image, lang):
        """OCR a PIL image; return word dictionaries as described above."""
        raise NotImplementedError


class WindowsOcrBackend(OcrBackend):
    """The OCR engine built into Windows, reached through the winocr wheel.

    winocr is a pip wheel that wraps the Windows OCR engine through WinRT.
    No external executable is installed, required or looked for - in
    particular this tool never calls tesseract, even on a machine where one
    happens to be present. It launches no subprocess at all.
    """

    name = "Windows OCR (winocr / WinRT)"

    def __init__(self):
        try:
            import winocr
            from winocr import OcrEngine
        except ImportError as exc:
            raise ScrubError(
                "winocr is not installed (%s). Fix: python -m pip install "
                "winocr. winocr is a pip wheel that wraps the OCR engine built "
                "into Windows; there is no installer and no .exe to find."
                % exc)
        self._winocr = winocr
        self._engine = OcrEngine
        self.max_image_dimension = OcrEngine.max_image_dimension

    def _languages(self):
        return ", ".join(language.language_tag
                         for language in
                         self._engine.available_recognizer_languages)

    def recognize(self, image, lang):
        try:
            result = self._winocr.recognize_pil_sync(image, lang)
        except Exception as exc:
            raise ScrubError(
                "Windows OCR failed for language '%s': %s. Installed "
                "recognizer languages: %s" % (lang, exc, self._languages()))

        words = []
        for index, line in enumerate(result["lines"]):
            for word in line["words"]:
                rect = word["bounding_rect"]
                words.append({
                    "text": word["text"],
                    "x": float(rect["x"]),
                    "y": float(rect["y"]),
                    "w": float(rect["width"]),
                    "h": float(rect["height"]),
                    "line": index,
                })
        return words


class MacVisionBackend(OcrBackend):
    """Apple's Vision text recognizer. NOT IMPLEMENTED YET.

    The macOS backend is deliberately present and deliberately loud. It is
    not a stub that returns nothing - a backend that returned an empty word
    list would look exactly like a clean screenshot, which is the one
    failure this tool must never produce.
    """

    name = "macOS Vision text recognizer"

    def __init__(self):
        raise ScrubError(
            "the macOS OCR backend is not implemented yet. cc-scrub needs a "
            "MacVisionBackend that recognises text with Apple's Vision "
            "framework (VNRecognizeTextRequest, accurate recognition level) "
            "through a pip-installable binding, and returns one dictionary "
            "per word with keys text, x, y, w, h in top-left pixel "
            "coordinates of the image it was handed, plus 'line' set to the "
            "index of the recognised line the word came from. It must also "
            "set max_image_dimension to the engine's real limit. Until that "
            "exists, run cc-scrub on Windows.")


def get_backend():
    """Return the OCR backend for this operating system, or fail loudly.

    Platform detection only. There is no probing, no trying one engine and
    then another, and no silent substitution: an operating system whose
    recognizer this tool cannot drive is an error, not a degraded mode.
    """
    if sys.platform.startswith("win"):
        return WindowsOcrBackend()
    if sys.platform == "darwin":
        return MacVisionBackend()
    raise ScrubError(
        "cc-scrub has no OCR backend for platform '%s'. The recognizer is the "
        "operating system's own; the supported systems are Windows and "
        "macOS. There is no portable fallback engine and none will be added."
        % sys.platform)
