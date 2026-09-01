"""Tests for cc-scrub.

Two kinds of test, on purpose.

The INTEGRATION tests run the tool end to end over the synthetic samples
that gen_samples.py draws, against the REAL recognizer for this platform.
They are what proves the tool works with the engine it actually depends on,
and they skip by platform name where no backend exists yet - never by
probing whether OCR happens to work, so a broken install on a supported
platform fails them instead of quietly passing.

The CONTROLLED tests drive a ScriptedBackend: a deliberate test double
implementing the OCR seam contract, unreachable from get_backend() and never
shipped. It exists because parts of the tool cannot be reached with a real
engine at all - no screenshot makes a recognizer read zero words from a file
it has just read words from, and none reliably leaves a redacted term
readable - and those are precisely the arms that must never rot. It also
lets the coordinate mapping and the word joining be asserted as exact
numbers rather than inferred from a hit count.

Neither kind substitutes for the other, and the double never stands in for
the engine in a test about the engine.

Run from this tool's directory:

    python -m pytest tests/ -q
"""

import os
import re
import sys
from pathlib import Path

import pytest
from PIL import Image

sys.path.insert(0, str(Path(__file__).parent.parent))

import gen_samples
from src import cli
from src.ocr_backend import ScrubError, _words_with_utf16_ranges


# The platforms that have a working OCR backend today. It is a list of
# platforms and NOT a probe of whether OCR happens to work: on a supported
# platform a missing or broken engine must fail these tests, not quietly
# skip them.
SUPPORTED_PLATFORMS = ("win32", "darwin")

requires_ocr = pytest.mark.skipif(
    sys.platform not in SUPPORTED_PLATFORMS,
    reason="cc-scrub has no OCR backend for %s yet" % sys.platform)


TERMS = [gen_samples.TERM_EMAIL, gen_samples.TERM_REPO, gen_samples.TERM_HOST]


@pytest.fixture(scope="session")
def samples(tmp_path_factory):
    """Draw the four samples once and hand back the directory."""
    out = tmp_path_factory.mktemp("samples")
    gen_samples.generate(str(out))
    return out


@pytest.fixture(scope="session")
def terms_file(tmp_path_factory):
    path = tmp_path_factory.mktemp("terms") / "terms.txt"
    path.write_text("# test denylist\n" + "\n".join(TERMS) + "\n",
                    encoding="utf-8")
    return path


def run(capsys, *argv):
    """Run the tool, return (exit code, everything it printed)."""
    code = cli.main([str(a) for a in argv])
    captured = capsys.readouterr()
    return code, captured.out + captured.err


def word(text, x, y, w, h, line=0):
    """One word in the shape the OCR seam contract requires."""
    return {"text": text, "x": float(x), "y": float(y),
            "w": float(w), "h": float(h), "line": line}


class ScriptedBackend(object):
    """A deliberate test double implementing the OCR seam contract exactly.

    This is test tooling, not a runtime fallback. It is never reachable from
    get_backend(), it never ships, and nothing in the tool can select it - a
    test must hand it in.

    It exists because the verify pass's failure arms cannot be reached with a
    real recognizer. There is no screenshot that makes an engine read zero
    words from a file it has just read words from, and none that reliably
    leaves a redacted term readable. Those two arms are exactly the ones that
    must never rot, so they get a backend whose every read is written down
    here in the test.
    """

    name = "scripted test backend"

    def __init__(self, reads, max_image_dimension=10000):
        self.reads = list(reads)
        self.max_image_dimension = max_image_dimension
        self.calls = []

    def recognize(self, image, lang):
        self.calls.append((image.size, lang))
        if not self.reads:
            raise AssertionError(
                "the scripted backend was asked for read %d but only %d were "
                "scripted" % (len(self.calls), len(self.calls) - 1))
        return self.reads.pop(0)


@pytest.fixture
def blank_image(tmp_path):
    """An input file for the scripted tests. Its pixels are never read."""
    path = tmp_path / "input.png"
    Image.new("RGB", (200, 100), (250, 250, 250)).save(path)
    return path


def run_scripted(capsys, monkeypatch, backend, *argv):
    """Run the tool against a scripted backend instead of the real engine."""
    monkeypatch.setattr(cli, "get_backend", lambda: backend)
    return run(capsys, *argv)


def temp_files_in(directory):
    return [name for name in os.listdir(str(directory)) if name.endswith(".tmp")]


# --------------------------------------------------------------- sample shape

def test_generator_writes_all_four_samples(samples):
    for name in gen_samples.SAMPLE_NAMES:
        assert (samples / name).is_file(), "%s was not generated" % name


# ------------------------------------------------------------- check-only pass

