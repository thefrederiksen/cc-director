# TriSight & cc-click Test Plan

Test output directory: `test_output/`

---

## Prerequisites

1. Open Notepad with some text:
   ```
   notepad.exe
   ```
   Type: "Hello World - Test Document"

2. Create test output directory:
   ```bash
   mkdir -p test_output
   ```

---

## Part 1: cc-click Tests

### Test 1.1: List Windows
**Command:**
```bash
"../cc-click/src/CcClick/bin/Release/net10.0-windows/CcClick.exe" list-windows
```
**Expected:** JSON array of windows including "Notepad" or "Untitled - Notepad"
**Validation:** Find Notepad in the output

---

### Test 1.2: List Elements in Notepad
**Command:**
```bash
"../cc-click/src/CcClick/bin/Release/net10.0-windows/CcClick.exe" list-elements -w "Notepad"
```
**Expected:** JSON array of UI elements (menu items, text area, etc.)
**Validation:** Should contain elements like "File", "Edit", "View", "Help" menus

---

### Test 1.3: Screenshot Notepad
**Command:**
```bash
"../cc-click/src/CcClick/bin/Release/net10.0-windows/CcClick.exe" screenshot -w "Notepad" -o "test_output/01_notepad_screenshot.png"
```
**Expected:** Screenshot saved to test_output folder
**Validation:** Open image - should show Notepad window with "Hello World - Test Document"

---

### Test 1.4: Read Text from Element
**Command:**
```bash
"../cc-click/src/CcClick/bin/Release/net10.0-windows/CcClick.exe" read-text -w "Notepad" --name "Text editor"
```
**Expected:** JSON with text content "Hello World - Test Document"
**Validation:** Text matches what was typed

---

### Test 1.5: Click Menu Item
**Command:**
```bash
"../cc-click/src/CcClick/bin/Release/net10.0-windows/CcClick.exe" click -w "Notepad" --name "Edit"
```
**Expected:** Edit menu opens
**Validation:** Take screenshot after to verify menu is open

---

### Test 1.6: Type Text
**Command:** (First click in text area, then type)
```bash
"../cc-click/src/CcClick/bin/Release/net10.0-windows/CcClick.exe" click -w "Notepad" --name "Text editor"
"../cc-click/src/CcClick/bin/Release/net10.0-windows/CcClick.exe" type -w "Notepad" --text " - Additional text added by cc-click"
```
**Expected:** Text appended to Notepad
**Validation:** Screenshot shows new text

---

## Part 2: TrisightCli Tests

### Test 2.1: UIA Detection Only
**Command:**
```bash
"TrisightCli/bin/Release/net10.0-windows10.0.17763.0/TrisightCli.exe" uia --window "Notepad"
```
**Expected:** JSON with UIA elements from Notepad
**Validation:** Contains window elements with bounding boxes

---

### Test 2.2: OCR Detection
**Command:** (requires screenshot first)
```bash
"../cc-click/src/CcClick/bin/Release/net10.0-windows/CcClick.exe" screenshot -w "Notepad" -o "test_output/02_for_ocr.png"

"TrisightCli/bin/Release/net10.0-windows10.0.17763.0/TrisightCli.exe" ocr --screenshot "test_output/02_for_ocr.png"
```
**Expected:** JSON with detected text regions
**Validation:** Should detect "Hello World", "File", "Edit", etc.

---

### Test 2.3: Full Detection Pipeline
**Command:**
```bash
"../cc-click/src/CcClick/bin/Release/net10.0-windows/CcClick.exe" screenshot -w "Notepad" -o "test_output/03_for_detect.png"

"TrisightCli/bin/Release/net10.0-windows10.0.17763.0/TrisightCli.exe" detect --window "Notepad" --screenshot "test_output/03_for_detect.png"
```
**Expected:** JSON with fused elements from UIA + OCR
**Validation:** Element count should be higher than UIA-only

---

### Test 2.4: Detection with Annotation (KEY TEST)
**Command:**
```bash
"../cc-click/src/CcClick/bin/Release/net10.0-windows/CcClick.exe" screenshot -w "Notepad" -o "test_output/04_raw.png"

"TrisightCli/bin/Release/net10.0-windows10.0.17763.0/TrisightCli.exe" detect --window "Notepad" --screenshot "test_output/04_raw.png" --annotate --output "test_output/04_annotated.png"
```
**Expected:**
- JSON output with element details
- Annotated screenshot with numbered bounding boxes
**Validation:** Open `04_annotated.png` - should show colored boxes with numbers on each UI element

---

