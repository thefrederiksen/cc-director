"""Tests for cc-scrub.

The OCR tests run the tool end to end over the synthetic samples that
gen_samples.py draws - there is no mocked recognizer anywhere, because a
mocked one would prove nothing about the engine this tool actually depends
on.

Run from this tool's directory:

    python -m pytest tests/ -q
"""

import os
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))

import gen_samples
from src import cli
from src.ocr_backend import ScrubError


# The platforms that have a working OCR backend today. macOS joins this
# tuple when MacVisionBackend is implemented. It is a list of platforms and
# NOT a probe of whether OCR happens to work: on a supported platform a
# missing or broken engine must fail these tests, not quietly skip them.
SUPPORTED_PLATFORMS = ("win32",)

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
    """The host name comes back with its leading l-shaped glyph as a t.

    Folded, it matches; unfolded, it does not. That pair is the whole
    argument for glyph folding, so both halves are asserted here.
    """
    folded_code, folded = run(capsys, samples / "sample-glyph.png",
                              "--check-only", "--terms-file", terms_file)
    assert folded_code == 1
    assert "term='%s'" % gen_samples.TERM_HOST in folded

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
    # regions redacted, words read back out of the file on disk.
    assert "VERIFY PASSED: 1 hit(s) found, 1 region(s) redacted, verify OCR " \
           "read " in out
    assert "and 0 denylist hit(s) remain." in out
    assert "verify OCR read 0 words" not in out


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

    # Second run sees the two outputs beside the inputs and must still pick
    # up exactly the two inputs.
    code, out = run(capsys, work, "--terms-file", terms_file)
    assert code == 0
    assert "images     : 2" in out


# -------------------------------------------------------- the broken instrument

@requires_ocr
def test_an_image_with_no_text_is_a_broken_read_not_a_clean_image(
        samples, terms_file, capsys):
    code, out = run(capsys, samples / "sample-notext.png",
                    "--check-only", "--terms-file", terms_file)
    assert code == 2
    assert "OCR read ZERO words" in out
    assert "Refusing to call this scrubbed." in out


# ------------------------------------------------------------- usage and rules

@requires_ocr
def test_refuses_to_overwrite_the_input_image(samples, terms_file, tmp_path,
                                              capsys):
    target = tmp_path / "sample-normal.png"
    target.write_bytes((samples / "sample-normal.png").read_bytes())
    code, out = run(capsys, target, "-o", target, "--terms-file", terms_file)
    assert code == 2
    assert "refusing to overwrite the input image" in out


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

def test_normalise_drops_punctuation_and_case():
    assert cli.normalise("Example@Example.COM", fold=False) == "exampleexamplecom"


def test_normalise_folds_the_glyph_classes():
    # i, 1, t, j, ! and | all fold onto l, which is what lets a misread
    # host name still match its term.
    assert cli.normalise("tnternal", fold=True) == cli.normalise("internal", fold=True)
    assert cli.normalise("O0", fold=True) == "oo"
    assert cli.normalise("tnternal", fold=False) != cli.normalise("internal", fold=False)


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


def test_output_path_defaults_to_the_scrubbed_name_beside_the_input(tmp_path):
    source = tmp_path / "shot.png"
    source.write_bytes(b"")
    out = cli.output_path_for(str(source), None, False)
    assert os.path.basename(out) == "shot-scrubbed.png"
    assert os.path.dirname(out) == str(tmp_path)