@requires_ocr
def test_check_only_finds_the_planted_email_with_coordinates(
        samples, terms_file, capsys):
    code, out = run(capsys, samples / "sample-normal.png",
                    "--check-only", "--terms-file", terms_file)
    assert code == 1
    assert "HITS: 1" in out
    assert "term='%s'" % gen_samples.TERM_EMAIL in out
    assert "rect=(x=" in out
    assert "CHECK-ONLY: 1 hit(s) present, image NOT modified." in out


@requires_ocr
def test_check_only_finds_small_grey_ui_text_only_after_upscaling(
        samples, terms_file, capsys):
    """The 10 px line is the reason multi-scale exists.

    The recognizer cannot resolve it at native resolution, so the hit must
    carry scales above 1. If this ever passes with 'scales=1' the sample has
    drifted and stopped testing what it is for.
    """
    code, out = run(capsys, samples / "sample-small-ui.png",
                    "--check-only", "--terms-file", terms_file)
    assert code == 1
    assert "term='%s'" % gen_samples.TERM_REPO in out
    scales = out.split("scales=")[1].split(" ")[0]
    assert scales != "1", "the small grey line resolved at scale 1: %s" % scales


@requires_ocr
def test_glyph_confusion_is_caught_by_folding_and_missed_without_it(
        samples, terms_file, capsys):
    """The host name must match with folding on, whatever the engine read.

    On Windows the recognizer returns the leading l-shaped glyph as a t, so
    the second half of this test demonstrates the miss: unfolded, the term
    is not found. Vision on macOS reads this sample cleanly at every scale,
    so the miss cannot be demonstrated there and that half is asserted on
    Windows only. The fold-versus-miss property itself is proven
    deterministically on every platform by the controlled test backend,
    which feeds the matcher a synthetic misread; this test is the live
    engine smoke pass on top of that.
    """
    folded_code, folded = run(capsys, samples / "sample-glyph.png",
                              "--check-only", "--terms-file", terms_file)
    assert folded_code == 1
    assert "term='%s'" % gen_samples.TERM_HOST in folded

    if sys.platform == "win32":
        exact_code, exact = run(capsys, samples / "sample-glyph.png",
                                "--check-only", "--no-fold",
                                "--terms-file", terms_file)
        assert exact_code == 0
        assert "HITS: 0" in exact


# ------------------------------------------------------------------ scrub pass

@requires_ocr
@pytest.mark.parametrize("name,term", [
    ("sample-normal.png", gen_samples.TERM_EMAIL),
    ("sample-small-ui.png", gen_samples.TERM_REPO),
    ("sample-glyph.png", gen_samples.TERM_HOST),
])
def test_scrub_redacts_and_the_verify_pass_proves_it(
        samples, terms_file, tmp_path, capsys, name, term):
    out_dir = tmp_path / name.replace(".png", "")
    out_dir.mkdir()
    code, out = run(capsys, samples / name, "-o", out_dir,
                    "--terms-file", terms_file)
    assert code == 0, out

    written = out_dir / name.replace(".png", "-scrubbed.png")
    assert written.is_file()
    assert written.read_bytes() != (samples / name).read_bytes()

    # The pass condition is a presence and is asserted as one: hits found,
    # regions redacted, and a word count read back out of the file on disk
    # that is checked as a NUMBER. Asserting that the string "read 0 words"
    # is absent would pass just as happily if the line were missing
    # altogether, which is the failure it was supposed to catch.
    assert "VERIFY PASSED: 1 hit(s) found, 1 region(s) redacted, verify OCR " \
           "read " in out
    assert "and 0 denylist hit(s) remain." in out
    match = re.search(r"verify OCR read (\d+) words in the output", out)
    assert match, out
    assert int(match.group(1)) > 0

    # Publishing is atomic: the candidate is gone, the output is there.
    assert temp_files_in(out_dir) == []


@requires_ocr
def test_the_scrubbed_output_is_clean_when_checked_again(
        samples, terms_file, tmp_path, capsys):
    out_dir = tmp_path / "recheck"
    out_dir.mkdir()
    code, _ = run(capsys, samples / "sample-normal.png", "-o", out_dir,
                  "--terms-file", terms_file)
    assert code == 0

    written = out_dir / "sample-normal-scrubbed.png"
    code, out = run(capsys, written, "--check-only", "--terms-file", terms_file)
    assert code == 0
    assert "HITS: 0" in out
    assert "CHECK-ONLY: no hits against 3 terms over " in out


