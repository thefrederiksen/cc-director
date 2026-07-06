# Live browser proof driver for issue #1088 (browser device enrollment is the Cockpit front door).
#
# Prerequisites (see tools/harnesses/browser-enrollment-proof/Program.cs):
#   1. VITE_DT_SITE_BASE=http://127.0.0.1:8971 npm run build --workspace @devthrottle/cockpit
#   2. dotnet run --project tools/harnesses/browser-enrollment-proof -- 8970 8971
#   3. python docs/cencon/proof/issue-1088/drive-proof.py
#
# Drives a REAL Chromium against the REAL GatewayHost (auth ON, signed in) and the local activation
# fixture (which models the devthrottle.com contract requested on cross-repo issue #1081, because the
# production page hard-rejects non-phone enrollment today). Captures the artifacts for every
# acceptance criterion into this directory. ASCII-only output; no device-key value is ever written to
# any artifact (values are masked to prefix + length).

import json
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

from playwright.sync_api import sync_playwright

GATEWAY = "http://127.0.0.1:8970"
FIXTURE = "http://127.0.0.1:8971"
OUT = Path(__file__).resolve().parent
CLOUD_KEY = "dtd_live_BROWSER_PROOF_KEY_1088"  # the fixed fixture key, for the log grep only


def mask(value: str) -> str:
    if not value:
        return "(empty)"
    return f"{value[:9]}... ({len(value)} characters, masked)"


def http(method: str, url: str, accept: str | None = None) -> dict:
    req = urllib.request.Request(url, method=method)
    if accept:
        req.add_header("Accept", accept)
    try:
        with urllib.request.urlopen(req) as res:
            return {"status": res.status, "location": res.headers.get("Location"), "body": res.read().decode("utf-8", "replace")}
    except urllib.error.HTTPError as err:
        return {"status": err.code, "location": err.headers.get("Location"), "body": err.read().decode("utf-8", "replace")}


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def http_no_redirect(url: str, accept: str | None = None) -> dict:
    opener = urllib.request.build_opener(NoRedirect)
    req = urllib.request.Request(url, method="GET")
    if accept:
        req.add_header("Accept", accept)
    try:
        with opener.open(req) as res:
            return {"status": res.status, "location": res.headers.get("Location")}
    except urllib.error.HTTPError as err:
        return {"status": err.code, "location": err.headers.get("Location")}


