"""Writes the Your Throttle contract fixtures (final inspection finding F-01, and the field inventory of F-08).

ONE WIRE OBJECT, TWO REAL CONSUMERS, ONE RENDERED ANSWER. Each fixture here is a hostile `GET /stats/data`
"throttle" object and the answer a correct consumer renders from it. The browser client (client-core's
real normalizer, then the real Cockpit and mobile pages) and the mentor report (the real throttle.py
checker, metrics.py adapter and render_report ring row) are each fed the SAME object and must print the
SAME headline - or refuse it the same way. The fixtures are hostile on purpose: the headline disagrees
with the counts and the buckets, so a consumer that recomputes anything prints a different number and
fails. That is the class of defect the inspector reproduced by changing one constant in the browser
normalizer while every test stayed green.

Run this file to regenerate the fixtures and manifest.json (the SHA-256 of each fixture). The mentor
harness carries a copy of this directory under tools/mentor/tests/contract; its test pins the same
digests, so a fixture changed on one side and not the other is a red on the side that was not updated.

    python tools/throttle-conformance/contract/make_fixtures.py

ASCII only. Nothing here is a real person's data: the counts are the shape of the owner's 2026-W35 as the
mission recorded it, and the hostile counts are invented.
"""
import hashlib
import json
from pathlib import Path

HERE = Path(__file__).resolve().parent

DEFINITION = ("The shared figure is computed over activity_events rows where EventType is turn-submitted and "
              "InputOrigin is present, grouped by the origin's modality and surface.")
# The mentor's fixture week: Toronto 2026-W35, Monday 24 August 00:00 to Monday 31 August 00:00 local.
FROM_UTC = "2026-08-24T04:00:00Z"
TO_UTC = "2026-08-31T04:00:00Z"
CHOICES = [{"days": 1, "label": "Last 24 hours"}, {"days": 7, "label": "Last 7 days"},
           {"days": 14, "label": "Last 14 days"}, {"days": 30, "label": "Last 30 days"}]


def share(turns, denominator):
    if denominator == 0:
        return {"turns": turns, "share": None, "percent": None}
    fraction = turns / denominator
    return {"turns": turns, "share": fraction, "percent": int(fraction * 100.0 + 0.5)}


def surfaces(counts, denominator):
    out = []
    for surface, label in (("desktop", "Desktop"), ("cockpit", "Cockpit"), ("phone", "Phone"), ("unknown", "Unknown")):
        entry = share(counts.get(surface, 0), denominator)
        entry.update({"surface": surface, "label": label})
        out.append(entry)
    return out


def headline(denominator, voice, typed, by_surface):
    return {
        "denominator": denominator,
        "hasData": denominator > 0,
        "voice": share(voice, denominator),
        "typed": share(typed, denominator),
        "phone": share(by_surface.get("phone", 0), denominator),
        "surfaces": surfaces(by_surface, denominator),
    }


def wire(turns, voice, typed, buckets, head, excluded=None):
    return {
        "definition": DEFINITION,
        "unit": "submitted turns",
        "window": {"fromUtc": FROM_UTC, "toUtc": TO_UTC, "isDefault": False,
                   "label": "2026-08-24 04:00 to 2026-08-31 04:00 UTC", "kind": "explicit",
                   "days": None, "week": None, "choices": CHOICES},
        "ledger": {"retentionDays": 30, "earliestUtc": "2026-08-06T14:17:15Z"},
        "headline": head,
        "turns": turns,
        "voiceTurns": voice,
        "typedTurns": typed,
        "sessions": 3,
        "buckets": buckets,
        "hourlyTurns": [{"hour": "2026-08-24T13", "turns": turns, "voiceTurns": voice, "typedTurns": typed}],
        "agents": [{"agent": "ClaudeCode", "agentName": "Claude Code", "turns": turns, "voiceTurns": voice,
                    "typedTurns": typed, "sessions": 3, "agentDrivenTurns": 4}],
        "repos": [{"repo": "owner/repo-alpha", "repoName": "repo-alpha", "turns": turns, "voiceTurns": voice,
                   "typedTurns": typed, "sessions": 3, "checkouts": ["D:/repo-alpha"]}],
        "reposUnattributedTurns": 0,
        "excluded": excluded or {"noInputOrigin": 662, "agentDriven": 4, "framework": 160, "unresolved": 498},
        "agentDrivenTurns": 4,
    }


def rendered(head):
    """What a correct consumer renders from a headline: the fields, verbatim."""
    return {
        "denominator": head["denominator"],
        "hasData": head["hasData"],
        "voiceTurns": head["voice"]["turns"],
        "typedTurns": head["typed"]["turns"],
        "phoneTurns": head["phone"]["turns"],
        "voicePercent": head["voice"]["percent"],
        "typedPercent": head["typed"]["percent"],
        "phonePercent": head["phone"]["percent"],
        "surfaces": [{"surface": s["surface"], "label": s["label"], "turns": s["turns"], "percent": s["percent"]}
                     for s in head["surfaces"]],
    }