@requires_ocr
def test_a_directory_run_skips_files_already_scrubbed(
        samples, terms_file, tmp_path, capsys):
    work = tmp_path / "dir-run"
    work.mkdir()
    for name in ("sample-normal.png", "sample-small-ui.png"):
        (work / name).write_bytes((samples / name).read_bytes())

    code, out = run(capsys, work, "--terms-file", terms_file)
    assert code == 0
    assert "images     : 2" in out
    first = (work / "sample-normal-scrubbed.png").read_bytes()

    # The second run still picks up exactly the two inputs - the outputs are
    # skipped by their suffix - but it now refuses to replace an output that
    # has already been verified.
    code, out = run(capsys, work, "--terms-file", terms_file)
    assert code == 2
    assert "already exists" in out
    assert (work / "sample-normal-scrubbed.png").read_bytes() == first

    # --force is how you say you meant it.
    code, out = run(capsys, work, "--force", "--terms-file", terms_file)
    assert code == 0
    assert "images     : 2" in out
    assert temp_files_in(work) == []


# -------------------------------------------------------- the broken instrument

@requires_ocr
def test_an_image_with_no_text_is_a_broken_read_not_a_clean_image(
        samples, terms_file, capsys):
    code, out = run(capsys, samples / "sample-notext.png",
                    "--check-only", "--terms-file", terms_file)
    assert code == 2
    assert "OCR read ZERO words" in out
    assert "Refusing to call this scrubbed." in out


# ------------------------------------------ the verify pass, both failure arms

HIT_WORDS = [word("repo", 10, 20, 30, 10, line=0),
             word("myorg/secret-repo", 45, 20, 100, 10, line=0),
             word("elsewhere", 10, 45, 60, 10, line=1)]
CLEAN_WORDS = [word("repo", 10, 20, 30, 10, line=0),
               word("elsewhere", 10, 45, 60, 10, line=1)]


def test_a_verify_read_of_zero_words_publishes_nothing_and_exits_two(
        blank_image, terms_file, tmp_path, capsys, monkeypatch):
    """A verify instrument that reads nothing proves nothing."""
    out_dir = tmp_path / "out"
    out_dir.mkdir()
    backend = ScriptedBackend([HIT_WORDS, []])

    code, out = run_scripted(capsys, monkeypatch, backend,
                             blank_image, "-o", out_dir,
                             "--scales", "1", "--terms-file", terms_file)
    assert code == 2
    assert "verify OCR read ZERO words from the candidate" in out
    assert "nothing was published" in out
    assert os.listdir(str(out_dir)) == [], "an unverified file was left behind"


def test_a_term_surviving_in_the_output_exits_one_and_publishes_nothing(
        blank_image, terms_file, tmp_path, capsys, monkeypatch):
    """The redaction did not work, so the candidate must not be published.

    A previously verified output is sitting at the destination. It must come
    through byte for byte, because a run that fails is not allowed to destroy
    a result that passed.
    """
    out_dir = tmp_path / "out"
    out_dir.mkdir()
    existing = out_dir / "input-scrubbed.png"
    existing.write_bytes(b"an earlier, verified output")
    before = existing.read_bytes()

    backend = ScriptedBackend([HIT_WORDS, HIT_WORDS])
    code, out = run_scripted(capsys, monkeypatch, backend,
                             blank_image, "-o", out_dir, "--force",
                             "--scales", "1", "--terms-file", terms_file)
    assert code == 1
    assert "VERIFY FAILED" in out
    assert "NOT PUBLISHED" in out
    assert existing.read_bytes() == before, "the earlier output was destroyed"
    assert temp_files_in(out_dir) == [], "a candidate file was left behind"


def test_a_passing_scripted_run_publishes_the_candidate(
        blank_image, terms_file, tmp_path, capsys, monkeypatch):
    out_dir = tmp_path / "out"
    out_dir.mkdir()
    backend = ScriptedBackend([HIT_WORDS, CLEAN_WORDS])

    code, out = run_scripted(capsys, monkeypatch, backend,
                             blank_image, "-o", out_dir,
                             "--scales", "1", "--terms-file", terms_file)
    assert code == 0
    assert "VERIFY PASSED" in out
    assert (out_dir / "input-scrubbed.png").is_file()
    assert temp_files_in(out_dir) == []


# ------------------------------------------- joining and coordinate mapping

def test_folding_finds_a_misread_term_and_no_fold_misses_it():
    """The fold-versus-miss property, proved without a real engine.

    The live-engine test above can only demonstrate the miss where the
    recognizer actually misreads the sample, which is a property of that
    engine on that platform - Vision reads the same sample cleanly, so the
    second half is asserted on Windows only there. The property itself is not
    platform specific, so it is proved here instead, deterministically and
    everywhere, by feeding the matcher the misread directly: the engine
    returned the leading 'i' of 'internal' as a 't'.

    Folded, the term is found. Unfolded, it is not. That pair is the whole
    argument for glyph folding and it must be held down on every platform,
    not only on the one whose engine happens to make the mistake.
    """
    misread = "https:/ftnternal-hostname.local/status/session"
    backend = ScriptedBackend([[word(misread, 10, 20, 300, 12, line=0)]])
    ocr_pass = cli.ocr_image(Image.new("RGB", (400, 60)), 1, "en", backend)

    folded = cli.find_hits(ocr_pass, [gen_samples.TERM_HOST], True)
    assert len(folded) == 1
    assert folded[0].box == (10.0, 20.0, 310.0, 32.0)

    assert cli.find_hits(ocr_pass, [gen_samples.TERM_HOST], False) == []


