import { describe, it, expect } from "vitest";
import { classifyFile, fileExtension, formatFileSize } from "./fileTypes";

// Local Files mission (Phase 1, hardened in Phase 4): classifyFile maps a detected absolute path to
// exactly one viewer type by extension. This set locks the contract from the brief's decision 4
// (image / pdf / html / markdown / text / download) so a later change cannot silently reclassify a
// type - every viewer type, case-insensitivity, extensionless textual basenames, and unknown ->
// download are all covered.

describe("classifyFile", () => {
  it.each([
    ["C:\\shots\\out.png", "image"],
    ["C:\\shots\\out.PNG", "image"],
    ["/c/shots/photo.jpg", "image"],
    ["D:\\a\\b.jpeg", "image"],
    ["D:\\a\\b.gif", "image"],
    ["D:\\a\\b.svg", "image"],
    ["D:\\a\\b.webp", "image"],
    ["D:\\a\\b.bmp", "image"],
  ])("classifies %s as image", (path, expected) => {
    expect(classifyFile(path)).toBe(expected);
  });

  it.each([
    ["D:\\reports\\Q3.pdf", "pdf"],
    ["D:\\reports\\Q3.PDF", "pdf"],
  ])("classifies %s as pdf", (path, expected) => {
    expect(classifyFile(path)).toBe(expected);
  });

  it.each([
    ["D:\\site\\index.html", "html"],
    ["D:\\site\\page.htm", "html"],
    ["D:\\site\\INDEX.HTML", "html"],
    ["D:\\site\\PAGE.HTM", "html"],
  ])("classifies %s as html", (path, expected) => {
    expect(classifyFile(path)).toBe(expected);
  });

  it.each([
    ["D:\\docs\\README.md", "markdown"],
    ["D:\\docs\\notes.markdown", "markdown"],
    ["D:\\docs\\NOTES.MARKDOWN", "markdown"],
    ["/c/docs/spec.MD", "markdown"],
  ])("classifies %s as markdown", (path, expected) => {
    expect(classifyFile(path)).toBe(expected);
  });

  it.each([
    ["D:\\logs\\run.log", "text"],
    ["D:\\a\\b.txt", "text"],
    ["D:\\a\\data.json", "text"],
    ["D:\\a\\data.csv", "text"],
    ["D:\\src\\app.ts", "text"],
    ["D:\\src\\app.py", "text"],
    ["D:\\src\\Program.cs", "text"],
    ["D:\\src\\build.gradle", "text"],
    ["D:\\src\\config.yaml", "text"],
    ["D:\\src\\style.css", "text"],
  ])("classifies %s as text", (path, expected) => {
    expect(classifyFile(path)).toBe(expected);
  });

  it.each([
    ["D:\\bin\\app.exe", "download"],
    ["D:\\data\\archive.zip", "download"],
    ["D:\\media\\clip.mp4", "download"],
    ["D:\\data\\model.bin", "download"],
    ["D:\\no-extension-here", "download"],
  ])("classifies unknown/binary %s as download", (path, expected) => {
    expect(classifyFile(path)).toBe(expected);
  });

  it.each([
    ["D:\\repo\\Dockerfile", "text"],
    ["D:\\repo\\Makefile", "text"],
    ["D:\\repo\\MAKEFILE", "text"],
    ["/c/repo/dockerfile", "text"],
    ["D:\\repo\\README", "text"],
    ["D:\\repo\\LICENSE", "text"],
    ["D:\\repo\\CHANGELOG", "text"],
  ])("classifies bare textual filename %s as text (case-insensitive)", (path, expected) => {
    expect(classifyFile(path)).toBe(expected);
  });

  it.each([
    ["D:\\repo\\.gitignore", "text"],
    ["D:\\repo\\.editorconfig", "text"],
  ])("treats dotfile %s as extensionless and applies the basename rule", (path, expected) => {
    expect(classifyFile(path)).toBe(expected);
  });

  it("classifies a bare unknown extensionless name as download (not guessed as text)", () => {
    expect(classifyFile("D:\\bin\\mysteryblob")).toBe("download");
  });
});

// formatFileSize (Phase 4): the human-readable size shown in the download panel. Binary units, bytes
// whole, KB and up with one decimal; a non-finite/negative value yields "" so the panel shows the name
// with NO size rather than a fake one.
describe("formatFileSize", () => {
  it.each([
    [0, "0 B"],
    [1, "1 B"],
    [820, "820 B"],
    [1023, "1023 B"],
    [1024, "1.0 KB"],
    [15360, "15.0 KB"],
    [1_048_576, "1.0 MB"],
    [1_500_000, "1.4 MB"],
    [1_073_741_824, "1.0 GB"],
    [1_099_511_627_776, "1.0 TB"],
  ])("formats %d bytes as %s", (bytes, expected) => {
    expect(formatFileSize(bytes)).toBe(expected);
  });

  it.each([
    [-1],
    [Number.NaN],
    [Number.POSITIVE_INFINITY],
  ])("returns an empty string for the non-representable value %d", (bytes) => {
    expect(formatFileSize(bytes)).toBe("");
  });
});

describe("fileExtension", () => {
  it.each([
    ["C:\\a\\b.PNG", "png"],
    ["/c/a/b.tar.gz", "gz"],
    ["D:\\dir.with.dot\\file", ""],
    ["D:\\repo\\.gitignore", ""],
    ["", ""],
  ])("extracts the extension of %s", (path, expected) => {
    expect(fileExtension(path)).toBe(expected);
  });
});