# The recorded shape of the owner's 2026-W35: 1786 counted, 1015 spoken (57 per cent), 248 from the phone
# (14 per cent) - the headline every hostile fixture below carries.
W35_SURFACES = {"desktop": 1531, "phone": 248, "unknown": 7}
W35_HEADLINE = headline(1786, 1015, 771, W35_SURFACES)
W35_BUCKETS = [
    {"modality": "typed", "surface": "desktop", "turns": 696},
    {"modality": "typed", "surface": "phone", "turns": 68},
    {"modality": "typed", "surface": "unknown", "turns": 7},
    {"modality": "voice", "surface": "desktop", "turns": 835},
    {"modality": "voice", "surface": "phone", "turns": 180},
]

FIXTURES = [
    {
        "name": "the-headline-is-rendered-not-the-counts",
        "why": ("The counts and the buckets agree with each other and say 8 of 10 turns were spoken (80 per cent), "
                "all 8 from the phone. The headline says 1015 of 1786 (57 per cent), 248 from the phone (14 per "
                "cent). A consumer that renders the headline prints 57 and 14; one that divides the counts, or "
                "re-totals the buckets, prints 80. Both consumers must print 57 and 14."),
        "wire": wire(10, 8, 2,
                     [{"modality": "voice", "surface": "phone", "turns": 8},
                      {"modality": "typed", "surface": "desktop", "turns": 2}],
                     W35_HEADLINE),
        "expected": {"outcome": "rendered", "rendered": rendered(W35_HEADLINE)},
    },
    {
        "name": "the-percent-field-is-printed-not-re-rounded",
        "why": ("The headline's shares are the real 2026-W35 fractions but its percent fields are 61 spoken and "
                "9 from the phone - not what rounding those fractions gives (57 and 14). A consumer that prints "
                "the percent field prints 61 and 9; one that rounds the share for itself prints 57 and 14. Both "
                "consumers must print 61 and 9: the rounding is the library's, done once."),
        "wire": wire(1786, 1015, 771, W35_BUCKETS, {
            **W35_HEADLINE,
            "voice": {**W35_HEADLINE["voice"], "percent": 61},
            "typed": {**W35_HEADLINE["typed"], "percent": 39},
            "phone": {**W35_HEADLINE["phone"], "percent": 9},
            "surfaces": [dict(s, percent=9) if s["surface"] == "phone" else s for s in W35_HEADLINE["surfaces"]],
        }),
        "expected": None,  # filled below from the wire's own headline
    },
    {
        "name": "the-empty-state-is-the-librarys-ruling",
        "why": ("Nothing counted: the headline says hasData false with every share and percent null, while the "
                "counts below happen to say 10 turns. Both consumers must show NO number - the browser its empty "
                "state, the report a refusal to draw a ring - and neither may print 0% or 100%."),
        "wire": wire(10, 8, 2,
                     [{"modality": "voice", "surface": "phone", "turns": 8},
                      {"modality": "typed", "surface": "desktop", "turns": 2}],
                     headline(0, 0, 0, {})),
        "expected": {"outcome": "empty", "rendered": rendered(headline(0, 0, 0, {}))},
    },
    {
        "name": "an-answer-without-a-headline-is-refused",
        "why": ("The headline block is absent and the counts are the real 2026-W35. A consumer that rebuilds the "
                "headline from the counts prints 57 - and has just become the second computation ruling R3 "
                "forbids. Both consumers must refuse the answer."),
        "wire": {k: v for k, v in wire(1786, 1015, 771, W35_BUCKETS, W35_HEADLINE).items() if k != "headline"},
        "expected": {"outcome": "refused"},
    },
    {
        "name": "a-surface-the-consumers-do-not-know-is-refused",
        "why": ("The headline lists a fifth surface, 'watch'. Neither consumer may fold it into 'unknown' or drop "
                "it; both must refuse the answer, the same way the library refuses a bucket on that surface."),
        "wire": wire(1786, 1015, 771, W35_BUCKETS, {
            **W35_HEADLINE,
            "surfaces": W35_HEADLINE["surfaces"] + [dict(share(0, 1786), surface="watch", label="Watch")],
        }),
        "expected": {"outcome": "refused"},
    },
    {
        "name": "a-modality-token-the-consumers-do-not-know-is-refused",
        "why": ("A bucket carries the modality 'spoken'. The library only ever writes 'voice' and 'typed'; a "
                "consumer that maps every other token to typed (or to voice) is guessing. Both must refuse."),
        "wire": wire(1786, 1015, 771,
                     [dict(b, modality="spoken") if b["modality"] == "voice" else b for b in W35_BUCKETS],
                     W35_HEADLINE),
        "expected": {"outcome": "refused"},
    },
]
FIXTURES[1]["expected"] = {"outcome": "rendered", "rendered": rendered(FIXTURES[1]["wire"]["headline"])}