def test_a_term_split_across_adjacent_words_on_one_line_is_joined():
    """Three OCR words, one term, one rectangle spanning all three."""
    backend = ScriptedBackend([[
        word("myorg", 10, 20, 40, 10, line=0),
        word("/secret", 55, 20, 50, 10, line=0),
        word("-repo", 110, 20, 35, 10, line=0),
        word("elsewhere", 10, 60, 60, 10, line=1),
    ]])
    ocr_pass = cli.ocr_image(Image.new("RGB", (300, 100)), 1, "en", backend)
    assert len(ocr_pass.lines) == 2

    hits = cli.find_hits(ocr_pass, ["myorg/secret-repo"], True)
    assert len(hits) == 1
    assert hits[0].box == (10.0, 20.0, 145.0, 30.0)


def test_a_term_split_across_two_lines_is_not_joined():
    """The documented limit, held down by a test so it cannot drift."""
    backend = ScriptedBackend([[
        word("myorg", 10, 20, 40, 10, line=0),
        word("/secret-repo", 10, 40, 80, 10, line=1),
    ]])
    ocr_pass = cli.ocr_image(Image.new("RGB", (300, 100)), 1, "en", backend)
    assert cli.find_hits(ocr_pass, ["myorg/secret-repo"], True) == []


def test_a_scaled_read_maps_back_to_native_coordinates_exactly():
    """The engine sees the enlarged image; every rectangle divides back down."""
    backend = ScriptedBackend([[word("secret", 31, 62, 91, 25, line=0)]])
    ocr_pass = cli.ocr_image(Image.new("RGB", (100, 50)), 3, "en", backend)

    assert backend.calls[0][0] == (300, 150), "the engine was not handed 3x"
    (text, box), = ocr_pass.lines[0]
    assert text == "secret"
    assert box == (31 / 3.0, 62 / 3.0, 91 / 3.0, 25 / 3.0)


def test_a_read_over_the_megapixel_budget_is_refused_before_it_is_attempted(
        blank_image, terms_file, capsys, monkeypatch):
    """Passing the side limit says nothing about the allocation.

    200x100 at scale 20 is 4000x2000 - well inside a 10000 px side limit, and
    8 megapixels, which is over a 1 megapixel budget. The engine must never
    be reached and the resize must never be attempted.
    """
    backend = ScriptedBackend([], max_image_dimension=10000)
    code, out = run_scripted(capsys, monkeypatch, backend,
                             blank_image, "--check-only", "--scales", "20",
                             "--max-megapixels", "1",
                             "--terms-file", terms_file)
    assert code == 2
    assert "over the 1 megapixel budget for one read" in out
    assert backend.calls == [], "the engine was called on an oversized read"


def test_the_megapixel_budget_must_be_at_least_one(blank_image, terms_file,
                                                   capsys):
    code, out = run(capsys, blank_image, "--check-only",
                    "--max-megapixels", "0", "--terms-file", terms_file)
    assert code == 2
    assert "--max-megapixels must be at least 1" in out


def test_an_image_too_big_for_the_engine_is_refused_before_it_is_read(
        blank_image, terms_file, capsys, monkeypatch):
    """Refused off the header, at scale 1, without touching the engine."""
    backend = ScriptedBackend([], max_image_dimension=150)
    code, out = run_scripted(capsys, monkeypatch, backend,
                             blank_image, "--check-only",
                             "--scales", "1", "--terms-file", terms_file)
    assert code == 2
    assert "over the scripted test backend limit of 150 px" in out
    assert backend.calls == [], "the engine was called on an oversized image"


# ------------------------------------------------------------- usage and rules

# The input-overwrite guard is a FILESYSTEM question, not an OCR one, so all
# three arms are driven with the scripted backend: coupling them to a real
# recognizer would make them skip on a platform where the guard itself works
# perfectly well. Hard links and os.path.samefile work on Windows and on
# macOS alike, so the hard-link arm is gated on nothing.
#
# Some arms do need a volume on which two spellings name one file. That is a
# property of the VOLUME THE TEST WRITES TO, and it is measured there.
#
# It must not be keyed on sys.platform, and this file has already made that
# mistake once: a marker reading `sys.platform != "win32"` skipped four arms
# on a Mac whose volume is case-insensitive and would have run every one of
# them, while its reason string said "a POSIX host may not be" and then
# assumed it was not. The guard those arms cover is the one protecting the
# only unredacted copy of the input, so it stood unproved there for a reason
# that was not true - a skip that reads like a pass, which is the same defect
# these tests exist to catch, one layer down.
#
# It also cannot be a marker at all. pytest.mark.skipif is evaluated at
# collection time, when tmp_path does not exist yet, so a marker could only
# ever probe some other directory than the one under test. The probe
# therefore happens inside the test, on its own directory, using the same
# instrument the tool itself uses.
def require_case_insensitive(directory):
    """Skip only on a measured answer from the volume under test."""
    if not cli.directory_is_case_insensitive(str(directory)):
        pytest.skip("this arm needs a volume on which two spellings name one "
                    "file; %s is case-sensitive (measured, not assumed)"
                    % directory)


