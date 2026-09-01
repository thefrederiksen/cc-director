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


def _words_with_utf16_ranges(text):
    """Split text on whitespace; return (word, start, length) triples with
    start and length measured in UTF-16 CODE UNITS, not Python code points.

    The ranges are what let the backend ask the recognizer for the exact
    rectangle of each word inside its line, and the recognizer's strings
    are Foundation NSStrings, which NSRange indexes in UTF-16 code units.
    A character outside the Basic Multilingual Plane - an emoji in a
    recognized line, say - occupies ONE Python code point but TWO UTF-16
    code units, so a Python enumerate() offset drifts by one for every
    such character earlier in the line and the rectangle request would
    land on the wrong characters. Counting code units here keeps the two
    sides of the bridge indexing the same string the same way.
    """
    ranges = []
    word_chars = []
    word_start = 0
    position = 0
    for ch in text:
        if ch.isspace():
            if word_chars:
                ranges.append(("".join(word_chars), word_start,
                               position - word_start))
                word_chars = []
        else:
            if not word_chars:
                word_start = position
            word_chars.append(ch)
        position += 2 if ord(ch) > 0xFFFF else 1
    if word_chars:
        ranges.append(("".join(word_chars), word_start, position - word_start))
    return ranges


class MacVisionBackend(OcrBackend):
    """The text recognizer built into macOS, reached through pyobjc wheels.

    pyobjc-framework-Vision is a pip wheel that binds the Vision framework
    the operating system already ships (VNRecognizeTextRequest, accurate
    recognition level). No external executable is installed, required or
    looked for - in particular this tool never calls tesseract, even on a
    machine where one happens to be present. It launches no subprocess at
    all: the PIL image is handed to the recognizer as an in-memory CGImage,
    never through a temporary file.

    Vision reports text line by line, with every rectangle normalized to
    0..1 and measured from the BOTTOM-left corner. This backend converts to
    the seam's top-left pixel coordinates, asks the recognizer itself for
    each word's rectangle within its line (boundingBoxForRange), and stamps
    all words of one observation with that observation's index as 'line',
    which is what keeps the matcher's adjacent-word joining alive.
    """

    name = "macOS Vision text recognizer (pyobjc)"

    def __init__(self):
        try:
            import Vision
            import Quartz
            from Foundation import NSData
        except ImportError as exc:
            raise ScrubError(
                "the Vision binding is not installed (%s). Fix: python -m "
                "pip install pyobjc-framework-Vision (it pulls in Quartz and "
                "the Foundation binding with it). These are pip wheels that "
                "bind the text recognizer built into macOS; there is no "
                "installer and no external engine." % exc)
        self._vision = Vision
        self._quartz = Quartz
        self._nsdata = NSData
        # Vision publishes no numeric input limit of its own. Its
        # recognizer runs on the graphics device, and the largest texture
        # side every Mac that can run this framework accepts is 16384
        # pixels, so that is the honest ceiling on what can be handed in
        # without the framework resampling it behind our back.
        self.max_image_dimension = 16384

    def _cg_image(self, image):
        """Build an in-memory CGImage from a PIL image. No temporary file."""
        rgb = image.convert("RGB")
        width, height = rgb.size
        raw = rgb.tobytes()
        data = self._nsdata.dataWithBytes_length_(raw, len(raw))
        provider = self._quartz.CGDataProviderCreateWithCFData(data)
        cg_image = self._quartz.CGImageCreate(
            width, height, 8, 24, width * 3,
            self._quartz.CGColorSpaceCreateDeviceRGB(),
            self._quartz.kCGImageAlphaNone,
            provider, None, False,
            self._quartz.kCGRenderingIntentDefault)
        if cg_image is None:
            raise ScrubError(
                "could not build a CGImage from the %dx%d input" % (width, height))
        return cg_image

    def _resolve_language(self, request, lang):
        """Turn a tag like 'en' into the recognizer's own tags, or fail.

        This is tag resolution, not a fallback: 'en' names the language the
        recognizer spells 'en-US', and a tag that names no supported
        language at all stops the run with the full supported list.
        """
        supported, error = request.supportedRecognitionLanguagesAndReturnError_(None)
        if supported is None:
            raise ScrubError(
                "Vision would not report its supported recognition "
                "languages: %s" % error)
        tags = [str(tag) for tag in supported]
        matches = [tag for tag in tags
                   if tag == lang or tag.split("-")[0] == lang]
        if not matches:
            raise ScrubError(
                "Vision does not support recognition language '%s'. "
                "Supported: %s" % (lang, ", ".join(tags)))
        return matches

    def recognize(self, image, lang):
        # The one exception boundary of this backend, mirroring the Windows
        # one. The bridge underneath can raise its own exceptions - from
        # the Objective-C side, from the binding, from Pillow - and every
        # one of them is a broken instrument, not a crash: it must surface
        # as the documented exit 2, not as a raw traceback. The deliberate
        # ScrubError messages raised below pass through unchanged.
        try:
            return self._recognize(image, lang)
        except ScrubError:
            raise
        except Exception as exc:
            raise ScrubError(
                "macOS Vision text recognition failed unexpectedly for "
                "language '%s': %s: %s" % (lang, type(exc).__name__, exc))

    def _recognize(self, image, lang):
        width, height = image.size
        vision = self._vision

        request = vision.VNRecognizeTextRequest.alloc().init()
        request.setRecognitionLevel_(
            vision.VNRequestTextRecognitionLevelAccurate)
        # Language correction rewrites what was read toward dictionary
        # words. The strings this tool hunts are exactly the ones a
        # dictionary does not hold - addresses, host names, repository
        # paths - so the raw read is the honest one, and glyph folding
        # above this seam is the designed answer to misread glyphs.
        request.setUsesLanguageCorrection_(False)
        request.setRecognitionLanguages_(self._resolve_language(request, lang))

        handler = vision.VNImageRequestHandler.alloc().initWithCGImage_options_(
            self._cg_image(image), None)
        success, error = handler.performRequests_error_([request], None)
        if not success:
            raise ScrubError(
                "Vision text recognition failed for language '%s': %s"
                % (lang, error))
        observations = request.results()
        if observations is None:
            raise ScrubError(
                "Vision reported success for language '%s' but returned no "
                "results object. That is a broken read." % lang)

        words = []
        for index, observation in enumerate(observations):
            candidates = observation.topCandidates_(1)
            if not candidates:
                raise ScrubError(
                    "Vision observation %d carries no text candidate. That "
                    "is a broken read." % index)
            candidate = candidates[0]
            text = str(candidate.string())
            for word_text, start, length in _words_with_utf16_ranges(text):
                box_observation, box_error = candidate.boundingBoxForRange_error_(
                    (start, length), None)
                if box_observation is None:
                    raise ScrubError(
                        "Vision would not give a rectangle for word '%s' of "
                        "line '%s': %s" % (word_text, text, box_error))
                box = box_observation.boundingBox()
                words.append({
                    "text": word_text,
                    "x": float(box.origin.x * width),
                    "y": float((1.0 - box.origin.y - box.size.height) * height),
                    "w": float(box.size.width * width),
                    "h": float(box.size.height * height),
                    "line": index,
                })
        return words


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