### Test 2.5: Annotate Existing Screenshot
**Command:**
```bash
"TrisightCli/bin/Release/net10.0-windows10.0.17763.0/TrisightCli.exe" annotate --screenshot "test_output/04_raw.png" --window "Notepad" --output "test_output/05_annotated_v2.png"
```
**Expected:** New annotated screenshot created
**Validation:** Compare with 04_annotated.png - should be similar

---

### Test 2.6: UIA-Only Detection (No OCR/Pixel)
**Command:**
```bash
"TrisightCli/bin/Release/net10.0-windows10.0.17763.0/TrisightCli.exe" detect --window "Notepad" --screenshot "test_output/04_raw.png" --tiers uia --annotate --output "test_output/06_uia_only.png"
```
**Expected:** Annotated screenshot with only UIA elements
**Validation:** Compare element count - should be less than full detection

---

## Part 3: Browser Window Tests (Edge/Chrome)

### Test 3.1: Screenshot Browser
**Command:**
```bash
"../cc-click/src/CcClick/bin/Release/net10.0-windows/CcClick.exe" screenshot -w "Edge" -o "test_output/07_browser.png"
```
**Expected:** Browser window screenshot
**Validation:** Image shows browser content

---

### Test 3.2: Detect Browser Elements
**Command:**
```bash
"TrisightCli/bin/Release/net10.0-windows10.0.17763.0/TrisightCli.exe" detect --window "Edge" --screenshot "test_output/07_browser.png" --annotate --output "test_output/07_browser_annotated.png"
```
**Expected:** Annotated browser screenshot
**Validation:** OCR should detect text that UIA misses in web content

---

## Part 4: Calculator Test (Complex UI)

### Test 4.1: Open Calculator and Detect
**Prereq:** Open Windows Calculator (calc.exe)
**Command:**
```bash
"../cc-click/src/CcClick/bin/Release/net10.0-windows/CcClick.exe" screenshot -w "Calculator" -o "test_output/08_calc.png"

"TrisightCli/bin/Release/net10.0-windows10.0.17763.0/TrisightCli.exe" detect --window "Calculator" --screenshot "test_output/08_calc.png" --annotate --output "test_output/08_calc_annotated.png"
```
**Expected:** All calculator buttons detected and numbered
**Validation:** Each button (0-9, +, -, *, /, =) should have a bounding box

---

### Test 4.2: Click Calculator Button
**Command:**
```bash
"../cc-click/src/CcClick/bin/Release/net10.0-windows/CcClick.exe" click -w "Calculator" --name "Seven"
"../cc-click/src/CcClick/bin/Release/net10.0-windows/CcClick.exe" click -w "Calculator" --name "Plus"
"../cc-click/src/CcClick/bin/Release/net10.0-windows/CcClick.exe" click -w "Calculator" --name "Three"
"../cc-click/src/CcClick/bin/Release/net10.0-windows/CcClick.exe" click -w "Calculator" --name "Equals"
"../cc-click/src/CcClick/bin/Release/net10.0-windows/CcClick.exe" screenshot -w "Calculator" -o "test_output/09_calc_result.png"
```
**Expected:** Calculator shows "10"
**Validation:** Screenshot shows 7+3=10

---

## Test Results Summary

| Test | Description | Pass/Fail | Notes |
|------|-------------|-----------|-------|
| 1.1 | List Windows | | |
| 1.2 | List Elements | | |
| 1.3 | Screenshot Notepad | | |
| 1.4 | Read Text | | |
| 1.5 | Click Menu | | |
| 1.6 | Type Text | | |
| 2.1 | UIA Detection | | |
| 2.2 | OCR Detection | | |
| 2.3 | Full Detection | | |
| 2.4 | Detection + Annotate | | |
| 2.5 | Annotate Existing | | |
| 2.6 | UIA-Only Tiers | | |
| 3.1 | Browser Screenshot | | |
| 3.2 | Browser Detect | | |
| 4.1 | Calculator Detect | | |
| 4.2 | Calculator Click | | |

---

## Output Files to Review

After running all tests, check `test_output/` for:
- `01_notepad_screenshot.png` - Basic Notepad capture
- `02_for_ocr.png` - OCR input image
- `03_for_detect.png` - Detection input
- `04_raw.png` - Raw screenshot
- `04_annotated.png` - Annotated with numbered boxes (KEY)
- `05_annotated_v2.png` - Second annotation pass
- `06_uia_only.png` - UIA-only detection
- `07_browser.png` - Browser capture
- `07_browser_annotated.png` - Browser with annotations
- `08_calc.png` - Calculator raw
- `08_calc_annotated.png` - Calculator annotated (KEY)
- `09_calc_result.png` - Calculator after 7+3=10