def test_refuses_to_overwrite_the_input_image(blank_image, terms_file, capsys,
                                              monkeypatch):
    backend = ScriptedBackend([HIT_WORDS])
    code, out = run_scripted(capsys, monkeypatch, backend,
                             blank_image, "-o", blank_image,
                             "--scales", "1", "--terms-file", terms_file)
    assert code == 2
    assert "refusing to overwrite the input image" in out


def test_refuses_to_overwrite_the_input_addressed_in_a_different_case(
        blank_image, terms_file, capsys, monkeypatch):
    """The same file spelled in another case is still the same file.

    A string comparison of absolute paths says these two are different and
    would let the scrub overwrite the only unredacted copy of the input.
    """
    require_case_insensitive(blank_image.parent)
    alias = blank_image.parent / blank_image.name.upper()

    # The premise, asserted rather than assumed.
    assert os.path.abspath(str(blank_image)) != os.path.abspath(str(alias))
    assert os.path.exists(str(alias))
    assert os.path.samefile(str(blank_image), str(alias))

    before = blank_image.read_bytes()
    backend = ScriptedBackend([HIT_WORDS])
    code, out = run_scripted(capsys, monkeypatch, backend,
                             blank_image, "-o", alias, "--force",
                             "--scales", "1", "--terms-file", terms_file)
    assert code == 2
    assert "refusing to overwrite the input image" in out
    assert blank_image.read_bytes() == before, "the input image was modified"


def test_refuses_to_overwrite_the_input_reached_through_a_hard_link(
        blank_image, terms_file, tmp_path, capsys, monkeypatch):
    """A hard link is a second name for one file, not a second file.

    No platform marker: os.link and os.path.samefile both work on Windows and
    on macOS, so this arm must run on both.
    """
    link = tmp_path / "link-to-input.png"
    os.link(str(blank_image), str(link))
    assert os.path.samefile(str(blank_image), str(link))

    before = blank_image.read_bytes()
    backend = ScriptedBackend([HIT_WORDS])
    code, out = run_scripted(capsys, monkeypatch, backend,
                             blank_image, "-o", link, "--force",
                             "--scales", "1", "--terms-file", terms_file)
    assert code == 2
    assert "refusing to overwrite the input image" in out
    assert blank_image.read_bytes() == before, "the input image was modified"


def test_a_missing_terms_file_names_the_example_to_copy(samples, capsys):
    code, out = run(capsys, samples / "sample-normal.png", "--check-only",
                    "--terms-file", samples / "nope.txt")
    assert code == 2
    assert "terms file not found" in out
    assert "terms.example.txt" in out


def test_the_shipped_example_denylist_parses(capsys):
    terms = cli.load_terms(cli.EXAMPLE_TERMS_FILE)
    assert terms == TERMS


def test_bad_scales_are_a_usage_error(samples, terms_file, capsys):
    code, out = run(capsys, samples / "sample-normal.png", "--check-only",
                    "--terms-file", terms_file, "--scales", "0")
    assert code == 2
    assert "--scales takes positive whole numbers" in out


# --------------------------------------------------------------- unit coverage

def test_word_ranges_count_utf16_code_units_not_code_points():
    """The macOS backend hands these ranges to NSRange, which indexes
    NSStrings in UTF-16 code units. A character outside the Basic
    Multilingual Plane is one Python code point but two UTF-16 units, so
    counting code points would shift every later word's range by one per
    such character and the recognizer would return a neighbouring word's
    rectangle. The grinning-face character below is exactly that case.
    """
    text = "\U0001F600 secret word"
    assert _words_with_utf16_ranges(text) == [
        ("\U0001F600", 0, 2),   # two code units, one code point
        ("secret", 3, 6),        # starts at 3, not the code-point offset 2
        ("word", 10, 4),
    ]


def test_word_ranges_match_code_points_for_plain_text():
    # Inside the Basic Multilingual Plane the two counts agree, so this
    # pins the ordinary case the OCR engines actually produce.
    assert _words_with_utf16_ranges("re po myorg") == [
        ("re", 0, 2), ("po", 3, 2), ("myorg", 6, 5)]
    assert _words_with_utf16_ranges("  padded  ") == [("padded", 2, 6)]
    assert _words_with_utf16_ranges("") == []