def main() -> int:
    transcript: dict = {"steps": []}

    # ---- Server-side HTTP transcript (acceptance criterion 1): the 3xx chain -----------------------
    root = http_no_redirect(f"{GATEWAY}/", accept="text/html")
    deep = http_no_redirect(f"{GATEWAY}/fleet?tab=map", accept="text/html")
    signin = http_no_redirect(f"{GATEWAY}/signin?next=%2Ffleet%3Ftab%3Dmap", accept="text/html")
    callback = http_no_redirect(f"{GATEWAY}/device-callback", accept="text/html")
    data401 = http_no_redirect(f"{GATEWAY}/sessions", accept="application/json")
    transcript["ac1_redirect_chain"] = {
        "GET / (no credential, Accept text/html)": root,
        "GET /fleet?tab=map (no credential, Accept text/html)": deep,
        "GET /signin (no credential)": signin,
        "GET /device-callback (no credential)": callback,
        "GET /sessions (no credential, JSON)": data401,
    }
    assert root["status"] == 302 and root["location"] == "/signin?next=%2F", root
    assert deep["status"] == 302 and deep["location"] == "/signin?next=" + urllib.parse.quote("/fleet?tab=map", safe=""), deep
    assert signin["status"] == 200, signin
    assert callback["status"] == 200, callback
    assert data401["status"] == 401, data401
    print("[proof] AC1 server transcript: 302 -> /signin (root + deep), /signin + /device-callback public, data 401")

    # ---- The real browser flow ----------------------------------------------------------------------
    nav_trail: list[str] = []
    with sync_playwright() as p:
        browser = p.chromium.launch()
        context = browser.new_context(viewport={"width": 1280, "height": 900})
        page = context.new_page()
        page.on("framenavigated", lambda frame: nav_trail.append(frame.url) if frame == page.main_frame else None)

        # 1. Signed-out navigation to a DEEP route -> the shared sign-in screen, with next= preserved.
        page.goto(f"{GATEWAY}/fleet?tab=map")
        page.wait_for_selector("text=Sign in to connect this device", timeout=15000)
        assert "/signin?next=" in page.url, page.url
        page.screenshot(path=str(OUT / "01-signed-out-redirected-to-signin.png"))
        transcript["ac1_browser_landed"] = page.url
        print(f"[proof] AC1 browser: landed on {page.url}")

        # 2. Sign in -> the activation page (the fixture standing in for devthrottle.com, issue #1081).
        page.click("button:has-text('Sign in')")
        page.wait_for_selector("text=Connect this device?", timeout=15000)
        activation_url = page.url
        assert activation_url.startswith(f"{FIXTURE}/m-activate"), activation_url
        # The request the site received: platform=browser, the Cockpit callback, a recognizable name.
        assert "platform=browser" in activation_url, activation_url
        assert urllib.parse.quote(f"{GATEWAY}/device-callback", safe="") in activation_url, activation_url
        page.screenshot(path=str(OUT / "02-activation-page-connect-this-device.png"))
        transcript["ac2_activation_request"] = re.sub(r"state=[^&]+", "state=(masked)", activation_url)
        print(f"[proof] AC2 activation request carries platform=browser and the /device-callback return path")

        # 3. Approve -> the fragment-only hand-back -> the shared callback enrolls -> the ORIGINAL route.
        page.click("a:has-text('Connect this device')")
        page.wait_for_url(f"{GATEWAY}/fleet?tab=map", timeout=20000)
        page.wait_for_load_state("networkidle")
        page.screenshot(path=str(OUT / "03-landed-on-originally-requested-route.png"))
        transcript["ac5_final_url"] = page.url
        print(f"[proof] AC5: landed back on the originally-requested route: {page.url}")

        # The callback URL shape (acceptance criterion 3): device key in the FRAGMENT only.
        callback_navs = [u for u in nav_trail if "/device-callback" in u]
        assert callback_navs, nav_trail
        cb = callback_navs[-1]
        assert "#device_key=" in cb, cb
        assert "?device_key=" not in cb.split("#")[0], cb
        transcript["ac3_callback_url_shape"] = re.sub(r"device_key=[^&#]+", "device_key=(fragment value, masked)", cb)
        transcript["ac3_note"] = "the key rides in the URL fragment only; the query string carries no key"
        print(f"[proof] AC3: callback shape fragment-only: {transcript['ac3_callback_url_shape']}")

        # 4. The stored credential (acceptance criterion 4): the shared client-core storage shape.
        device_key = page.evaluate("() => localStorage.getItem('cc.deviceKey') || ''")
        install_id = page.evaluate("() => localStorage.getItem('cc.installId') || ''")
        cookies = {c["name"]: c["value"] for c in context.cookies(f"{GATEWAY}/")}
        assert device_key, "no cc.deviceKey stored"
        assert device_key != CLOUD_KEY, "the LOCAL key must differ from the cloud key (the Gateway swaps it)"
        assert cookies.get("cc-gateway-token") == device_key, "cookie must mirror the device key"
        transcript["ac4_storage"] = {
            "localStorage 'cc.deviceKey' (the shared client-core store)": mask(device_key),
            "localStorage 'cc.installId'": install_id,
            "cookie 'cc-gateway-token' mirrors the device key": True,
            "local key differs from the cloud key (swapped at /m/enroll)": True,
        }

        # A normal Cockpit data request, authorized by the device key alone -> 200.
        status = page.evaluate(
            "async () => (await fetch('/sessions', {headers: {Accept: 'application/json', Authorization: 'Bearer ' + localStorage.getItem('cc.deviceKey')}})).status"
        )
        assert status == 200, status
        transcript["ac4_data_request"] = {"GET /sessions with Bearer <device key>": status}
        print(f"[proof] AC4: device key stored via shared client-core modules; GET /sessions -> {status}")

        # 5. The account roster (acceptance criterion 2): the browser row, non-phone device type.
        roster = json.loads(http("GET", f"{FIXTURE}/__control/roster")["body"])
        browser_rows = [r for r in roster["data"] if r.get("device_type") == "browser"]
        assert browser_rows, roster
        transcript["ac2_roster_row"] = browser_rows[0]
        print(f"[proof] AC2: roster contains the browser device: {browser_rows[0]['name']} ({browser_rows[0]['device_type']})")

        # 6. Revoke round trip (acceptance criterion 6): remove the device on the roster, run the real
        #    reconcile, and watch the LIVE page bounce back to the shared sign-in flow on its next poll.
        http("POST", f"{FIXTURE}/__control/revoke")
        reconcile = json.loads(http("POST", f"{FIXTURE}/__control/reconcile")["body"])
        transcript["ac6_reconcile"] = reconcile
        page.wait_for_url(lambda url: "/signin" in url, timeout=30000)
        page.wait_for_selector("text=Sign in to connect this device", timeout=15000)
        page.screenshot(path=str(OUT / "04-after-revoke-back-at-signin.png"))
        key_after = page.evaluate("() => localStorage.getItem('cc.deviceKey') || ''")
        assert key_after == "", "the revoked key must be cleared from storage"
        assert "/signin" in page.url and "/login" not in page.url, page.url
        transcript["ac6_after_revoke"] = {
            "page returned to": re.sub(r"next=[^&]+", "next=(url-encoded original route)", page.url),
            "cc.deviceKey cleared": True,
            "not the token wall (/login)": True,
        }
        print(f"[proof] AC6: after revoke + reconcile the live page returned to {page.url}")

        # The same request that was 200 is now 401 (server half of the revoke round trip).
        relogin_status = None
        req = urllib.request.Request(f"{GATEWAY}/sessions", method="GET")
        req.add_header("Accept", "application/json")
        req.add_header("Authorization", f"Bearer {device_key}")
        try:
            with urllib.request.urlopen(req) as res:
                relogin_status = res.status
        except urllib.error.HTTPError as err:
            relogin_status = err.code
        assert relogin_status == 401, relogin_status
        transcript["ac6_same_key_after_revoke"] = {"GET /sessions with the revoked key": relogin_status}
        print(f"[proof] AC6: the revoked key now answers {relogin_status}")

        browser.close()

    # ---- Log grep (acceptance criterion 3, second half): no key value in any Gateway log ------------
    logs_root = Path.home() / "AppData" / "Local" / "cc-director" / "logs"
    hits = 0
    scanned = 0
    cutoff = time.time() - 30 * 60
    for log_file in logs_root.rglob("*.log"):
        try:
            if log_file.stat().st_mtime < cutoff:
                continue
            text = log_file.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        scanned += 1
        if CLOUD_KEY in text or (device_key and device_key in text):
            hits += 1
            print(f"[proof] LOG LEAK in {log_file}")
    transcript["ac3_log_grep"] = {
        "logs scanned (modified in the last 30 minutes)": scanned,
        "files containing the cloud device key": hits if hits else 0,
        "files containing the issued local device key": hits if hits else 0,
        "result": "PASS - no device-key value in any log" if hits == 0 else "FAIL",
    }
    assert hits == 0, "a device key leaked into a log"
    print(f"[proof] AC3 log grep: scanned {scanned} recent log file(s), zero device-key hits")

    (OUT / "live-proof-transcript.json").write_text(json.dumps(transcript, indent=2), encoding="utf-8")
    print("[proof] DONE - artifacts written to " + str(OUT))
    return 0


if __name__ == "__main__":
    sys.exit(main())
