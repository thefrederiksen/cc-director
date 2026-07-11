import { describe, expect, it } from "vitest";
import {
  compareSortValues,
  filterAndSortRows,
  matchesQuery,
  nextSort,
  type SortState,
} from "./dataTableCore";

interface Row {
  id: string;
  name: string;
  next: number;
}

const rows: Row[] = [
  { id: "a", name: "Alpha", next: 300 },
  { id: "b", name: "bravo", next: 100 },
  { id: "c", name: "Charlie", next: 200 },
];

const searchableText = (row: Row) => `${row.name} ${row.id}`;
const sortValueFor = (row: Row, columnKey: string): string | number | null => {
  if (columnKey === "name") return row.name.toLowerCase();
  if (columnKey === "next") return row.next;
  return null;
};

describe("matchesQuery", () => {
  it("matches every row on an empty query", () => {
    expect(matchesQuery("anything", "")).toBe(true);
    expect(matchesQuery("anything", "   ")).toBe(true);
  });

  it("matches case-insensitively on a substring", () => {
    expect(matchesQuery("SOREN_NORTH devthrottle", "north")).toBe(true);
  });

  it("does not match when the substring is absent", () => {
    expect(matchesQuery("SOREN_NORTH", "laptop")).toBe(false);
  });
});

describe("compareSortValues", () => {
  it("orders numbers numerically ascending", () => {
    expect(compareSortValues(100, 300, "asc")).toBeLessThan(0);
  });

  it("flips the order for descending", () => {
    expect(compareSortValues(100, 300, "desc")).toBeGreaterThan(0);
  });

  it("orders strings case-insensitively", () => {
    expect(compareSortValues("bravo", "Charlie", "asc")).toBeLessThan(0);
  });
});

describe("filterAndSortRows", () => {
  it("sorts by a numeric column ascending (soonest first)", () => {
    const sort: SortState = { columnKey: "next", direction: "asc" };
    const result = filterAndSortRows(rows, "", searchableText, sort, sortValueFor);
    expect(result.map((r) => r.id)).toEqual(["b", "c", "a"]);
  });

  it("sorts by a string column ascending, ignoring case", () => {
    const sort: SortState = { columnKey: "name", direction: "asc" };
    const result = filterAndSortRows(rows, "", searchableText, sort, sortValueFor);
    expect(result.map((r) => r.id)).toEqual(["a", "b", "c"]);
  });

  it("filters by the search query before sorting", () => {
    const sort: SortState = { columnKey: "next", direction: "asc" };
    const result = filterAndSortRows(rows, "bravo", searchableText, sort, sortValueFor);
    expect(result.map((r) => r.id)).toEqual(["b"]);
  });

  it("leaves the filtered order untouched when the active column is not sortable", () => {
    const sort: SortState = { columnKey: "unknown", direction: "asc" };
    const result = filterAndSortRows(rows, "", searchableText, sort, sortValueFor);
    expect(result.map((r) => r.id)).toEqual(["a", "b", "c"]);
  });

  it("is stable: equal rows keep their input order", () => {
    const tied = [
      { id: "x", name: "same", next: 1 },
      { id: "y", name: "same", next: 1 },
      { id: "z", name: "same", next: 1 },
    ];
    const sort: SortState = { columnKey: "name", direction: "asc" };
    const result = filterAndSortRows(tied, "", searchableText, sort, sortValueFor);
    expect(result.map((r) => r.id)).toEqual(["x", "y", "z"]);
  });
});

describe("nextSort", () => {
  it("sorts a new column ascending", () => {
    expect(nextSort(null, "name")).toEqual({ columnKey: "name", direction: "asc" });
  });

  it("flips the direction of the already-sorted column", () => {
    expect(nextSort({ columnKey: "name", direction: "asc" }, "name")).toEqual({
      columnKey: "name",
      direction: "desc",
    });
  });

  it("switches to a different column ascending", () => {
    expect(nextSort({ columnKey: "name", direction: "desc" }, "next")).toEqual({
      columnKey: "next",
      direction: "asc",
    });
  });
});