def test_a_denylist_term_that_can_never_match_is_refused(
        samples, tmp_path, capsys):
    """A term counted in the banner but skipped in the matcher is a lie."""
    path = tmp_path / "terms.txt"
    path.write_text("example@example.com\n...\n", encoding="utf-8")
    code, out = run(capsys, samples / "sample-normal.png", "--check-only",
                    "--terms-file", path)
    assert code == 2
    assert "nothing left to match on after normalisation" in out
    assert "'...'" in out


def test_term_validation_follows_the_normalisation_actually_in_force():
    # '!!!' folds onto 'lll' and can match; with --no-fold it is punctuation
    # and cannot. The verdict has to follow the setting, not a guess.
    cli.validate_terms(["!!!"], True)
    with pytest.raises(ScrubError):
        cli.validate_terms(["!!!"], False)


def test_two_inputs_that_would_share_one_output_are_refused(tmp_path, capsys,
                                                            terms_file):
    """shot.png and shot.jpg both want shot-scrubbed.png."""
    work = tmp_path / "collide"
    work.mkdir()
    Image.new("RGB", (60, 40), (255, 255, 255)).save(str(work / "shot.png"))
    Image.new("RGB", (60, 40), (255, 255, 255)).save(str(work / "shot.jpg"))

    code, out = run(capsys, work, "--terms-file", terms_file)
    assert code == 2
    assert "would both be written to" in out
    assert "shot-scrubbed.png" in out


def test_two_inputs_whose_stems_differ_only_in_case_are_refused(
        tmp_path, capsys, terms_file):
    """Shot.png and shot.jpg want Shot-scrubbed.png and shot-scrubbed.png.

    Those two names are one file wherever the volume is case-insensitive.
    """
    work = tmp_path / "case-collide"
    work.mkdir()
    require_case_insensitive(work)
    Image.new("RGB", (60, 40), (255, 255, 255)).save(str(work / "Shot.png"))
    Image.new("RGB", (60, 40), (255, 255, 255)).save(str(work / "shot.jpg"))

    code, out = run(capsys, work, "--terms-file", terms_file)
    assert code == 2
    assert "would both be written to" in out


def test_collision_detection_does_not_depend_on_the_host_normcase(
        tmp_path, capsys, terms_file, monkeypatch):
    """The exact defect: normcase is the HOST's rule, not the volume's.

    os.path.normcase lower-cases on Windows and is the identity function on
    POSIX, so a lexical comparison built on it stops detecting anything on a
    case-insensitive volume under a POSIX host - which is a normal Mac. This
    test makes normcase the identity function and requires the collision to
    be found anyway, because the answer must come from the destination
    directory rather than from the path module.
    """
    work = tmp_path / "normcase-collide"
    work.mkdir()
    require_case_insensitive(work)

    monkeypatch.setattr(os.path, "normcase", lambda path: path)

    Image.new("RGB", (60, 40), (255, 255, 255)).save(str(work / "Shot.png"))
    Image.new("RGB", (60, 40), (255, 255, 255)).save(str(work / "shot.jpg"))

    code, out = run(capsys, work, "--terms-file", terms_file)
    assert code == 2
    assert "would both be written to" in out


def test_the_case_probe_answers_from_the_filesystem_and_cleans_up(tmp_path):
    """The instrument, checked against an independent observation.

    No skip and no platform anywhere in here: on any volume the probe's
    verdict has to match what that directory actually does with two
    spellings of one name, so the test writes a witness file and looks.
    """
    witness = tmp_path / "Witness.txt"
    witness.write_text("x", encoding="utf-8")
    observed = os.path.exists(str(tmp_path / "witness.txt"))
    witness.unlink()

    before = sorted(os.listdir(str(tmp_path)))
    assert cli.directory_is_case_insensitive(str(tmp_path)) == observed
    assert sorted(os.listdir(str(tmp_path))) == before, "the probe was left behind"


def _explode_on_the_probe(monkeypatch, name, error):
    """Make one os call fail for the case probe only, and nothing else.

    The probe's own name is mixed case and the readback looks it up with the
    case swapped, so the match has to be case-insensitive to catch both
    spellings.
    """
    original = getattr(os, name)

    def exploding(path, *args, **kwargs):
        if "ccscrubcase" in str(path).lower():
            raise error
        return original(path, *args, **kwargs)

    monkeypatch.setattr(os, name, exploding)


