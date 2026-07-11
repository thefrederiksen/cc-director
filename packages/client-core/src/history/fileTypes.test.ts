import { describe, it, expect } from "vitest";
import { classifyFile, fileExtension } from "./fileTypes";

// Local Files mission (Phase 1): classifyFile maps a detected absolute path to exactly one viewer
// type by extension. Full coverage is formally Phase 4; this basic set locks the contract from the
// brief's decision 4 (image / pdf / html / markdown / text / download) so a later change cannot
// silently reclassify a type.

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

  it("classifies a pdf", () => {
    expect(classifyFile("D:\\reports\\Q3.pdf")).toBe("pdf");
  });

  it.each([
    ["D:\\site\\index.html", "html"],
    ["D:\\site\\page.htm", "html"],
  ])("classifies %s as html", (path, expected) => {
    expect(classifyFile(path)).toBe(expected);
  });

  it.each([
    ["D:\\docs\\README.md", "markdown"],
    ["D:\\docs\\notes.markdown", "markdown"],
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
  ])("classifies bare textual filename %s as text", (path, expected) => {
    expect(classifyFile(path)).toBe(expected);
  });

  it("treats a dotfile like .gitignore as having no extension (falls to basename rule)", () => {
    expect(classifyFile("D:\\repo\\.gitignore")).toBe("text");
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