# ---------------------------------------------------------------- the field inventory (finding F-08)
#
# Every field on the library's answer, as the feed serves it, and which real consumer reads it. The C#
# side asserts the DTO has exactly these paths (a new DTO field must be added here on purpose); the
# browser and the report each assert that every path marked for them survives their real normalizer with
# the wire's value. A field nobody reads is written down as such rather than left unmentioned.
INVENTORY = {
    "definition": ["browser", "report"],
    "unit": ["browser", "report"],
    "window.fromUtc": ["browser", "report"],
    "window.toUtc": ["browser", "report"],
    "window.isDefault": ["browser"],
    "window.label": ["browser", "report"],
    "window.kind": ["browser"],
    "window.days": ["browser"],
    "window.week": ["browser"],
    "window.choices[].days": ["browser"],
    "window.choices[].label": ["browser"],
    "ledger.retentionDays": ["browser", "report"],
    "ledger.earliestUtc": ["browser", "report"],
    "headline.denominator": ["browser", "report"],
    "headline.hasData": ["browser", "report"],
    "headline.voice.turns": ["browser", "report"],
    "headline.voice.share": ["browser", "report"],
    "headline.voice.percent": ["browser", "report"],
    "headline.typed.turns": ["browser", "report"],
    "headline.typed.share": ["browser", "report"],
    "headline.typed.percent": ["browser", "report"],
    "headline.phone.turns": ["browser", "report"],
    "headline.phone.share": ["browser", "report"],
    "headline.phone.percent": ["browser", "report"],
    "headline.surfaces[].surface": ["browser", "report"],
    "headline.surfaces[].label": ["browser", "report"],
    "headline.surfaces[].turns": ["browser", "report"],
    "headline.surfaces[].share": ["browser", "report"],
    "headline.surfaces[].percent": ["browser", "report"],
    "turns": ["browser", "report"],
    "voiceTurns": ["browser", "report"],
    "typedTurns": ["browser", "report"],
    "sessions": ["browser", "report"],
    "buckets[].modality": ["browser", "report"],
    "buckets[].surface": ["browser", "report"],
    "buckets[].turns": ["browser", "report"],
    "hourlyTurns[].hour": ["browser"],
    "hourlyTurns[].turns": ["browser"],
    "hourlyTurns[].voiceTurns": ["browser"],
    "hourlyTurns[].typedTurns": ["browser"],
    "agents[].agent": ["browser"],
    "agents[].agentName": ["browser"],
    "agents[].turns": ["browser"],
    "agents[].voiceTurns": ["browser"],
    "agents[].typedTurns": ["browser"],
    "agents[].sessions": ["browser"],
    "agents[].agentDrivenTurns": ["browser"],
    "repos[].repo": ["browser"],
    "repos[].repoName": ["browser"],
    "repos[].turns": ["browser"],
    "repos[].voiceTurns": ["browser"],
    "repos[].typedTurns": ["browser"],
    "repos[].sessions": ["browser"],
    "repos[].checkouts[]": ["browser"],
    "reposUnattributedTurns": ["browser"],
    "excluded.noInputOrigin": ["browser", "report"],
    "excluded.agentDriven": ["browser", "report"],
    "excluded.framework": ["browser", "report"],
    "excluded.unresolved": ["browser", "report"],
    "agentDrivenTurns": ["browser", "report"],
}


def dump(path, value):
    """Write one file with LF endings and return the digest of that LF text. Every reader of manifest.json
    normalises CRLF to LF before digesting, because git writes CRLF into a fresh Windows checkout."""
    text = json.dumps(value, indent=2, sort_keys=True, ensure_ascii=True) + "\n"
    path.write_text(text, encoding="ascii", newline="\n")
    return hashlib.sha256(text.encode("ascii")).hexdigest()


def main():
    manifest = {"fixtures": {}, "inventory": None}
    for fixture in FIXTURES:
        name = fixture["name"] + ".json"
        manifest["fixtures"][name] = dump(HERE / name, fixture)
    manifest["inventory"] = dump(HERE / "field-inventory.json", {"fields": INVENTORY})
    dump(HERE / "manifest.json", manifest)
    print("wrote " + str(len(FIXTURES)) + " fixtures, the field inventory and manifest.json to " + str(HERE))


if __name__ == "__main__":
    main()