def test_a_probe_readback_error_is_an_error_not_a_case_sensitive_answer(
        tmp_path, monkeypatch):
    """The fail-open case: an unreadable probe must not answer 'sensitive'.

    os.path.exists swallows OSError and returns False, so a permission
    error, an I/O error or a race used to be classified as case-sensitive -
    which is the answer that switches the output-collision check OFF and
    lets one result overwrite another. Only a genuine not-found means
    case-sensitive; every other error is exit 2.
    """
    _explode_on_the_probe(monkeypatch, "stat",
                          PermissionError(13, "permission denied"))
    with pytest.raises(ScrubError) as caught:
        cli.directory_is_case_insensitive(str(tmp_path))
    assert "Refusing to guess" in str(caught.value)


def test_a_probe_that_cannot_be_removed_is_an_error_not_a_warning(
        tmp_path, monkeypatch):
    """A verdict returned after a failed cleanup is a verdict from a
    directory that is not behaving as the check assumes."""
    _explode_on_the_probe(monkeypatch, "remove",
                          PermissionError(13, "permission denied"))
    with pytest.raises(ScrubError) as caught:
        cli.directory_is_case_insensitive(str(tmp_path))
    assert "could not be removed" in str(caught.value)


def test_a_genuinely_missing_swapped_name_is_the_case_sensitive_answer(
        tmp_path, monkeypatch):
    """FileNotFoundError is the one error that IS an answer.

    Everything else is an instrument failure, but a swapped name that
    honestly is not there is exactly what a case-sensitive volume looks
    like, so it must come back False rather than raise.
    """
    _explode_on_the_probe(monkeypatch, "stat",
                          FileNotFoundError(2, "no such file"))
    assert cli.directory_is_case_insensitive(str(tmp_path)) is False


def test_the_megapixel_budget_is_decimal_megapixels_not_mebipixels():
    """The number is pinned, not the direction.

    --max-megapixels 1 must mean 1,000,000 pixels. Read as mebipixels it
    meant 1,048,576, so a documented 192 megapixel ceiling actually admitted
    201,326,592 pixels - a true 200 megapixel image passing under a 192
    megapixel limit.
    """
    backend = ScriptedBackend([], max_image_dimension=100000)

    # Exactly one million pixels is inside a one megapixel budget.
    cli.check_scales_fit((1000, 1000), [1], backend, "image", 1)

    # A thousand more is not.
    with pytest.raises(ScrubError) as caught:
        cli.check_scales_fit((1000, 1001), [1], backend, "image", 1)
    assert "over the 1 megapixel budget" in str(caught.value)

    # 1024x1024 is 1,048,576 pixels. Under the mebipixel reading that was
    # exactly at the limit and passed; it is over a decimal megapixel.
    with pytest.raises(ScrubError):
        cli.check_scales_fit((1024, 1024), [1], backend, "image", 1)

    # And the scaled arithmetic is decimal too: 500x500 at scale 2 is one
    # million pixels exactly.
    cli.check_scales_fit((500, 500), [2], backend, "image", 1)
    with pytest.raises(ScrubError):
        cli.check_scales_fit((500, 501), [2], backend, "image", 1)

    # The PRINTED figure is pinned, not just the direction, because a fix
    # can reach the comparison and miss the message. 900x260 at scale 8 is
    # 7200x2080, which is 14,976,000 pixels: 15.0 true megapixels, and 14.3
    # if the unit were mebipixels. This is the same arithmetic the proof
    # transcript prints.
    with pytest.raises(ScrubError) as caught:
        cli.check_scales_fit((900, 260), [8], backend, "image", 1)
    assert "15.0 megapixels" in str(caught.value)
    assert "14.3" not in str(caught.value)


def test_the_case_probe_refuses_to_guess_when_it_cannot_be_created(tmp_path):
    missing = tmp_path / "no-such-directory"
    with pytest.raises(ScrubError) as caught:
        cli.directory_is_case_insensitive(str(missing))
    assert "Refusing to guess" in str(caught.value)


def test_an_output_directory_that_cannot_be_created_exits_two(
        blank_image, terms_file, tmp_path, capsys):
    """Output I/O answers with exit 2, not a raw traceback."""
    blocker = tmp_path / "a-file"
    blocker.write_text("not a directory", encoding="utf-8")
    code, out = run(capsys, blank_image, "-o", blocker / "sub" / "out.png",
                    "--terms-file", terms_file)
    assert code == 2
    assert "cannot create the output directory" in out


def test_pad_box_grows_outwards_to_whole_pixels():
    # Rounding the far edges instead of ceiling them used to return
    # (10, 20, 40, 30) here, losing the last column and the last row of the
    # region - a strip of readable text left behind at --pad 0.
    assert cli._pad_box((10.2, 20.9, 40.1, 30.4), 0, (100, 100)) == (10, 20, 41, 31)


def test_pad_box_pads_and_clamps_to_the_image():
    assert cli._pad_box((1.5, 1.5, 98.5, 98.5), 4, (100, 100)) == (0, 0, 100, 100)


def test_normalise_drops_punctuation_and_case():
    assert cli.normalise("Example@Example.COM", fold=False) == "exampleexamplecom"


def test_normalise_folds_every_advertised_glyph_class():
    """All six classes, every member - including the two that were dead.

    '!' and '|' are not alphanumeric. While the alphanumeric filter ran
    before the fold, they were discarded rather than folded, so two of the
    six advertised folds did nothing at all and an engine reading 'internal'
    as '!nternal' produced a false negative. Asserting only the 'l'/'t' pair
    and 'O0', as this test once did, could not see that.
    """
    for target, members in cli._FOLD_CLASSES:
        for member in members:
            assert cli.normalise(member, fold=True) == target, (
                "'%s' does not fold onto '%s'" % (member, target))

    # The two that were silently dropped, stated as their own case.
    assert cli.normalise("!nternal", fold=True) == cli.normalise("internal",
                                                                 fold=True)
    assert cli.normalise("|nternal", fold=True) == cli.normalise("internal",
                                                                 fold=True)

    # Folding off means an exact normalised match, and punctuation is gone.
    assert cli.normalise("tnternal", fold=False) != cli.normalise("internal",
                                                                  fold=False)
    assert cli.normalise("!nternal", fold=False) == "nternal"


def test_parse_scales_sorts_and_deduplicates():
    assert cli.parse_scales("3,1,2,1") == [1, 2, 3]


def test_parse_scales_rejects_rubbish():
    with pytest.raises(ScrubError):
        cli.parse_scales("two")
    with pytest.raises(ScrubError):
        cli.parse_scales("")


def test_load_terms_ignores_comments_and_blank_lines(tmp_path):
    path = tmp_path / "t.txt"
    path.write_text("# a comment\n\nalpha\nbeta # trailing\n", encoding="utf-8")
    assert cli.load_terms(str(path)) == ["alpha", "beta"]


def test_load_terms_rejects_an_empty_denylist(tmp_path):
    path = tmp_path / "t.txt"
    path.write_text("# nothing but comments\n", encoding="utf-8")
    with pytest.raises(ScrubError):
        cli.load_terms(str(path))


def test_merge_hits_unions_overlapping_rectangles_of_one_term():
    a = cli.Hit("x", (10.0, 10.0, 50.0, 30.0), 1, "line one")
    b = cli.Hit("x", (40.0, 12.0, 90.0, 32.0), 2, "line one longer")
    merged = cli.merge_hits([a, b])
    assert len(merged) == 1
    assert merged[0].box == (10.0, 10.0, 90.0, 32.0)
    assert merged[0].scales == [1, 2]


def test_merge_hits_keeps_different_terms_apart():
    a = cli.Hit("x", (10.0, 10.0, 50.0, 30.0), 1, "line")
    b = cli.Hit("y", (10.0, 10.0, 50.0, 30.0), 1, "line")
    assert len(cli.merge_hits([a, b])) == 2


def test_gather_inputs_skips_already_scrubbed_files(tmp_path):
    (tmp_path / "one.png").write_bytes(b"")
    (tmp_path / "one-scrubbed.png").write_bytes(b"")
    (tmp_path / "notes.txt").write_text("x", encoding="utf-8")
    found = [os.path.basename(p) for p in cli.gather_inputs(str(tmp_path))]
    assert found == ["one.png"]


def test_gather_inputs_rejects_a_missing_path(tmp_path):
    with pytest.raises(ScrubError):
        cli.gather_inputs(str(tmp_path / "nowhere"))


def test_is_same_file_sees_through_a_case_variant_of_an_existing_path(tmp_path):
    require_case_insensitive(tmp_path)
    real = tmp_path / "shot.png"
    real.write_bytes(b"x")
    alias = tmp_path / "SHOT.PNG"
    assert os.path.abspath(str(real)) != os.path.abspath(str(alias))
    assert cli.is_same_file(str(real), str(alias))


def test_is_same_file_compares_canonically_when_the_target_is_not_created_yet(
        tmp_path):
    # The destination branch: nothing exists at either path, so the operating
    # system cannot be asked and the canonical spellings decide.
    missing = tmp_path / "sub" / ".." / "later.png"
    assert not os.path.exists(str(missing))
    assert cli.is_same_file(str(missing), str(tmp_path / "later.png"))


def test_is_same_file_says_no_to_two_genuinely_different_files(tmp_path):
    one = tmp_path / "one.png"
    two = tmp_path / "two.png"
    one.write_bytes(b"x")
    two.write_bytes(b"x")
    assert not cli.is_same_file(str(one), str(two))


def test_output_path_defaults_to_the_scrubbed_name_beside_the_input(tmp_path):
    source = tmp_path / "shot.png"
    source.write_bytes(b"")
    out = cli.output_path_for(str(source), None, False)
    assert os.path.basename(out) == "shot-scrubbed.png"
    assert os.path.dirname(out) == str(tmp_path)
